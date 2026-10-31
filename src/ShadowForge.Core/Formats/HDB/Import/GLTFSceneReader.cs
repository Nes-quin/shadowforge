using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SharpGLTF.Schema2;
using ShadowForge.Formats.DDS;
using ShadowForge.Formats.GLTF;
using ShadowForge.Formats.HDB.Wire;

namespace ShadowForge.Formats.HDB.Import;

/// <summary>
/// Reads a glTF into an ImportScene in world space. VertexPacker later encodes vertices
/// against the same runtime-composed bone transforms that FileBuilder writes.
/// </summary>
public static class GLTFSceneReader
{
    public static ImportScene Read(string path, ILogger log, GraphicFormat? formatOverride)
    {
        var gltf = ModelRoot.Load(path);
        var scene = new ImportScene();
        string sourceDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        ReadSkeleton(gltf, scene, log);
        RuntimeMath.ComputeWorldTransforms(scene.Bones);
        ReadTextures(gltf, scene, formatOverride, log);
        ReadGeometry(gltf, scene, formatOverride, sourceDir, log);
        ComputeTangents(scene);

        return scene;
    }

    /// <summary>
    /// Texture indices of one staged (eye) material: Stage0 is the face texture the
    /// runtime layers stage 1 over, Stage1 the iris (the material's own baseColor), and
    /// Stage2 the eyelid overlay or -1.
    /// </summary>
    private readonly record struct StagedBinding(int Stage0, int Stage1, int Stage2);

    /// <summary>
    /// The first skin's joint names in joint order, named as the imported bones are.
    /// </summary>
    public static List<string> JointNames(ModelRoot gltf)
    {
        var skin = gltf.LogicalSkins.FirstOrDefault();
        var names = new List<string>();
        for (int i = 0; i < (skin?.JointsCount ?? 0); i++)
            names.Add(skin!.GetJoint(i).Joint.Name ?? $"joint{i}");
        return names;
    }

    private static void ReadSkeleton(ModelRoot gltf, ImportScene scene, ILogger log)
    {
        var skin = gltf.LogicalSkins.FirstOrDefault();
        if (skin == null || skin.JointsCount == 0)
        {
            log.LogInformation("glTF has no skin; emitting a single root bone (rigid model).");
            scene.Bones.Add(new ImportBone { Index = 0, Name = "root" });
            return;
        }

        int n = skin.JointsCount;
        var nodes = new Node[n];
        var nodeToJoint = new Dictionary<Node, int>(n);
        for (int i = 0; i < n; i++)
        {
            nodes[i] = skin.GetJoint(i).Joint;
            nodeToJoint[nodes[i]] = i;
        }

        for (int i = 0; i < n; i++)
        {
            var node = nodes[i];
            var t = node.LocalTransform;
            int parent = node.VisualParent != null && nodeToJoint.TryGetValue(node.VisualParent, out int p) ? p : -1;
            scene.Bones.Add(new ImportBone
            {
                Index = i,
                Name = node.Name ?? $"joint{i}",
                ParentIndex = parent,
                Translation = t.Translation,
                Rotation = t.Rotation,
                Scale = t.Scale,
            });
        }
        scene.Skinned = true;
        log.LogInformation("Skeleton: {Count} joints, {Roots} root(s).",
            n, scene.Bones.Count(b => b.ParentIndex < 0));
    }

    private static void ReadTextures(ModelRoot gltf, ImportScene scene, GraphicFormat? formatOverride, ILogger log)
    {
        var seen = new HashSet<Image>();
        foreach (var mat in gltf.LogicalMaterials)
        {
            Emit(mat, "BaseColor", false);
            Emit(mat, "Normal", true);
        }

        void Emit(Material mat, string channelName, bool normalMap)
        {
            var img = mat.FindChannel(channelName)?.Texture?.PrimaryImage;
            if (img == null || !seen.Add(img)) return;

            if (!normalMap && VolumeTextureName(mat) is { } volumeFile)
            {
                scene.Textures.Add(new ImportTexture
                {
                    Name = TextureName(img, mat, Path.GetFileNameWithoutExtension(volumeFile)),
                });
                log.LogWarning(
                    "Material '{Mat}' samples the 3D texture '{File}'; its glTF image is a slice-0 "
                    + "preview and is not cooked back. The binding is kept by name and the runtime "
                    + "loads the '{File}' that ships with the rig. To change the fur, edit the "
                    + "slices with 'sforge dds export' and rebuild with 'sforge dds import-volume'.",
                    mat.Name, volumeFile, volumeFile);
                return;
            }

            byte[] dds = EncodeDDS(img.Content.Content.ToArray(), formatOverride, log);
            scene.Textures.Add(new ImportTexture
            {
                Name = TextureName(img, mat, $"tex{scene.Textures.Count}"),
                DDSBytes = dds,
                IsNormalMap = normalMap,
            });
        }
    }

