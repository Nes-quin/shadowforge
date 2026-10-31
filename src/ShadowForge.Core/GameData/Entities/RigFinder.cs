using ShadowForge.Formats.HDB;
using ShadowForge.Formats.HDB.Raw;
using ShadowForge.Formats.MDL;

namespace ShadowForge.GameData.Entities;

/// <summary>
/// A shipped skeleton file and its baked bones.
/// </summary>
public sealed record ShippedRig(string VfsPath, IReadOnlyList<Bone> Bones);

/// <summary>
/// Finds the shipped skeletons a glTF skeleton can come from, by bone names. A skeleton
/// matches when its bone names are exactly the joint names, leaving out the "Dummy" joints
/// the exporter adds for unused bone indices.
/// </summary>
public sealed class RigFinder
{
    public const string PlaceholderJointName = "Dummy";

    private readonly GameInstall _install;
    private readonly GameFileSystem _gfs;

    public RigFinder(GameInstall install)
    {
        _install = install;
        _gfs = new GameFileSystem(install);
    }

    /// <summary>
    /// Every matching skeleton once: files shared by several entities, and files whose bones
    /// match an earlier one's in name and rest transform, are listed once.
    /// </summary>
    public List<ShippedRig> Find(IReadOnlyCollection<string> jointNames)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<ShippedRig>();
        foreach (var entry in new EntityCatalog(_install).ListChara())
        {
            var mdl = ModelDef.Read(_gfs.ReadVfs(entry.ModelDefRelPath));
            if (mdl.Path is not { Length: > 0 } rigPath || mdl.ObjectHDB is not { Length: > 0 } hdb) continue;
            string skeleton = $@"{rigPath.Replace('/', '\\').Trim('\\')}\{hdb}";
            if (!seen.Add(skeleton) || !_gfs.ExistsVfs(skeleton)) continue;

            var raw = ModelReader.Read(_gfs.ReadVfs(skeleton));
            if (!Matches(jointNames, raw.FirstTable.OfType<RawBoneEntry>().Select(b => b.Name))) continue;
            var bones = ModelCooker.Bake(raw).Bones;
            if (matches.Any(m => SameRestPose(m.Bones, bones))) continue;
            matches.Add(new ShippedRig(skeleton, bones));
        }
        return matches;
    }

    /// <summary>
    /// True when both skeletons have the same named bones with the same position, euler,
    /// scale and extra euler.
    /// </summary>
    public static bool SameRestPose(IReadOnlyList<Bone> a, IReadOnlyList<Bone> b)
    {
        static float[] Rest(Bone x) =>
            [x.PosX, x.PosY, x.PosZ, x.EulerX, x.EulerY, x.EulerZ, x.ScaleX, x.ScaleY, x.ScaleZ, .. x.ExtraEuler];

        var restByName = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var bone in a.Where(x => x.Name != PlaceholderJointName))
            restByName.TryAdd(bone.Name, Rest(bone));
        var named = b.Where(x => x.Name != PlaceholderJointName).ToList();
        return named.Count == restByName.Count
            && named.All(x => restByName.TryGetValue(x.Name, out var rest) && rest.SequenceEqual(Rest(x)));
    }

    public static bool Matches(IEnumerable<string> jointNames, IEnumerable<string> boneNames)
        => NameSet(jointNames).SetEquals(NameSet(boneNames));

    private static HashSet<string> NameSet(IEnumerable<string> names)
        => new(names.Where(n => n.Length > 0 && n != PlaceholderJointName), StringComparer.Ordinal);
}
