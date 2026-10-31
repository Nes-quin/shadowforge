using Microsoft.Extensions.Logging.Abstractions;
using ShadowForge.CLI.Commands;
using ShadowForge.Formats.GLTF;
using ShadowForge.Formats.HDB;
using static ShadowForge.Tests.CLI.EntityCommandRunner;

namespace ShadowForge.Tests.CLI;

/// <summary>
/// hdb import given no --rig takes the shipped rig whose bone names are the glTF's joint
/// names, so the cooked model keeps that rig's extra euler.
/// </summary>
public sealed class HdbImportRigTests
{
    [SkippableFact]
    public void Import_WithoutRig_KeepsTheShippedRigsExtraEuler()
    {
        var install = RetailData.Install;
        var stock = ModelCooker.Bake(ModelReader.Read(RetailData.Read(@"chara\ply\pc03\pc03_obj.hdb")));
        string dir = Directory.CreateDirectory(MissingPath("sf_hdbrig_")).FullName;
        try
        {
            string glb = Path.Combine(dir, "pc03.glb");
            SceneExporter.Export(stock, glb);
            string hdb = Path.Combine(dir, "pc03_obj.hdb");

            int code = HDBCommands.Create(_ => NullLoggerFactory.Instance)
                .Parse(["import", glb, "-o", hdb, "--game-root", install.GameDataRoot])
                .Invoke();

            Assert.Equal(0, code);
            var imported = ModelCooker.Bake(ModelReader.Read(hdb)).Bones;
            foreach (var bone in stock.Bones.Where(b => b.ExtraEuler.Any(v => v != 0f)))
                Assert.Equal(bone.ExtraEuler, imported.First(b => b.Name == bone.Name).ExtraEuler);
        }
        finally { Directory.Delete(dir, true); }
    }
}
