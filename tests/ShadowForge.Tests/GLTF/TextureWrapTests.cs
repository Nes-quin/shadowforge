using System.Numerics;
using ShadowForge.Formats.GLTF;
using ShadowForge.Formats.HDB;
using SharpGLTF.Schema2;

namespace ShadowForge.Tests.GLTF;

/// <summary>
/// A texture mirrors only when a draw of its slot takes a UV below 0. A part laid out a
/// whole tile past 1, like np108's face, repeats so the texture is not flipped.
/// </summary>
public sealed class TextureWrapTests
{
    /// <summary>
    /// Textures "body" and "face", one skinned VA of six vertices and two triangle-list
    /// groups: "body" spans u -0.5..0.5 in 0..1 v, "face" spans 1.2..1.8 on both axes.
    /// </summary>
    private static ModelFile BuildModel()
    {
        var raw = new byte[6 * 100];
        // EncodeSkinnedVertex takes the stored v, which decodes to 1 - v.
        ExportFixtures.EncodeSkinnedVertex(raw, 0, new Vector3(0, 0, 0), new Vector2(-0.5f, 0.75f));
        ExportFixtures.EncodeSkinnedVertex(raw, 100, new Vector3(1, 0, 0), new Vector2(0.5f, 0.75f));
        ExportFixtures.EncodeSkinnedVertex(raw, 200, new Vector3(0, 1, 0), new Vector2(0f, 0.25f));
        ExportFixtures.EncodeSkinnedVertex(raw, 300, new Vector3(0, 0, 1), new Vector2(1.2f, -0.2f));
        ExportFixtures.EncodeSkinnedVertex(raw, 400, new Vector3(1, 0, 1), new Vector2(1.8f, -0.2f));
        ExportFixtures.EncodeSkinnedVertex(raw, 500, new Vector3(0, 1, 1), new Vector2(1.5f, -0.8f));

        return new ModelFile
        {
            Bones = { new Bone { Index = 0, Name = "root" } },
            Textures =
            {
                new TextureEntry { Name = "body" },
                new TextureEntry { Name = "face" },
            },
            TextureCount = 2,
            VertexArrays =
            {
                new VertexArray { VAType = 0, VertexCount = 6, RawVertices = raw },
            },
            IndexArrays =
            {
                new IndexArray { Indices = new ushort[] { 0, 1, 2 } },
                new IndexArray { Indices = new ushort[] { 3, 4, 5 } },
            },
            MeshGroups =
            {
                new MeshGroup { VAIndex = 0, IAIndex = 0, MaterialIndex = 0,
                                Topology = 0, BonePalette = { 0 } },
                new MeshGroup { VAIndex = 0, IAIndex = 1, MaterialIndex = 1,
                                Topology = 0, BonePalette = { 0 } },
            },
        };
    }

    /// <summary>
    /// The base color wrap modes. A texture without a sampler repeats on both axes.
    /// </summary>
    private static (TextureWrapMode S, TextureWrapMode T) BaseColorWrap(ModelRoot gltf, string material)
    {
        var channel = gltf.LogicalMaterials.Single(m => m.Name == material).FindChannel("BaseColor")!.Value;
        Assert.NotNull(channel.Texture);
        var sampler = channel.TextureSampler;
        return sampler is null
            ? (TextureWrapMode.REPEAT, TextureWrapMode.REPEAT)
            : (sampler.WrapS, sampler.WrapT);
    }

    private static ModelRoot ExportAndLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sf_wrap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ExportFixtures.WriteSolidDDS(dir, "body", 200, 50, 50);
            ExportFixtures.WriteSolidDDS(dir, "face", 50, 200, 50);
            string outPath = Path.Combine(dir, "model.glb");
            SceneExporter.Export(BuildModel(), outPath, textureDir: dir);
            return ModelRoot.Load(outPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AxisBelowZero_Mirrors()
    {
        var wrap = BaseColorWrap(ExportAndLoad(), "body");
        Assert.Equal((TextureWrapMode.MIRRORED_REPEAT, TextureWrapMode.MIRRORED_REPEAT), wrap);
    }

    [Fact]
    public void PartOneTilePastOne_Repeats()
    {
        var wrap = BaseColorWrap(ExportAndLoad(), "face");
        Assert.Equal((TextureWrapMode.REPEAT, TextureWrapMode.REPEAT), wrap);
    }
}
