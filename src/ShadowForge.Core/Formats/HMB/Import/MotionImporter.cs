using System.Numerics;
using ShadowForge.Formats.HDB;
using ShadowForge.Formats.HDB.Import;
using ShadowForge.Formats.IPK;
using SharpGLTF.Schema2;

namespace ShadowForge.Formats.HMB.Import;

/// <summary>
/// Imports baked GLTF animation clips into a repacked mot.mpk. Fatal conditions throw
/// <see cref="InvalidDataException"/>. Non-fatal ones are returned in
/// <see cref="MotionImportResult.Warnings"/> rather than logged.
/// </summary>
public static class MotionImporter
{
    /// <summary>
    /// Reads the clips in <paramref name="glbPath"/>, checks them against the HDB skeleton, moves
    /// them onto its bone frames and undoes each bone's extra euler orientation. Every clip is
    /// built, read back and compared before it is packed. With a template, entries with a
    /// matching name are replaced, the rest kept verbatim, unmatched clips appended, and the
    /// template's alignment wins over <paramref name="alignment"/>. Without one, the archive
    /// holds only the imported clips.
    /// </summary>
    public static MotionImportResult Import(
        string glbPath, byte[] hdbBytes, Stream? templateMPK = null, uint alignment = 0x80)
    {
        var warnings = new List<string>();

        var template = new List<ArchiveWriter.InputEntry>();
        if (templateMPK is not null)
        {
            var archive = ArchiveReader.ReadArchive(templateMPK);
            alignment = archive.Alignment;
            foreach (var entry in archive.Entries)
            {
                using var ms = new MemoryStream();
                ArchiveReader.ExtractEntry(templateMPK, entry, ms, archive.UsesZlib);
                template.Add(new ArchiveWriter.InputEntry(entry.Name, ms.ToArray()));
            }
        }

        var model = ModelCooker.Bake(ModelReader.Read(hdbBytes));
        var gltf = ModelRoot.Load(glbPath);

        warnings.AddRange(GLTFAnimationReader.CrossCheckSkeleton(model.Bones.Select(b => b.Name), gltf));

        var clips = GLTFAnimationReader.Read(gltf, out var readWarnings, BoneOrients(model), model.Bones);
        warnings.AddRange(readWarnings);

        RequireKnownBones(clips, model);

        var built = BuildClips(clips);
        var (entries, replaced, appended, untouched) = Merge(template, built);

        return new MotionImportResult(ArchiveWriter.Build(entries, alignment), warnings, replaced, appended, untouched);
    }

    /// <summary>
    /// The pre and post rotations of every named bone with a non-zero extra euler, keyed by
    /// name. A repeated name keeps its first bone.
    /// </summary>
    private static Dictionary<string, (Quaternion Pre, Quaternion Post)> BoneOrients(ModelFile model)
    {
        var orients = new Dictionary<string, (Quaternion Pre, Quaternion Post)>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in model.Bones)
        {
            if (string.IsNullOrEmpty(bone.Name) || !seen.Add(bone.Name)) continue;
            var extra = bone.ExtraEuler;
            if (extra.Any(v => v != 0f))
            {
                orients[bone.Name] = (
                    Sampler.EulerToQuaternion(extra[0], extra[1], extra[2]),
                    Sampler.EulerToQuaternion(extra[3], extra[4], extra[5]));
            }
        }
        return orients;
    }

    private static void RequireKnownBones(IEnumerable<MotionClip> clips, ModelFile model)
    {
        var boneNames = new HashSet<string>(model.Bones.Select(b => b.Name), StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var clip in clips)
            foreach (var track in clip.Tracks)
                if (!boneNames.Contains(track.BoneName))
                    missing.Add(track.BoneName);

        if (missing.Count > 0)
            throw new InvalidDataException(
                $"{missing.Count} track bone name(s) not found in HDB model: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Builds each clip as "{name}.hmb" and rejects it unless reading the bytes back samples
    /// within the <see cref="MotionValidator"/> tolerances.
    /// </summary>
    private static Dictionary<string, byte[]> BuildClips(IEnumerable<MotionClip> clips)
    {
        var built = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in clips)
        {
            byte[] bytes;
            try
            {
                bytes = MotionBuilder.Build(clip);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"MotionBuilder.Build failed for clip {clip.Name}: {ex.Message}", ex);
            }

            MotionClip rebuilt;
            try
            {
                rebuilt = MotionReader.Read(bytes, clip.Name);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Self-validation read-back failed for clip {clip.Name}: {ex.Message}", ex);
            }

            var report = MotionValidator.Compare(clip, rebuilt);
            if (!report.Passed)
            {
                throw new InvalidDataException(
                    $"Self-validation FAILED for clip {clip.Name}: {string.Join("; ", report.Messages)} " +
                    $"(T={report.MaxTranslationError:G4} R={report.MaxRotationErrorRadians:G4} S={report.MaxScaleError:G4})");
            }

            string key = clip.Name + ".hmb";
            if (!built.TryAdd(key, bytes))
                throw new InvalidDataException(
                    $"Duplicate animation name {clip.Name} cannot map to distinct mpk entries.");
        }
        return built;
    }

    /// <summary>
    /// Keeps template order. A template entry being replaced must have no extra first-table
    /// sections, since the rebuilt clip would drop them.
    /// </summary>
    private static (List<ArchiveWriter.InputEntry> Entries, int Replaced, int Appended, int Untouched) Merge(
        List<ArchiveWriter.InputEntry> template, Dictionary<string, byte[]> built)
    {
        var entries = new List<ArchiveWriter.InputEntry>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int replaced = 0, untouched = 0;
        foreach (var entry in template)
        {
            if (!built.TryGetValue(entry.Name, out var newBytes))
            {
                entries.Add(entry);
                untouched++;
                continue;
            }

            var oldClip = MotionReader.Read(entry.Data, Path.GetFileNameWithoutExtension(entry.Name));
            if (oldClip.ExtraSectionTypes.Count > 0)
            {
                throw new InvalidDataException(
                    $"Refusing to replace template entry {entry.Name}: it has extra FT section(s) "
                    + $"{string.Join(", ", oldClip.ExtraSectionTypes.Select(t => $"0x{t:X}"))} "
                    + "that import would silently drop.");
            }

            entries.Add(new ArchiveWriter.InputEntry(entry.Name, newBytes));
            matched.Add(entry.Name);
            replaced++;
        }

        int appended = 0;
        foreach (var (name, bytes) in built)
        {
            if (matched.Contains(name)) continue;
            entries.Add(new ArchiveWriter.InputEntry(name, bytes));
            appended++;
        }

        return (entries, replaced, appended, untouched);
    }
}