    /// <summary>
    /// The .36t file named by the material's sfVolumeTexture extra, or null for a 2D material.
    /// </summary>
    private static string? VolumeTextureName(Material mat)
    {
        if (mat.Extras is not JsonObject extras || !extras.ContainsKey("sfVolumeTexture"))
            return null;
        string? name = (string?)extras["sfVolumeTexture"];
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Resolves the staged (eye) materials, those whose extras carry sfStage0Texture. The
    /// material's baseColor image is the stage-1 texture, already added by
    /// <see cref="ReadTextures"/>. Stage 0 resolves through sfStage0Texture to a body
    /// texture, and stage 2 through sfStage2Texture or a .dds beside the glTF. Every
    /// unresolved reference is logged as a warning.
    /// </summary>
    private static Dictionary<Material, StagedBinding> ResolveStagedMaterials(
        ModelRoot gltf, ImportScene scene, GraphicFormat? formatOverride, string sourceDir, ILogger log)
    {
        var result = new Dictionary<Material, StagedBinding>();
        foreach (var mat in gltf.LogicalMaterials)
        {
            if (mat.Extras is not JsonObject extras || !extras.ContainsKey("sfStage0Texture"))
                continue;

            string eyeName = Sanitize(!string.IsNullOrEmpty(mat.Name) ? mat.Name : "eye");
            int stage1 = FindColorTexture(scene, eyeName);
            if (stage1 < 0)
            {
                var eyeImg = mat.FindChannel("BaseColor")?.Texture?.PrimaryImage;
                if (eyeImg != null)
                    stage1 = AddTexture(scene, eyeName, eyeImg.Content.Content.ToArray(), formatOverride);
                else
                    log.LogWarning(
                        "Staged material '{Mat}': no stage-1 (eye) image in glTF; eye texture unresolved.",
                        mat.Name);
            }

            string? stage0Name = (string?)extras["sfStage0Texture"];
            int stage0 = 0;
            if (!string.IsNullOrEmpty(stage0Name))
            {
                string s0 = Sanitize(stage0Name);
                stage0 = FindColorTexture(scene, s0);
                if (stage0 < 0)
                {
                    log.LogWarning(
                        "Staged material '{Mat}': sfStage0Texture '{Name}' does not resolve to a "
                        + "texture; drawing face texture 0.", mat.Name, stage0Name);
                    stage0 = 0;
                }
            }
            else
            {
                log.LogWarning("Staged material '{Mat}': sfStage0Texture missing or empty.", mat.Name);
            }

            int stage2 = -1;
            JsonNode? s2node = extras.ContainsKey("sfStage2Texture") ? extras["sfStage2Texture"] : null;
            string? stage2Name = s2node is null ? null : (string?)s2node;
            if (!string.IsNullOrEmpty(stage2Name))
            {
                string s2 = Sanitize(stage2Name);
                stage2 = FindColorTexture(scene, s2);
                if (stage2 < 0)
                {
                    var dds = TextureResolver.FindDDS(stage2Name, sourceDir);
                    if (dds != null)
                        stage2 = AddTexture(scene, s2, Converter.ConvertToPngBytes(dds), formatOverride);
                    else
                    {
                        scene.Textures.Add(new ImportTexture { Name = s2 });
                        stage2 = scene.Textures.Count - 1;
                        log.LogWarning(
                            "Staged material '{Mat}': sfStage2Texture '{Name}' has no image in the glTF "
                            + "and no '{DDSFile}' beside it; kept the binding by name only. "
                            + "'{DDSFile}' must ship alongside the cooked model.",
                            mat.Name, stage2Name, stage2Name + ".dds", stage2Name + ".dds");
                    }
                }
            }

            result[mat] = new StagedBinding(stage0, stage1, stage2);
        }
        return result;
    }

    /// <summary>
    /// Adds a color texture under <paramref name="name"/> unless one exists, and returns
    /// its index.
    /// </summary>
    private static int AddTexture(ImportScene scene, string name, byte[] imageBytes,
        GraphicFormat? formatOverride)
    {
        int existing = FindColorTexture(scene, name);
        if (existing >= 0) return existing;

        scene.Textures.Add(new ImportTexture { Name = name, DDSBytes = EncodeDDS(imageBytes, formatOverride, log: null) });
        return scene.Textures.Count - 1;
    }

    private static int FindColorTexture(ImportScene scene, string name)
        => scene.Textures.FindIndex(t => t.Name == name && !t.IsNormalMap);

    /// <summary>
    /// Sanitized image name, else material name, else <paramref name="fallback"/>.
    /// </summary>
    private static string TextureName(Image img, Material mat, string fallback)
        => Sanitize(!string.IsNullOrEmpty(img.Name) ? img.Name
            : !string.IsNullOrEmpty(mat.Name) ? mat.Name : fallback);

    /// <summary>
    /// Decodes an image, upsizes it to power-of-two dimensions, and encodes DXT5 when any
    /// pixel is not opaque, otherwise DXT1, unless <paramref name="formatOverride"/> is set.
    /// </summary>
    private static byte[] EncodeDDS(byte[] imageBytes, GraphicFormat? formatOverride, ILogger? log)
    {
        var (w, h, rgba) = DecodeToRgba(imageBytes);
        if (!BitOperations.IsPow2(w) || !BitOperations.IsPow2(h))
        {
            int nw = (int)BitOperations.RoundUpToPowerOf2((uint)w);
            int nh = (int)BitOperations.RoundUpToPowerOf2((uint)h);
            log?.LogInformation("Resizing texture {W}x{H} -> {NW}x{NH}.", w, h, nw, nh);
            rgba = Resize(rgba, w, h, nw, nh);
            w = nw;
            h = nh;
        }
        var format = formatOverride
            ?? (HasAlpha(rgba) ? GraphicFormat.TextureFormatDXT5 : GraphicFormat.TextureFormatDXT1);
        return Importer.ImportFromRgba(rgba, w, h, format);
    }

    private static void ReadGeometry(ModelRoot gltf, ImportScene scene,
        GraphicFormat? formatOverride, string sourceDir, ILogger log)
    {
        var staged = ResolveStagedMaterials(gltf, scene, formatOverride, sourceDir, log);

        var matToTexIndex = new Dictionary<Material, int>();
        foreach (var mat in gltf.LogicalMaterials)
        {
            var img = mat.FindChannel("BaseColor")?.Texture?.PrimaryImage;
            if (img == null) continue;
            int idx = FindColorTexture(scene, TextureName(img, mat, ""));
            if (idx >= 0) matToTexIndex[mat] = idx;
        }

        int overweighted = 0;
        float maxDroppedWeight = 0f;

        foreach (var node in gltf.LogicalNodes)
        {
            var mesh = node.Mesh;
            if (mesh == null) continue;
            var meshWorld = node.WorldMatrix;

            foreach (var prim in mesh.Primitives)
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                    throw new InvalidDataException($"Primitive mode {prim.DrawPrimitiveType} unsupported; triangulate on export.");

                int baseVertex = scene.Vertices.Count;

                StagedBinding? bind = prim.Material != null
                    && staged.TryGetValue(prim.Material, out var b) ? b : null;
                int material, stage1, stage2;
                if (bind is { } sb)
                {
                    material = sb.Stage0;
                    stage1 = sb.Stage1;
                    stage2 = sb.Stage2;
                }
                else
                {
                    material = prim.Material != null
                        && matToTexIndex.TryGetValue(prim.Material, out int mi) ? mi : 0;
                    stage1 = -1;
                    stage2 = -1;
                }

                var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array()
                    ?? throw new InvalidDataException("Primitive missing POSITION.");
                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uvs = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

                var uvsEye = prim.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
                var uvsEyelid = prim.GetVertexAccessor("TEXCOORD_2")?.AsVector2Array();
                var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                bool skinnedPrim = scene.Skinned && joints != null && weights != null;

                for (int i = 0; i < positions.Count; i++)
                {
                    if (skinnedPrim)
                    {
                        var w = weights![i];
                        if (w.X > 1e-4f && w.Y > 1e-4f && w.Z > 1e-4f && w.W > 1e-4f)
                        {
                            overweighted++;
                            float smallest = MathF.Min(MathF.Min(w.X, w.Y), MathF.Min(w.Z, w.W));
                            if (smallest > maxDroppedWeight) maxDroppedWeight = smallest;
                        }
                    }
                    var v = new ImportVertex
                    {
                        WorldPosition = Vector3.Transform(positions[i], meshWorld),
                        WorldNormal = normals != null
                            ? Vector3.Normalize(Vector3.TransformNormal(normals[i], meshWorld))
                            : Vector3.UnitY,
                        UV = uvs?[i] ?? Vector2.Zero,
                        UVEye = uvsEye?[i] ?? Vector2.Zero,
                        UVEyelid = uvsEyelid?[i] ?? Vector2.Zero,
                        Influences = skinnedPrim
                            ? PickInfluences(joints![i], weights![i], scene.Bones.Count)
                            : Array.Empty<ImportInfluence>(),
                    };
                    scene.Vertices.Add(v);
                }

                var indices = prim.GetIndices();
                for (int t = 0; t + 2 < indices.Count; t += 3)
                {
                    scene.Triangles.Add(new ImportTriangle
                    {
                        A = baseVertex + (int)indices[t],
                        B = baseVertex + (int)indices[t + 1],
                        C = baseVertex + (int)indices[t + 2],
                        MaterialIndex = material,
                        Stage1Index = stage1,
                        Stage2Index = stage2,
                    });
                }
            }
        }

        log.LogInformation("Geometry: {V} vertices, {T} triangles, {M} textures.",
            scene.Vertices.Count, scene.Triangles.Count, scene.Textures.Count);

        if (overweighted > 0)
            log.LogWarning(
                "{Count} vertex(es) use more than 3 bone influences; the HDB skinned vertex holds 3, "
                + "so the smallest is dropped and the rest renormalized (max dropped weight {Weight:F3}). "
                + "Bind pose is unaffected, but these vertices will deform slightly under animation. "
                + "Limit weights to 3 bones per vertex in Blender (Weight Paint > Weights > Limit Total) for exact skinning.",
                overweighted, maxDroppedWeight);
    }

