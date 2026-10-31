using System.Numerics;
using System.Text.Json.Nodes;
using ShadowForge.Formats.DDS;
using ShadowForge.Formats.HDB;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace ShadowForge.Formats.GLTF;

/// <summary>
/// glTF materials for a model's texture slots. A slot whose texture is not found gets a
/// flat color from a fixed palette. A texture that any draw of the slot takes below 0 samples
/// with MIRRORED_REPEAT, because Blue Dragon UVs span [-1, +1] on mirror-symmetric parts.
/// Every other texture samples with REPEAT, because some parts (np108's face, sw02's head)
/// sit a whole tile past 1 and show the texture unflipped. Both axes share one mode, since
/// Blender's importer rebuilds a mixed pair from math nodes that sample differently.
/// </summary>
internal static class SceneMaterials
{
    private static readonly Vector4[] FallbackColors =
    [
        new(0.8f, 0.2f, 0.2f, 1f),
        new(0.2f, 0.6f, 0.8f, 1f),
        new(0.2f, 0.8f, 0.3f, 1f),
        new(0.8f, 0.7f, 0.2f, 1f),
        new(0.6f, 0.3f, 0.7f, 1f),
        new(0.8f, 0.5f, 0.2f, 1f),
        new(0.4f, 0.7f, 0.7f, 1f),
        new(0.7f, 0.4f, 0.5f, 1f),
    ];

    /// <summary>
    /// One material per texture slot, at least one. <paramref name="textureNames"/>
    /// overrides the slot names by ordinal.
    /// </summary>
    public static MaterialBuilder[] Build(ModelFile model, string? textureDir,
        IReadOnlyList<string>? textureNames = null)
    {
        int count = Math.Max(model.TextureCount, 1);
        var materials = new MaterialBuilder[count];
        var wraps = WrapModes(model, count);
        string NameFor(int i) =>
            textureNames is not null && i < textureNames.Count ? textureNames[i]
            : i < model.Textures.Count ? model.Textures[i].Name : $"material_{i}";
        for (int i = 0; i < count; i++)
        {
            string name = NameFor(i);
            var mat = NewMaterial(name);

            bool hasTexture = false;
            if (textureDir != null && i < model.Textures.Count)
            {
                if (TextureResolver.FindDDS(name, textureDir) is { } diffusePath)
                {
                    mat.WithBaseColor(LoadDDS(diffusePath, name), null);
                    ApplyWrap(mat, KnownChannel.BaseColor, wraps[i]);
                    hasTexture = true;
                }
                else if (TextureResolver.FindVolume(name, textureDir) is { } volumePath)
                {
                    hasTexture = BindVolumeTexture(mat, name, volumePath, wraps[i]);
                }

                if (TextureResolver.FindNormalMap(name, textureDir) is { } normalPath)
                {
                    mat.WithNormal(LoadDDS(normalPath, name + "_n"), 1.0f);
                    ApplyWrap(mat, KnownChannel.Normal, wraps[i]);
                }
            }

            if (!hasTexture)
                mat.WithBaseColor(FallbackColors[i % FallbackColors.Length]);

            materials[i] = mat;
        }
        return materials;
    }

    /// <summary>
    /// One staged (eye) material, named after its stage-1 texture, the iris the runtime
    /// shows. The stage-1 image is baseColor on TEXCOORD_1. Stage 0 (the material the
    /// command stream had bound) and stage 2 (the eyelid, on TEXCOORD_2) go in the
    /// sfStage0Texture and sfStage2Texture extras so the importer can rebuild the
    /// 0x60 / 0x61 / 0x62 binds.
    /// </summary>
    public static MaterialBuilder BuildStaged(ModelFile model,
        int stage0, int stage1, int stage2, string? textureDir)
    {
        string NameFor(int i) =>
            i >= 0 && i < model.Textures.Count ? model.Textures[i].Name : $"material_{i}";

        string name = NameFor(stage1);
        var mat = NewMaterial(name);

        bool hasTexture = false;
        if (textureDir != null && stage1 >= 0 && stage1 < model.Textures.Count
            && TextureResolver.FindDDS(name, textureDir) is { } diffusePath)
        {
            mat.UseChannel(KnownChannel.BaseColor)
                .UseTexture()
                .WithPrimaryImage(LoadDDS(diffusePath, name))
                .WithCoordinateSet(1);
            ApplyWrap(mat, KnownChannel.BaseColor, TextureWrapMode.MIRRORED_REPEAT);
            hasTexture = true;
        }

        if (!hasTexture)
            mat.WithBaseColor(FallbackColors[(stage1 >= 0 ? stage1 : 0) % FallbackColors.Length]);

        mat.Extras = new JsonObject
        {
            ["sfStage0Texture"] = NameFor(stage0),
            ["sfStage2Texture"] = stage2 >= 0 ? NameFor(stage2) : null,
            ["sfStage2TexCoord"] = 2,
        };
        return mat;
    }

