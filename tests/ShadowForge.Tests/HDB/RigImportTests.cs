using System.Numerics;
using ShadowForge.Formats.GLTF;
using ShadowForge.Formats.HDB;
using ShadowForge.Formats.HDB.Import;
using ShadowForge.Formats.HMB;
using ShadowForge.Formats.HMB.Import;
using ShadowForge.Tests.GLTF;
using ShadowForge.Tests.Logging;

namespace ShadowForge.Tests.HDB;

/// <summary>
/// The runtime poses a bone as T * A * Euler * B * S in its rest frame, with T, Euler and S
/// from the motion track and A and B the bone's extra euler. Importing with the rig keeps the
/// rig's frames and extra euler, so clips made for the rig, such as cutscene and face motions,
/// pose the imported model like the rig.
/// </summary>
public sealed class RigImportTests
{
    private const int ClipFrames = 20;

    private static ModelFile Rig()
    {
        var raw = new byte[3 * 100];
        ExportFixtures.EncodeSkinnedVertex(raw, 0, new Vector3(0, 0, 0), new Vector2(0.25f, 0.5f));
        ExportFixtures.EncodeSkinnedVertex(raw, 100, new Vector3(1, 0, 0), new Vector2(0.5f, 0.5f));
        ExportFixtures.EncodeSkinnedVertex(raw, 200, new Vector3(0, 1, 0), new Vector2(0.25f, 0.25f));

        return new ModelFile
        {
            Bones =
            {
                new Bone { Index = 0, Name = "root" },
                new Bone
                {
                    Index = 1, Name = "hips", ParentIndex = 0, PosY = 10f,
                    ExtraEuler = new float[] { 0f, 0f, 1.5707964f, 0f, 0f, 0f },
                },
                new Bone
                {
                    Index = 2, Name = "skirt", ParentIndex = 1, PosX = 1f, PosZ = -0.5f,
                    EulerZ = -0.05f,
                    ExtraEuler = new float[] { -1.5184364f, 0.41887906f, 0.015707964f, 0.2f, 0f, -0.1f },
                },
                new Bone
                {
                    Index = 3, Name = "skirt_end", ParentIndex = 2, PosX = 2f,
                    EulerX = 0.1f, EulerY = 0.2f, EulerZ = 0.3f,
                },
            },
            Textures = { new TextureEntry { Name = "body" } },
            TextureCount = 1,
            VertexArrays = { new VertexArray { VAType = 0, VertexCount = 3, RawVertices = raw } },
            IndexArrays = { new IndexArray { Indices = new ushort[] { 0, 1, 2 } } },
            MeshGroups =
            {
                new MeshGroup { VAIndex = 0, IAIndex = 0, MaterialIndex = 0, Topology = 0, BonePalette = { 0 } },
            },
        };
    }

    /// <summary>
    /// A rig clip with translation and rotation keys on every bone below the root.
    /// </summary>
    private static MotionClip RigClip() => new()
    {
        Name = "rig_clip",
        Mode = 2,
        DurationFrames = ClipFrames,
        Rate = MotionClip.DefaultFrameRate,
        Tracks =
        {
            Track("hips", new Vector3(0f, 10f, 0f), new Vector3(0.5f, 10.5f, 0.2f),
                new Vector3(0.1f, -0.2f, 0.3f), new Vector3(-0.4f, 0.6f, 0.2f)),
            Track("skirt", new Vector3(1f, 0f, -0.5f), new Vector3(1f, 0.2f, -0.5f),
                new Vector3(0.3f, 0.1f, -0.2f), new Vector3(0.9f, -0.3f, 0.5f)),
            Track("skirt_end", new Vector3(2f, 0f, 0f), new Vector3(2.5f, 0f, 0f),
                new Vector3(0f, 0.5f, 0f), new Vector3(0.2f, -0.1f, 1.2f)),
        },
    };

    private static MotionTrack Track(string bone, Vector3 t0, Vector3 t1, Vector3 r0, Vector3 r1) => new()
    {
        BoneName = bone,
        Translation = new Vec3Channel
        {
            Linear = [new LinearVec3Key(0, t0), new LinearVec3Key(ClipFrames, t1)],
        },
        Rotation = new EulerChannel
        {
            Linear = [EulerKey(0, r0), EulerKey(ClipFrames, r1)],
        },
    };

