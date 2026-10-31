using Microsoft.Extensions.Logging;
using ShadowForge.Formats.HDB;
using ShadowForge.Formats.HDB.Import;
using ShadowForge.Formats.HMB.Import;

namespace ShadowForge.GameData.Entities;

public sealed record EntityCookResult(IReadOnlyList<string> Outputs, IReadOnlyList<string> Warnings);

/// <summary>
/// Cooks an edited entity GLB back into game files: the object HDB, its DDS textures, and the
/// motion pack. The entity's shipped rig, when it has one, supplies the bone frames and extra
/// euler, and its shipped motion pack is the template for the new pack. Output names come from
/// the model def, falling back to "{id}_obj.hdb" and "{id}_mot.mpk".
/// </summary>
public sealed class EntityCooker
{
    private readonly GameFileSystem _gfs;

    public EntityCooker(GameInstall install) => _gfs = new GameFileSystem(install);

    /// <summary>
    /// "{id}.glb" in <paramref name="fromDir"/>, else "{id}.gltf".
    /// </summary>
    public static string FindEditedModel(string fromDir, string id)
    {
        foreach (string ext in (string[])[".glb", ".gltf"])
        {
            string path = Path.Combine(fromDir, id + ext);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"No {id}.glb or {id}.gltf in {fromDir}.");
    }

    public EntityCookResult Cook(ResolvedEntity entity, string modelPath, string cookedDir, ILogger log)
    {
        Directory.CreateDirectory(cookedDir);
        var outputs = new List<string>();

        var rig = entity.Files.FirstOrDefault(f => f.Role == FileRole.Skeleton && f.Exists);
        var rigBones = rig is null ? null : ModelCooker.Bake(ModelReader.Read(_gfs.ReadVfs(rig.VfsPath))).Bones;

        string hdbOut = Path.Combine(cookedDir, entity.ModelDef.ObjectHDB ?? entity.Id + "_obj.hdb");
        ModelImporter.Import(modelPath, hdbOut, log, rigBones: rigBones);
        outputs.Add(hdbOut);
        outputs.AddRange(Directory.EnumerateFiles(cookedDir, "*.dds"));

        var baseMotion = entity.Files.FirstOrDefault(f => f.Role == FileRole.Motion && f.Exists);
        using var template = baseMotion is null ? null : new MemoryStream(_gfs.ReadVfs(baseMotion.VfsPath), writable: false);
        var result = MotionImporter.Import(modelPath, File.ReadAllBytes(hdbOut), template);

        string mpkOut = Path.Combine(cookedDir, entity.ModelDef.MotPack ?? entity.Id + "_mot.mpk");
        File.WriteAllBytes(mpkOut, result.MPKBytes);
        outputs.Add(mpkOut);
        return new EntityCookResult(outputs, result.Warnings);
    }
}