    /// <summary>
    /// Keeps the three heaviest influences, the most a vertex record holds, and
    /// renormalizes their weights.
    /// </summary>
    private static ImportInfluence[] PickInfluences(Vector4 joints, Vector4 weights, int boneCount)
    {
        Span<(int J, float W)> raw = stackalloc (int, float)[4]
        {
            ((int)joints.X, weights.X), ((int)joints.Y, weights.Y),
            ((int)joints.Z, weights.Z), ((int)joints.W, weights.W),
        };
        var picked = new List<(int J, float W)>(3);
        for (int k = 0; k < 3; k++)
        {
            int best = -1;
            for (int i = 0; i < 4; i++)
                if (raw[i].W > 0f && raw[i].J >= 0 && raw[i].J < boneCount
                    && (best < 0 || raw[i].W > raw[best].W))
                    best = i;
            if (best < 0) break;
            picked.Add(raw[best]);
            raw[best] = (raw[best].J, 0f);
        }
        if (picked.Count == 0) return Array.Empty<ImportInfluence>();

        float total = picked.Sum(p => p.W);
        float inv = total > 0f ? 1f / total : 1f;
        return picked.Select(p => new ImportInfluence { BoneIndex = p.J, Weight = p.W * inv }).ToArray();
    }

    /// <summary>
    /// Per-vertex tangents summed from triangle UV gradients, then Gram-Schmidt
    /// orthogonalized against the normal. Degenerate UVs fall back to any vector
    /// orthogonal to the normal.
    /// </summary>
    private static void ComputeTangents(ImportScene scene)
    {
        var accum = new Vector3[scene.Vertices.Count];
        foreach (var tri in scene.Triangles)
        {
            var va = scene.Vertices[tri.A];
            var vb = scene.Vertices[tri.B];
            var vc = scene.Vertices[tri.C];
            var e1 = vb.WorldPosition - va.WorldPosition;
            var e2 = vc.WorldPosition - va.WorldPosition;
            var d1 = vb.UV - va.UV;
            var d2 = vc.UV - va.UV;
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;
            var tangent = (e1 * d2.Y - e2 * d1.Y) / det;
            accum[tri.A] += tangent;
            accum[tri.B] += tangent;
            accum[tri.C] += tangent;
        }
        for (int i = 0; i < scene.Vertices.Count; i++)
        {
            var n = scene.Vertices[i].WorldNormal;
            var t = accum[i] - n * Vector3.Dot(n, accum[i]);
            if (t.LengthSquared() < 1e-12f)
                t = Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            scene.Vertices[i].WorldTangent = Vector3.Normalize(t);
        }
    }

    private static (int W, int H, byte[] Rgba) DecodeToRgba(byte[] encoded)
    {
        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(encoded);
        byte[] rgba = new byte[img.Width * img.Height * 4];
        img.CopyPixelDataTo(rgba);
        return (img.Width, img.Height, rgba);
    }

    private static byte[] Resize(byte[] rgba, int sw, int sh, int dw, int dh)
    {
        using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(rgba, sw, sh);
        SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(img,
            x => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(x, dw, dh));
        byte[] result = new byte[dw * dh * 4];
        img.CopyPixelDataTo(result);
        return result;
    }

    private static bool HasAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return true;
        return false;
    }

    private static string Sanitize(string raw)
    {
        var clean = new string(raw.Where(c =>
            c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_' or '-').ToArray());
        if (clean.Length > TextureData.NameWidth)
            clean = clean[..TextureData.NameWidth];
        return clean.Length == 0 ? "tex" : clean;
    }
}
