using System.Numerics;
using ShadowForge.Formats.GLTF;
using ShadowForge.Formats.HMB;

namespace ShadowForge.Formats.HDB.Import;

/// <summary>
/// Moves glTF joints onto a rig's bone frames, so motions made for the rig pose the joints
/// like the rig. Each joint gets a frame rotation K, applied in its own local space
/// (world' = world * K for column vectors), that turns its rest rotation relative to its
/// parent into the rig bone's A * Euler * B. A joint keeps its frame when the rig has no bone
/// of its name, when that bone or one above it is mirrored, or when K is a rounding-level turn.
/// </summary>
public static class JointFrames
{
    /// <summary>
    /// A glTF joint: its name, its parent's index in the same list or -1, and its rest local rotation.
    /// </summary>
    public readonly record struct Joint(string Name, int Parent, Quaternion Rotation);

    private const float IdentityTolerance = 1e-5f;

    /// <summary>
    /// The frame rotation of each joint, in joint order.
    /// </summary>
    public static Quaternion[] Compute(IReadOnlyList<Joint> joints, IReadOnlyList<Bone> rigBones)
    {
        var rigByName = new Dictionary<string, Bone>(StringComparer.Ordinal);
        foreach (var bone in rigBones)
            rigByName.TryAdd(bone.Name, bone);

        var frames = new Quaternion?[joints.Count];
        var mirrored = new bool[joints.Count];
        for (int i = 0; i < joints.Count; i++)
            Resolve(i);
        return frames.Select(f => f!.Value).ToArray();

        void Resolve(int i)
        {
            if (frames[i] is not null) return;
            var joint = joints[i];
            var parentFrame = Quaternion.Identity;
            if (joint.Parent >= 0)
            {
                Resolve(joint.Parent);
                parentFrame = frames[joint.Parent]!.Value;
                mirrored[i] = mirrored[joint.Parent];
            }

            if (!rigByName.TryGetValue(joint.Name, out var bone))
            {
                frames[i] = Quaternion.Identity;
                return;
            }
            mirrored[i] |= bone.ScaleX < 0f || bone.ScaleY < 0f || bone.ScaleZ < 0f;
            if (mirrored[i])
            {
                frames[i] = Quaternion.Identity;
                return;
            }

            var frame = Quaternion.Normalize(Quaternion.Conjugate(joint.Rotation) * parentFrame * RestRotation(bone));
            frames[i] = new Vector3(frame.X, frame.Y, frame.Z).Length() < IdentityTolerance ? Quaternion.Identity : frame;
        }
    }

    public static Vector3 MoveTranslation(Vector3 translation, Quaternion parentFrame)
        => Vector3.Transform(translation, Quaternion.Conjugate(parentFrame));

    public static Quaternion MoveRotation(Quaternion rotation, Quaternion frame, Quaternion parentFrame)
        => Quaternion.Normalize(Quaternion.Conjugate(parentFrame) * rotation * frame);

    /// <summary>
    /// The scale along each new axis. Exact when the frame maps axes onto axes or the scale
    /// is uniform.
    /// </summary>
    public static Vector3 MoveScale(Vector3 scale, Quaternion frame)
    {
        if (frame == Quaternion.Identity) return scale;
        float Along(Vector3 axis) => (scale * Vector3.Transform(axis, frame)).Length();
        return new Vector3(Along(Vector3.UnitX), Along(Vector3.UnitY), Along(Vector3.UnitZ));
    }

    /// <summary>
    /// A * Euler * B of a rig bone as a quaternion.
    /// </summary>
    private static Quaternion RestRotation(Bone bone)
    {
        var e = bone.ExtraEuler;
        return AnimationExporter.SandwichRotation(
            Sampler.EulerToQuaternion(e[0], e[1], e[2]),
            Sampler.EulerToQuaternion(bone.EulerX, bone.EulerY, bone.EulerZ),
            Sampler.EulerToQuaternion(e[3], e[4], e[5]));
    }
}