    private static LinearEulerKey EulerKey(short frame, Vector3 radians)
        => new(frame, RawAngle.FromRadians(radians.X), RawAngle.FromRadians(radians.Y), RawAngle.FromRadians(radians.Z));

    [Fact]
    public void ImportWithRig_RigMotionPosesEveryBoneLikeTheRig()
    {
        var rig = Rig();
        string dir = NewTempDir();
        try
        {
            ExportFixtures.WriteSolidDDS(dir, "body", 200, 50, 50);
            string glb = Path.Combine(dir, "model.glb");
            SceneExporter.Export(rig, glb, textureDir: dir);
            string hdb = Path.Combine(dir, "model.hdb");
            ModelImporter.Import(glb, hdb, new CapturingLogger(), rigBones: rig.Bones);

            var imported = ModelCooker.Bake(ModelReader.Read(hdb)).Bones;
            var trackEuler = new Vector3(0.5f, -0.3f, 0.8f);
            foreach (var rigBone in rig.Bones)
            {
                var bone = imported.Single(b => b.Name == rigBone.Name);
                AssertSameRotation(BindPose.LocalRotation(rigBone), BindPose.LocalRotation(bone), rigBone.Name + " at rest", 1e-4f);
                AssertSameRotation(PosedRotation(rigBone, trackEuler), PosedRotation(bone, trackEuler), rigBone.Name + " posed", 1e-4f);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A model and clip exported with every joint below the root turned import onto the
    /// rig's frames, and the clip comes back as the rig's clip.
    /// </summary>
    [Fact]
    public void ImportWithRig_TurnedJoints_TakeTheRigFramesAndKeepTheRigClip()
    {
        var rig = Rig();
        var clip = RigClip();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var (turnedModel, turnedClip) = Turned(rig, clip, turn);
        string dir = NewTempDir();
        try
        {
            ExportFixtures.WriteSolidDDS(dir, "body", 200, 50, 50);
            string glb = Path.Combine(dir, "model.glb");
            SceneExporter.Export(turnedModel, glb, textureDir: dir, clips: [turnedClip]);
            string hdb = Path.Combine(dir, "model.hdb");
            ModelImporter.Import(glb, hdb, new CapturingLogger(), rigBones: rig.Bones);
            string mpk = Path.Combine(dir, "model_mot.mpk");
            File.WriteAllBytes(mpk, MotionImporter.Import(glb, File.ReadAllBytes(hdb)).MPKBytes);

            var imported = ModelCooker.Bake(ModelReader.Read(hdb)).Bones;
            foreach (var rigBone in rig.Bones)
            {
                var bone = imported.Single(b => b.Name == rigBone.Name);
                AssertSameRotation(BindPose.LocalRotation(rigBone), BindPose.LocalRotation(bone), rigBone.Name + " at rest", 1e-4f);
                AssertClose(Position(rigBone), Position(bone), rigBone.Name + " rest position");
            }

            var importedClip = MotPack.ReadAll(mpk, out _).Single();
            foreach (var track in clip.Tracks)
            {
                var rigBone = rig.Bones.Single(b => b.Name == track.BoneName);
                var bone = imported.Single(b => b.Name == track.BoneName);
                var importedTrack = importedClip.Tracks.Single(t => t.BoneName == track.BoneName);
                for (int f = 0; f <= ClipFrames; f++)
                {
                    AssertSameRotation(
                        PosedRotation(rigBone, SampleEuler(track, f)),
                        PosedRotation(bone, SampleEuler(importedTrack, f)),
                        $"{track.BoneName} frame {f}", 2e-3f);
                    AssertClose(
                        Sampler.SampleVec3(track.Translation!, f),
                        Sampler.SampleVec3(importedTrack.Translation!, f),
                        $"{track.BoneName} frame {f} translation");
                }
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The rig and clip with every joint below the root turned by <paramref name="turn"/> in
    /// its own local space, the extra euler folded into the euler, and one key per frame.
    /// </summary>
    private static (ModelFile Model, MotionClip Clip) Turned(ModelFile rig, MotionClip clip, Quaternion turn)
    {
        Quaternion FrameOf(int boneIndex) => boneIndex > 0 ? turn : Quaternion.Identity;
        Quaternion ParentFrameOf(Bone bone) => bone.ParentIndex >= 0 ? FrameOf(bone.ParentIndex) : Quaternion.Identity;
        Quaternion Turn(Bone bone, Quaternion local)
            => Quaternion.Conjugate(ParentFrameOf(bone)) * local * FrameOf(bone.Index);

        var model = Rig();
        model.Bones.Clear();
        foreach (var bone in rig.Bones)
        {
            var position = Vector3.Transform(Position(bone), Quaternion.Conjugate(ParentFrameOf(bone)));
            var euler = RuntimeMath.QuaternionToEulerZyx(
                Turn(bone, FullRotation(bone, new Vector3(bone.EulerX, bone.EulerY, bone.EulerZ))));
            model.Bones.Add(new Bone
            {
                Index = bone.Index, Name = bone.Name, ParentIndex = bone.ParentIndex,
                PosX = position.X, PosY = position.Y, PosZ = position.Z,
                EulerX = euler.X, EulerY = euler.Y, EulerZ = euler.Z,
            });
        }

        var turnedClip = new MotionClip
        {
            Name = clip.Name, Mode = 2, DurationFrames = clip.DurationFrames, Rate = clip.Rate,
        };
        foreach (var track in clip.Tracks)
        {
            var bone = rig.Bones.Single(b => b.Name == track.BoneName);
            var toParentFrame = Quaternion.Conjugate(ParentFrameOf(bone));
            var translations = new LinearVec3Key[ClipFrames + 1];
            var rotations = new LinearEulerKey[ClipFrames + 1];
            for (short f = 0; f <= ClipFrames; f++)
            {
                translations[f] = new LinearVec3Key(f, Vector3.Transform(Sampler.SampleVec3(track.Translation!, f), toParentFrame));
                rotations[f] = EulerKey(f, RuntimeMath.QuaternionToEulerZyx(Turn(bone, FullRotation(bone, SampleEuler(track, f)))));
            }
            turnedClip.Tracks.Add(new MotionTrack
            {
                BoneName = track.BoneName,
                Translation = new Vec3Channel { Linear = translations },
                Rotation = new EulerChannel { Linear = rotations },
            });
        }
        return (model, turnedClip);
    }

    private static Vector3 Position(Bone bone) => new(bone.PosX, bone.PosY, bone.PosZ);

    private static Vector3 SampleEuler(MotionTrack track, float frame)
    {
        var (x, y, z) = Sampler.SampleEuler(track.Rotation!, frame);
        return new Vector3(x, y, z);
    }

    /// <summary>
    /// A * Euler * B with the given euler.
    /// </summary>
    private static Quaternion FullRotation(Bone bone, Vector3 euler)
    {
        var e = bone.ExtraEuler;
        return AnimationExporter.SandwichRotation(
            Sampler.EulerToQuaternion(e[0], e[1], e[2]),
            Sampler.EulerToQuaternion(euler.X, euler.Y, euler.Z),
            Sampler.EulerToQuaternion(e[3], e[4], e[5]));
    }

    /// <summary>
    /// The bone's local rotation with <paramref name="trackEuler"/> in place of its rest euler.
    /// </summary>
    private static float[,] PosedRotation(Bone bone, Vector3 trackEuler)
        => BindPose.LocalRotation(new Bone
        {
            EulerX = trackEuler.X,
            EulerY = trackEuler.Y,
            EulerZ = trackEuler.Z,
            ExtraEuler = bone.ExtraEuler,
        });

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sf_rig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void AssertSameRotation(float[,] expected, float[,] actual, string what, float tolerance)
    {
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) < tolerance,
                    $"{what}[{r},{c}]: {actual[r, c]} vs {expected[r, c]}");
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, string what)
        => Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"{what}: {actual} vs {expected}");
}
