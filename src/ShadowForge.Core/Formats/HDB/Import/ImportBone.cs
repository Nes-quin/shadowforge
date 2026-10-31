using System.Numerics;

namespace ShadowForge.Formats.HDB.Import;

/// <summary>
/// One skeleton joint with its glTF node-local TRS. FileBuilder writes the rotation as
/// euler angles for the runtime composition Parent * T * R * S, R = Rz * Ry * Rx.
/// </summary>
public sealed class ImportBone
{
    public int Index;
    public string Name = "";
    public int ParentIndex = -1;
    public Vector3 Translation;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;

    /// <summary>
    /// Extra euler triples A then B, as on <see cref="Bone.ExtraEuler"/>. FileBuilder keeps
    /// <see cref="Rotation"/> as A * Euler * B and writes A and B into the bone record.
    /// </summary>
    public float[] ExtraEuler = new float[6];

    /// <summary>
    /// World transform composed with <see cref="RuntimeMath"/>, used to encode vertices in
    /// bone-local space.
    /// </summary>
    public Matrix4x4 World = Matrix4x4.Identity;
    public Matrix4x4 InverseWorld = Matrix4x4.Identity;
}
