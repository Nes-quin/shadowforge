using System.Numerics;
using SharpGLTF.Schema2;
using ShadowForge.Formats.HMB;

namespace ShadowForge.Formats.HDB.Import;

/// <summary>
/// glTF -> MotionClip (mode 2, 30 fps). Companion of
/// <see cref="ShadowForge.Formats.GLTF.AnimationExporter"/> for the reverse
/// direction. Joint set is the first skin's joints, matching
/// <see cref="GLTFSceneReader"/>'s enumeration so bone names line up.
/// </summary>
public static class GLTFAnimationReader
{
    /// <summary>
    /// Reads glTF animations into MotionClips. <paramref name="boneOrients"/> maps a joint
    /// name to the (pre, post) orient quaternions built from Bone.ExtraEuler. For a joint
    /// with an entry, the sampled rotation is divided by pre and post before euler
    /// decomposition, so the HMB track carries only the animated factor that
    /// AnimationExporter multiplied in. Joints without an entry are read as sampled.
    /// With <paramref name="frameBones"/>, the bones the model was imported with, every key
    /// is first moved onto those bones' frames (see <see cref="JointFrames"/>).
    /// </summary>
    public static List<MotionClip> Read(
        ModelRoot gltf, out List<string> warnings,
        IReadOnlyDictionary<string, (Quaternion Pre, Quaternion Post)>? boneOrients = null,
        IReadOnlyList<Bone>? frameBones = null)
    {
        warnings = [];
        var clips = new List<MotionClip>();

        var skin = gltf.LogicalSkins.FirstOrDefault();
        int jointCount = skin?.JointsCount ?? 0;
        var joints = new Node[jointCount];
        var jointNames = new string[jointCount];
        var jointSet = new HashSet<Node>();
        for (int i = 0; i < jointCount; i++)
        {
            joints[i] = skin!.GetJoint(i).Joint;
            jointSet.Add(joints[i]);
            string name = joints[i].Name ?? $"joint{i}";
            ValidateJointName(name);
            jointNames[i] = name;
        }
        CheckCrcCollisions(jointNames);
        var parents = JointParents(joints);
        var frames = frameBones is null
            ? Enumerable.Repeat(Quaternion.Identity, jointCount).ToArray()
            : JointFrames.Compute(
                Enumerable.Range(0, jointCount)
                    .Select(i => new JointFrames.Joint(jointNames[i], parents[i], joints[i].LocalTransform.Rotation))
                    .ToList(),
                frameBones);

        foreach (var anim in gltf.LogicalAnimations)
        {
            if (string.IsNullOrEmpty(anim.Name))
                throw new InvalidDataException(
                    "glTF animation has no name; HMB clips must be nameable for mpk entries.");

            int durFrames = Math.Max(1, (int)MathF.Round(anim.Duration * MotionClip.DefaultFrameRate));
            int frameCount = durFrames + 1;

            var clip = new MotionClip
            {
                Name = anim.Name,
                Mode = 2,
                DurationFrames = durFrames,
                Rate = MotionClip.DefaultFrameRate,
                RateFlag = 0f,
            };

            for (int i = 0; i < jointCount; i++)
            {
                var node = joints[i];
                var tChan = anim.FindTranslationChannel(node);
                var rChan = anim.FindRotationChannel(node);
                var sChan = anim.FindScaleChannel(node);
                if (tChan == null && rChan == null && sChan == null)
                    continue;

                var track = new MotionTrack { BoneName = jointNames[i] };
                (Quaternion Pre, Quaternion Post) orient = default;
                bool hasOrient = boneOrients != null
                    && boneOrients.TryGetValue(jointNames[i], out orient)
                    && (orient.Pre != default || orient.Post != default);
                var frame = frames[i];
                var parentFrame = parents[i] >= 0 ? frames[parents[i]] : Quaternion.Identity;
                bool moved = frame != Quaternion.Identity || parentFrame != Quaternion.Identity;

                if (tChan != null)
                {
                    var sampler = tChan.GetTranslationSampler().CreateCurveSampler();
                    var values = new Vector3[frameCount];
                    for (int f = 0; f < frameCount; f++)
                    {
                        var t = sampler.GetPoint(f / MotionClip.DefaultFrameRate);
                        values[f] = moved ? JointFrames.MoveTranslation(t, parentFrame) : t;
                    }
                    track.Translation = new Vec3Channel { Linear = HMBQuantize.ReduceVec3(values) };
                }

                if (sChan != null)
                {
                    var sampler = sChan.GetScaleSampler().CreateCurveSampler();
                    var values = new Vector3[frameCount];
                    for (int f = 0; f < frameCount; f++)
                        values[f] = JointFrames.MoveScale(sampler.GetPoint(f / MotionClip.DefaultFrameRate), frame);
                    track.Scale = new Vec3Channel { Linear = HMBQuantize.ReduceVec3(values) };
                }

                if (rChan != null)
                {
                    var sampler = rChan.GetRotationSampler().CreateCurveSampler();
                    var anglesX = new float[frameCount];
                    var anglesY = new float[frameCount];
                    var anglesZ = new float[frameCount];
                    for (int f = 0; f < frameCount; f++)
                    {
                        var qLocal = sampler.GetPoint(f / MotionClip.DefaultFrameRate);
                        if (moved) qLocal = JointFrames.MoveRotation(qLocal, frame, parentFrame);
                        var qAnim = hasOrient ? UnsandwichRotation(qLocal, orient.Pre, orient.Post) : qLocal;
                        var (ex, ey, ez) = QuaternionToEulerZyx(qAnim);
                        anglesX[f] = ex;
                        anglesY[f] = ey;
                        anglesZ[f] = ez;
                    }

                    short[] rawX, rawY, rawZ;
                    try
                    {
                        rawX = HMBQuantize.QuantizeAngleAxis(anglesX);
                        rawY = HMBQuantize.QuantizeAngleAxis(anglesY);
                        rawZ = HMBQuantize.QuantizeAngleAxis(anglesZ);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new InvalidDataException(
                            $"{clip.Name}: bone '{track.BoneName}' rotation cannot be represented: {ex.Message}");
                    }

                    track.Rotation = new EulerChannel { Linear = HMBQuantize.ReduceEuler(rawX, rawY, rawZ) };
                }

                clip.Tracks.Add(track);
            }

            foreach (var channel in anim.Channels)
            {
                if (channel.TargetNode != null && !jointSet.Contains(channel.TargetNode))
                    warnings.Add(
                        $"{clip.Name}: channel targets non-joint node '{channel.TargetNode.Name ?? "<unnamed>"}'; ignored.");
            }

            if (clip.Tracks.Count == 0)
            {
                warnings.Add($"{clip.Name}: no channels on any joint; skipped.");
                continue;
            }

            clips.Add(clip);
        }

        return clips;
    }