    /// <summary>
    /// Adds the stage-0 (face) materials that no unstaged draw uses. ToGltf2 writes only
    /// materials a primitive uses, and a staged draw uses its iris material, so a face
    /// texture bound only under eye stages (pc05) would otherwise be missing and leave the
    /// sfStage0Texture extra pointing at nothing.
    /// </summary>
    public static void AddStage0OnlyMaterials(ModelFile model, MaterialBuilder[] materials, ModelRoot gltf)
    {
        var bound = new HashSet<int>(model.MeshGroups
            .Where(g => g.Stage1TexIndex < 0)
            .Select(g => Math.Clamp(g.MaterialIndex, 0, materials.Length - 1)));
        var stage0Only = model.MeshGroups
            .Where(g => g.Stage1TexIndex >= 0)
            .Select(g => Math.Clamp(g.MaterialIndex, 0, materials.Length - 1))
            .Where(i => !bound.Contains(i))
            .Distinct();
        foreach (int i in stage0Only)
        {
            string name = materials[i].Name;
            if (gltf.LogicalMaterials.Any(m => m.Name == name))
                continue;
            gltf.CreateMaterial(materials[i]);
        }
    }

    private static MaterialBuilder NewMaterial(string name)
        => new MaterialBuilder(name)
            .WithMetallicRoughnessShader()
            .WithMetallicRoughness(0f, 0.8f);

    private static ImageBuilder LoadDDS(string path, string name)
        => ImageBuilder.From(new MemoryImage(Converter.ConvertToPngBytes(path)), name);

    /// <summary>
    /// Binds a .36t volume texture (fur shell). glTF has no 3D textures, so depth slice 0
    /// is the baseColor image, and the sfVolumeTexture and sfVolumeDepth extras name the
    /// file and its slice count. The importer keeps such a binding by name instead of
    /// cooking the preview into a 2D .dds.
    /// </summary>
    private static bool BindVolumeTexture(MaterialBuilder mat, string texName, string volumePath,
        TextureWrapMode wrap)
    {
        var slices = Converter.ConvertVolumeToPngSlices(File.ReadAllBytes(volumePath));
        if (slices.Count == 0)
            return false;

        mat.WithBaseColor(ImageBuilder.From(new MemoryImage(slices[0]), texName), null);
        ApplyWrap(mat, KnownChannel.BaseColor, wrap);
        mat.Extras = new JsonObject
        {
            ["sfVolumeTexture"] = texName + ".36t",
            ["sfVolumeDepth"] = slices.Count,
        };
        return true;
    }

    /// <summary>
    /// Wrap mode per texture slot from the base UVs of the draws bound to it: MIRRORED_REPEAT
    /// when some vertex takes either axis below 0, REPEAT otherwise.
    /// </summary>
    private static TextureWrapMode[] WrapModes(ModelFile model, int count)
    {
        var mirror = new bool[count];
        foreach (var va in VertexArrayDraws.Collect(model))
        {
            foreach (var (group, ia) in va.Draws)
            {
                int slot = Math.Clamp(group.MaterialIndex, 0, count - 1);
                foreach (var (a, b, c) in DrawTriangles.Enumerate(ia.Indices, group.Topology, va.Vertices.Count))
                {
                    foreach (var v in (Vertex[])[va.Vertices[a], va.Vertices[b], va.Vertices[c]])
                        mirror[slot] |= v.U < 0 || v.V < 0;
                }
            }
        }
        return mirror.Select(m => m ? TextureWrapMode.MIRRORED_REPEAT : TextureWrapMode.REPEAT).ToArray();
    }

    private static void ApplyWrap(MaterialBuilder mat, KnownChannel channel, TextureWrapMode wrap)
    {
        mat.UseChannel(channel)
            .UseTexture()
            .WithSampler(
                wrap,
                wrap,
                TextureMipMapFilter.DEFAULT,
                TextureInterpolationFilter.DEFAULT);
    }
}
