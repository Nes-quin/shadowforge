using System.Numerics;
using Microsoft.Extensions.Logging;
using ShadowForge.Formats.DDS;

namespace ShadowForge.Formats.HDB.Import;

/// <summary>
/// glTF to HDB importer. Reads the scene, splits it into palette-limited draw batches,
/// writes the HDB, then runs ModelValidator over the bytes and throws before writing
/// anything when the result would not load.
/// </summary>
public static class ModelImporter
{
    /// <summary>
    /// With <paramref name="rigBones"/>, each bone named in the rig takes that bone's frame
    /// and extra euler, so motions made for the rig, such as event and face clips, pose the
    /// imported model the same way.
    /// </summary>
    public static void Import(
        string gltfPath, string hdbOut, ILogger log, GraphicFormat? formatOverride = null,
        IReadOnlyList<Bone>? rigBones = null)
    {
        var scene = GLTFSceneReader.Read(gltfPath, log, formatOverride);
        if (rigBones is not null)
        {
            int moved = MoveOntoRigFrames(scene, rigBones);
            int kept = TakeRigExtraEuler(scene, rigBones);
            log.LogInformation(
                "Moved {Moved} bone(s) onto the rig's frames and kept the rig's extra euler on {Kept}.", moved, kept);
        }
        var batches = BatchBuilder.Build(scene);

        int stripIndices = batches.Sum(b => b.StripIndices.Count);
        log.LogInformation(
            "{Batches} draw batch(es), {Tris} triangles -> {Indices} strip indices, {Verts} packed vertices ({Mode}).",
            batches.Count, scene.Triangles.Count, stripIndices,
            batches.Sum(b => b.Vertices.Count), scene.Skinned ? "skinned 100B" : "rigid 48B");

        byte[] hdb = FileBuilder.Build(scene, batches);

        var validation = ModelValidator.Run(hdb);
        foreach (var f in validation.Findings)
        {
            if (f.Level == ModelValidator.Level.Error) log.LogError("validator: {Msg}", f.Message);
            else if (f.Level == ModelValidator.Level.Warning) log.LogWarning("validator: {Msg}", f.Message);
        }
        if (validation.HasErrors)
            throw new InvalidDataException(
                "Emitted HDB would not load; refusing to write. See validator errors above.");

        string outDir = Path.GetDirectoryName(Path.GetFullPath(hdbOut)) ?? ".";
        foreach (var tex in scene.Textures)
        {
            if (tex.DDSBytes.Length == 0)
            {
                log.LogInformation(
                    "Skipped writing {Name}.dds (name-only texture-table entry; "
                    + "the real file must ship alongside the model).", tex.Name);
                continue;
            }
            string ddsPath = Path.Combine(outDir, tex.Name + ".dds");
            File.WriteAllBytes(ddsPath, tex.DDSBytes);
            log.LogInformation("Wrote {Path}", ddsPath);
        }

        File.WriteAllBytes(hdbOut, hdb);
        log.LogInformation("Wrote {Path} ({Size} bytes, validator clean).", hdbOut, hdb.Length);
    }

    /// <summary>
    /// Re-expresses every scene bone's rest transform in the rig's frames (see
    /// <see cref="JointFrames"/>) and recomputes the world transforms the vertices are
    /// encoded against. Returns how many bones changed frame.
    /// </summary>
    private static int MoveOntoRigFrames(ImportScene scene, IReadOnlyList<Bone> rigBones)
    {
        var bones = scene.Bones;
        var frames = JointFrames.Compute(
            bones.Select(b => new JointFrames.Joint(b.Name, b.ParentIndex, b.Rotation)).ToList(), rigBones);

        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            var frame = frames[i];
            var parentFrame = bone.ParentIndex >= 0 ? frames[bone.ParentIndex] : Quaternion.Identity;
            if (frame == Quaternion.Identity && parentFrame == Quaternion.Identity) continue;
            bone.Translation = JointFrames.MoveTranslation(bone.Translation, parentFrame);
            bone.Rotation = JointFrames.MoveRotation(bone.Rotation, frame, parentFrame);
            bone.Scale = JointFrames.MoveScale(bone.Scale, frame);
        }
        RuntimeMath.ComputeWorldTransforms(bones);
        return frames.Count(f => f != Quaternion.Identity);
    }

    /// <summary>
    /// Copies the extra euler of the first rig bone with each scene bone's name and returns
    /// how many scene bones got a non-zero one.
    /// </summary>
    private static int TakeRigExtraEuler(ImportScene scene, IReadOnlyList<Bone> rigBones)
    {
        var byName = new Dictionary<string, Bone>(StringComparer.Ordinal);
        foreach (var bone in rigBones)
            byName.TryAdd(bone.Name, bone);

        int kept = 0;
        foreach (var bone in scene.Bones)
        {
            if (!byName.TryGetValue(bone.Name, out var rigBone)) continue;
            bone.ExtraEuler = (float[])rigBone.ExtraEuler.Clone();
            if (bone.ExtraEuler.Any(v => v != 0f)) kept++;
        }
        return kept;
    }
}