    /// <summary>
    /// Compares the HDB bone names with the first skin's joint names and returns one
    /// warning per direction of mismatch. Tracks bind to bones by name CRC, so a bone
    /// present in only one file marks a stale HDB, and geometry weighted to it stays at
    /// bind pose.
    /// </summary>
    public static List<string> CrossCheckSkeleton(IEnumerable<string> hdbBoneNames, ModelRoot gltf)
    {
        var warnings = new List<string>();
        var skin = gltf.LogicalSkins.FirstOrDefault();
        var jointNames = new HashSet<string>(StringComparer.Ordinal);
        if (skin != null)
            for (int i = 0; i < skin.JointsCount; i++)
                jointNames.Add(skin.GetJoint(i).Joint.Name ?? $"joint{i}");

        var boneNames = new HashSet<string>(hdbBoneNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);

        var hdbOnly = boneNames.Except(jointNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (hdbOnly.Count > 0)
            warnings.Add(
                $"HDB has {hdbOnly.Count} bone(s) with no matching glTF skin joint: "
                + string.Join(", ", hdbOnly)
                + ". The HDB is likely STALE (built from an older export of this model); "
                + "geometry weighted to these bones will not animate. Rebuild it with 'hdb import'.");

        var gltfOnly = jointNames.Except(boneNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (gltfOnly.Count > 0)
            warnings.Add(
                $"glTF skin has {gltfOnly.Count} joint(s) with no matching HDB bone: "
                + string.Join(", ", gltfOnly)
                + ". Tracks for these joints cannot bind in-game (note the HDB bone name "
                + "field holds at most 16 ASCII characters, no NUL terminator).");

        return warnings;
    }

    /// <summary>
    /// Each joint's parent joint index, or -1 when its parent node is not a joint.
    /// </summary>
    private static int[] JointParents(Node[] joints)
    {
        var index = new Dictionary<Node, int>();
        for (int i = 0; i < joints.Length; i++)
            index[joints[i]] = i;
        return joints
            .Select(j => j.VisualParent is { } p && index.TryGetValue(p, out int pi) ? pi : -1)
            .ToArray();
    }

    private static void CheckCrcCollisions(string[] jointNames)
    {
        var byCrc = new Dictionary<uint, string>();
        foreach (var name in jointNames)
        {
            uint crc = BoneNameCrc.Compute(name);
            if (byCrc.TryGetValue(crc, out var existing))
            {
                if (existing != name)
                    throw new InvalidDataException(
                        $"joint names '{existing}' and '{name}' collide on CRC 0x{crc:X8}; "
                        + "the runtime cannot distinguish them by name hash.");
            }
            else
            {
                byCrc[crc] = name;
            }
        }
    }

    private static void ValidateJointName(string name)
    {
        bool ascii = true;
        foreach (char c in name)
        {
            if (c >= 128) { ascii = false; break; }
        }
        if (!ascii || name.Length > MotionTrack.NameFieldSize - 1)
            throw new InvalidDataException(
                $"joint name '{name}' exceeds the HMB name field limit of {MotionTrack.NameFieldSize - 1} ASCII bytes.");
    }

    /// <summary>
    /// Inverse of <see cref="ShadowForge.Formats.GLTF.AnimationExporter.SandwichRotation"/>.
    /// From local = pre * anim * post (column vectors), anim = pre^-1 * local * post^-1.
    /// Concatenate(a, b) applies a then b, which is M_b * M_a.
    /// </summary>
    internal static Quaternion UnsandwichRotation(Quaternion local, Quaternion pre, Quaternion post)
        => Quaternion.Concatenate(
            Quaternion.Inverse(post),
            Quaternion.Concatenate(local, Quaternion.Inverse(pre)));

    private static (float X, float Y, float Z) QuaternionToEulerZyx(Quaternion q)
    {
        var m4 = Matrix4x4.CreateFromQuaternion(q);
        float m00 = m4.M11, m10 = m4.M12, m20 = m4.M13;
        float m21 = m4.M23, m22 = m4.M33;
        float m01 = m4.M21, m11 = m4.M22;

        if (MathF.Abs(m20) < 0.999999f)
            return (MathF.Atan2(m21, m22), MathF.Asin(-m20), MathF.Atan2(m10, m00));

        float gimbalY = m20 <= -0.999999f ? MathF.PI / 2f : -MathF.PI / 2f;
        return (0f, gimbalY, MathF.Atan2(-m01, m11));
    }
}
