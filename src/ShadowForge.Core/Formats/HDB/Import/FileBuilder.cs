using System.Numerics;
using System.Text;
using ShadowForge.IO;
using ShadowForge.Formats.HDB.Raw;
using ShadowForge.Formats.HDB.Wire;
using ShadowForge.Formats.HMB;

namespace ShadowForge.Formats.HDB.Import;

/// <summary>
/// Writes an HDB in the order retail files use: header, first table, rebase table,
/// trailing word, payloads with type 9 last, then the index and vertex blocks from
/// <see cref="RawLayout.DataRegionStart"/> of the type-9 payload end.
/// </summary>
public static class FileBuilder
{
    private sealed class Layout
    {
        public List<(FirstTableEntryType Type, int Offset, int Length)> Entries = new();
        public List<(int Cell, int Target)> Fixups = new();
        public List<int> SlotCells = new();
    }

    public static byte[] Build(ImportScene scene, List<ImportBatch> batches)
    {
        var bones = scene.Bones;
        bool skinned = scene.Skinned;
        int geometryBone = bones.FindIndex(b => b.ParentIndex < 0);
        if (geometryBone < 0) throw new InvalidDataException("Scene has no root bone.");

        batches = batches
            .OrderBy(b => b.MaterialIndex)
            .ThenBy(b => b.Stage1Index >= 0 || b.Stage2Index >= 0)
            .ToList();

        byte[] rcStream = BuildRenderCommands(batches, skinned);
        var vaBlobs = batches
            .Select(b => skinned ? VertexPacker.PackSkinned(b, bones) : VertexPacker.PackRigid(b.Vertices))
            .ToList();

        var combinedIndices = new List<ushort>();
        var drawRanges = new List<(int Start, int Count)>();
        foreach (var b in batches)
        {
            drawRanges.Add((combinedIndices.Count, b.StripIndices.Count));
            combinedIndices.AddRange(b.StripIndices);
        }
        if (combinedIndices.Count > 0xFFFF)
            throw new InvalidDataException(
                $"Combined index buffer holds {combinedIndices.Count} indices; draw words encode 16-bit start offsets.");

        var entrySizes = new List<(FirstTableEntryType Type, int Length)>();
        bool hasTextures = scene.Textures.Count > 0;
        if (hasTextures)
        {
            entrySizes.Add((FirstTableEntryType.TextureTable, scene.Textures.Count * TextureData.Size));
            entrySizes.Add((FirstTableEntryType.TextureCount, 8));
        }
        foreach (var bone in bones) entrySizes.Add((FirstTableEntryType.Bone, BoneRecordSize(bone)));
        entrySizes.Add((FirstTableEntryType.RenderCommands, rcStream.Length));
        entrySizes.Add((FirstTableEntryType.IndexTable, 8));
        entrySizes.Add((FirstTableEntryType.VASetup, 4 + VASetupRecord.Size * batches.Count));
        entrySizes.Add((FirstTableEntryType.Model, ModelRecord.Size));
        entrySizes.Add((FirstTableEntryType.SceneDesc, RawUnknownEntry9.RecordSize));

        int ftCount = entrySizes.Count;
        int slotCount = ftCount
            + (hasTextures ? 1 : 0)
            + bones.Count * 3
            + 3
            + 2;

        int p = FileHeader.Size;
        int entriesStart = p + RawLayout.FirstTableHeaderSize;
        int q = entriesStart + ftCount * FirstTableEntry.Size;
        int r = q + 8 + slotCount * 4;
        int payloadStart = Align.Up(r + 4, 4);

        var layout = new Layout();
        int cursor = payloadStart;
        foreach (var (type, length) in entrySizes)
        {
            layout.Entries.Add((type, cursor, length));
            cursor = Align.Up(cursor + length, 4);
        }
        int type9End = layout.Entries[^1].Offset + RawUnknownEntry9.RecordSize;
        int dataStart = RawLayout.DataRegionStart(type9End);

        int ibBlockOffset = dataStart;
        int ibBytes = combinedIndices.Count * 2;
        int vaCursor = ibBlockOffset + IndexBlockHeader.Size + Align.Up(ibBytes, 4);
        var vaOffsets = new List<int>();
        foreach (var blob in vaBlobs)
        {
            vaOffsets.Add(vaCursor);
            vaCursor += VAHeaderData.Size + Align.Up(blob.Length, 4);
        }
        int fileSize = vaCursor;

        var d = new byte[fileSize];

        BigEndian.WriteUInt32(d, 0x00, RawHeader.MagicValue);
        BigEndian.WriteUInt32(d, 0x04, 0x00010000);
        BigEndian.WriteUInt32(d, 0x0C, 4);
        BigEndian.WriteUInt32(
            d, RawHeader.FirstTableOffsetField,
            (uint)(p - RawHeader.FirstTableOffsetField));

        BigEndian.WriteUInt32(d, p, (uint)ftCount);
        BigEndian.WriteInt32(d, p + 4, q - (p + 4));
        for (int i = 0; i < ftCount; i++)
        {
            int e = entriesStart + i * FirstTableEntry.Size;
            var (type, offset, length) = layout.Entries[i];
            BigEndian.WriteUInt32(d, e, (uint)type);
            layout.Fixups.Add((e + RawEntry.DataOffsetField, offset));
            layout.SlotCells.Add(e + RawEntry.DataOffsetField);
            BigEndian.WriteUInt32(d, e + 12, (uint)length);
        }

        int PayloadOf(FirstTableEntryType type)
        {
            for (int i = 0; i < layout.Entries.Count; i++)
                if (layout.Entries[i].Type == type) return layout.Entries[i].Offset;
            throw new InvalidOperationException($"no FT entry of type {type}");
        }

        var boneOffsets = new List<int>();
        for (int i = 0; i < layout.Entries.Count; i++)
            if (layout.Entries[i].Type == FirstTableEntryType.Bone)
                boneOffsets.Add(layout.Entries[i].Offset);

        if (hasTextures)
        {
            int t10 = PayloadOf(FirstTableEntryType.TextureTable);
            for (int i = 0; i < scene.Textures.Count; i++)
            {
                int rec = t10 + i * TextureData.Size;
                WriteName(d, rec, scene.Textures[i].Name, TextureData.NameWidth);
                BigEndian.WriteUInt32(d, rec + 0x18, scene.Textures[i].IsNormalMap ? 2u : 0u);
            }
            int t11 = PayloadOf(FirstTableEntryType.TextureCount);
            BigEndian.WriteUInt32(d, t11, (uint)scene.Textures.Count);
            layout.Fixups.Add((t11 + 4, t10));
            layout.SlotCells.Add(t11 + 4);
        }

        int type6Off = PayloadOf(FirstTableEntryType.Model);
        WriteBones(d, layout, scene, bones, boneOffsets, geometryBone, type6Off);

        int rcOff = PayloadOf(FirstTableEntryType.RenderCommands);
        Array.Copy(rcStream, 0, d, rcOff, rcStream.Length);

        int t4 = PayloadOf(FirstTableEntryType.IndexTable);
        BigEndian.WriteUInt32(d, t4, (uint)combinedIndices.Count);

        int t5 = PayloadOf(FirstTableEntryType.VASetup);
        BigEndian.WriteUInt32(d, t5, (uint)batches.Count);
        for (int i = 0; i < batches.Count; i++)
        {
            int rec = t5 + 4 + i * VASetupRecord.Size;
            BigEndian.WriteUInt32(d, rec, (uint)batches[i].Vertices.Count);
            BigEndian.WriteUInt32(d, rec + 4, skinned ? VertexFormat.Skinned : VertexFormat.Rigid);
        }

        layout.Fixups.Add((type6Off + ModelRecord.RenderCommandPtrOffset, rcOff));
        layout.SlotCells.Add(type6Off + ModelRecord.RenderCommandPtrOffset);
        BigEndian.WriteUInt32(d, type6Off + ModelRecord.IndexBlockCountOffset, 1);
        layout.Fixups.Add((type6Off + ModelRecord.IndexTablePtrOffset, t4));
        layout.SlotCells.Add(type6Off + ModelRecord.IndexTablePtrOffset);
        BigEndian.WriteUInt32(d, type6Off + ModelRecord.VertexArrayCountOffset, (uint)batches.Count);
        layout.Fixups.Add((type6Off + ModelRecord.VASetupPtrOffset, t5));
        layout.SlotCells.Add(type6Off + ModelRecord.VASetupPtrOffset);

        var (center, radius) = ComputeSphere(scene, bones[geometryBone]);
        WriteVector3(d, type6Off + ModelRecord.SphereCenterOffset, center);
        BigEndian.WriteFloat(d, type6Off + ModelRecord.SphereRadiusOffset, radius);

        int t9 = PayloadOf(FirstTableEntryType.SceneDesc);
        int budget = ComputeSceneBudget(bones.Count, rcStream.Length, batches.Count);
        BigEndian.WriteUInt32(d, t9 + 0, 0x00300600);
        BigEndian.WriteUInt32(d, t9 + 4, skinned ? 0x00010202u : 0x00010002u);
        layout.SlotCells.Add(t9 + 8);
        BigEndian.WriteUInt32(d, t9 + 12, (uint)bones.Count);
        layout.Fixups.Add((t9 + 16, boneOffsets[geometryBone]));
        layout.SlotCells.Add(t9 + 16);
        BigEndian.WriteUInt32(d, t9 + 20, (uint)budget);

        BigEndian.WriteUInt32(d, ibBlockOffset, (uint)Align.Up(ibBytes, 4));
        BigEndian.WriteUInt32(d, ibBlockOffset + 4, (uint)ibBytes);
        for (int i = 0; i < combinedIndices.Count; i++)
            BigEndian.WriteUInt16(d, ibBlockOffset + IndexBlockHeader.Size + i * 2, combinedIndices[i]);

        uint declWord = VertexFormat.ComputeStrideAndDecl(
            skinned ? VertexFormat.Skinned : VertexFormat.Rigid).DeclWord;
        for (int i = 0; i < vaBlobs.Count; i++)
        {
            int off = vaOffsets[i];
            BigEndian.WriteUInt32(d, off, (uint)vaBlobs[i].Length);
            BigEndian.WriteUInt32(d, off + 4, declWord);
            BigEndian.WriteUInt32(d, off + 8, (uint)batches[i].Vertices.Count);
            Array.Copy(vaBlobs[i], 0, d, off + VAHeaderData.Size, vaBlobs[i].Length);
        }

        foreach (var (cell, target) in layout.Fixups)
            BigEndian.WriteInt32(d, cell, target - cell);

        if (layout.SlotCells.Count != slotCount)
            throw new InvalidOperationException(
                $"rebase slot bookkeeping drifted: reserved {slotCount}, emitted {layout.SlotCells.Count}");
        BigEndian.WriteUInt32(d, q, (uint)slotCount);
        BigEndian.WriteInt32(d, q + 4, r - (q + 4));
        for (int i = 0; i < slotCount; i++)
        {
            int slotAddr = q + 8 + i * 4;
            BigEndian.WriteInt32(d, slotAddr, layout.SlotCells[i] - slotAddr);
        }
        BigEndian.WriteUInt32(d, r, 4);

        return d;
    }

    /// <summary>
    /// Emits the render-command word stream. Skinned models follow the retail character
    /// layout of em003: 0x5000, then per draw 0x4NNN, 0x0400 once, 0x60NN on material
    /// change, 0x02NN palette, 0x2000 draw. Rigid props follow the retail prop layout of
    /// bi11j01a, which adds 0x0419, a 0x93 color and 0xE000, and carries no palette. A
    /// staged (eye) batch puts [StagedStateOpen, stage-1 bind, stage-2 bind,
    /// StagedStateClose] before its palette and forces a stage-0 rebind on the next batch.
    /// Internal so tests can drive it with hand-built batches.
    /// </summary>
    internal static byte[] BuildRenderCommands(List<ImportBatch> batches, bool skinned)
    {
        var words = new List<ushort> { 0x5000 };
        int lastMaterial = -1;
        int drawBase = 0;
        for (int i = 0; i < batches.Count; i++)
        {
            var b = batches[i];
            words.Add(0x4000);
            words.Add((ushort)i);
            if (i == 0)
            {
                words.Add(skinned ? (ushort)0x0400 : (ushort)0x0419);
                if (!skinned)
                {
                    words.Add(0x9333);
                    words.Add(0x3333);
                }
            }
            bool staged = b.Stage1Index >= 0 || b.Stage2Index >= 0;
            if (b.MaterialIndex != lastMaterial)
            {
                words.Add(new MaterialWord(0, b.MaterialIndex));
                lastMaterial = b.MaterialIndex;
            }
            if (staged)
            {
                words.Add(RenderCommandStream.StagedStateOpen);
                if (b.Stage1Index >= 0) words.Add(new MaterialWord(1, b.Stage1Index));
                if (b.Stage2Index >= 0) words.Add(new MaterialWord(2, b.Stage2Index));
                words.Add(RenderCommandStream.StagedStateClose);
                lastMaterial = -1;
            }
            if (skinned && b.Palette.Length > 0)
            {
                if (b.Palette.Length > BatchBuilder.MaxPaletteEntries)
                    throw new InvalidDataException("palette exceeds 24 entries");
                words.Add((ushort)(0x0200 | b.Palette.Length));
                foreach (int boneIndex in b.Palette)
                    words.Add((ushort)boneIndex);
            }
            if (!skinned) words.Add(0xE000);
            words.Add(0x2000);
            words.Add((ushort)(b.StripIndices.Count - 2));
            words.Add((ushort)drawBase);
            drawBase += b.StripIndices.Count;
        }
        words.Add(0x00FF);
        if ((words.Count & 1) != 0) words.Add(0x0000);

        var bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++)
            BigEndian.WriteUInt16(bytes, i * 2, words[i]);
        return bytes;
    }

    private static void WriteBones(
        byte[] d, Layout layout, ImportScene scene, List<ImportBone> bones,
        List<int> boneOffsets, int geometryBone, int type6Off)
    {
        int n = bones.Count;
        var firstChild = new int[n];
        var nextSibling = new int[n];
        Array.Fill(firstChild, -1);
        Array.Fill(nextSibling, -1);
        var lastChildOf = new int[n];
        Array.Fill(lastChildOf, -1);
        int firstRoot = -1, lastRoot = -1;
        for (int i = 0; i < n; i++)
        {
            int parent = bones[i].ParentIndex;
            if (parent < 0)
            {
                if (firstRoot < 0) firstRoot = i;
                else nextSibling[lastRoot] = i;
                lastRoot = i;
                continue;
            }
            if (firstChild[parent] < 0) firstChild[parent] = i;
            else nextSibling[lastChildOf[parent]] = i;
            lastChildOf[parent] = i;
        }

        for (int i = 0; i < n; i++)
        {
            var b = bones[i];
            int o = boneOffsets[i];
            var euler = RecordEuler(b);
            var extraA = new Vector3(b.ExtraEuler[0], b.ExtraEuler[1], b.ExtraEuler[2]);
            var extraB = new Vector3(b.ExtraEuler[3], b.ExtraEuler[4], b.ExtraEuler[5]);

            var flags = BoneFlags.None;
            if (b.Translation.LengthSquared() > 1e-12f) flags |= BoneFlags.Position;
            if (euler.LengthSquared() > 1e-12f) flags |= BoneFlags.Euler;
            if ((b.Scale - Vector3.One).LengthSquared() > 1e-12f) flags |= BoneFlags.Scale;
            if (extraA != Vector3.Zero) flags |= BoneFlags.ExtraEulerA;
            if (extraB != Vector3.Zero) flags |= BoneFlags.ExtraEulerB;

            flags |= b.ParentIndex >= 0 ? BoneFlags.Parented : BoneFlags.Anchor;
            if (i == geometryBone) flags |= BoneFlags.Model;

            BigEndian.WriteUInt32(d, o + BoneData.IndexOffset, (uint)b.Index);
            BigEndian.WriteUInt32(d, o + BoneData.FlagsOffset, (uint)flags);

            if (i == geometryBone)
                layout.Fixups.Add((o + BoneData.ModelPtrOffset, type6Off));
            layout.SlotCells.Add(o + BoneData.ModelPtrOffset);

            WriteVector3(d, o + BoneData.PositionOffset, b.Translation);
            WriteVector3(d, o + BoneData.EulerOffset, euler);
            WriteVector3(d, o + BoneData.ScaleOffset, b.Scale);

            if (firstChild[i] >= 0)
                layout.Fixups.Add((o + BoneData.ChildPtrOffset, boneOffsets[firstChild[i]]));
            layout.SlotCells.Add(o + BoneData.ChildPtrOffset);
            if (nextSibling[i] >= 0)
                layout.Fixups.Add((o + BoneData.SiblingPtrOffset, boneOffsets[nextSibling[i]]));
            layout.SlotCells.Add(o + BoneData.SiblingPtrOffset);

            WriteName(d, o + BoneData.NameOffset, b.Name, BoneData.NameWidth);

            if (HasExtraEuler(b))
            {
                WriteVector3(d, o + BoneData.ExtraEulerAOffset, extraA);
                WriteVector3(d, o + BoneData.ExtraEulerBOffset, extraB);
            }
        }
    }

    private static bool HasExtraEuler(ImportBone bone) => bone.ExtraEuler.Any(v => v != 0f);

    private static int BoneRecordSize(ImportBone bone)
        => HasExtraEuler(bone) ? BoneData.LongRecordSize : BoneData.ShortRecordSize;

    /// <summary>
    /// The euler a bone record stores: the bone's rotation with its extra euler factors
    /// divided out, since the runtime composes A * Euler * B.
    /// </summary>
    private static Vector3 RecordEuler(ImportBone bone)
    {
        if (!HasExtraEuler(bone)) return RuntimeMath.QuaternionToEulerZyx(bone.Rotation);
        var e = bone.ExtraEuler;
        var inner = GLTFAnimationReader.UnsandwichRotation(
            bone.Rotation,
            Sampler.EulerToQuaternion(e[0], e[1], e[2]),
            Sampler.EulerToQuaternion(e[3], e[4], e[5]));
        return RuntimeMath.QuaternionToEulerZyx(inner);
    }

    /// <summary>
    /// Bounding sphere: world AABB center of all vertices, expressed in the
    /// geometry bone's local frame, since culling transforms the stored center
    /// by that node's world matrix.
    /// </summary>
    private static (Vector3 Center, float Radius) ComputeSphere(ImportScene scene, ImportBone geometryBone)
    {
        if (scene.Vertices.Count == 0) return (Vector3.Zero, 1f);
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var v in scene.Vertices)
        {
            min = Vector3.Min(min, v.WorldPosition);
            max = Vector3.Max(max, v.WorldPosition);
        }
        var worldCenter = (min + max) * 0.5f;
        float radius = 0f;
        foreach (var v in scene.Vertices)
            radius = MathF.Max(radius, Vector3.Distance(worldCenter, v.WorldPosition));
        var localCenter = RuntimeMath.TransformPoint(geometryBone.InverseWorld, worldCenter);
        return (localCenter, radius * 1.05f + 0.01f);
    }

    /// <summary>
    /// Estimated scene-node buffer use plus slack, stored in type9.u32[5]. The runtime
    /// allocates that figure + 1024 bytes and hangs when the scene overruns it.
    /// </summary>
    private static int ComputeSceneBudget(int boneCount, int rcBytes, int vaCount)
    {
        int use = 28
            + boneCount * BoneData.LongRecordSize
            + ModelRecord.Size
            + Align.Up(rcBytes, 4)
            + 8
            + 4 + VASetupRecord.Size * vaCount;
        return use + 512;
    }

    /// <summary>
    /// Writes a fixed-width ASCII name field, truncating at maxLen. Neither name field
    /// needs a NUL terminator, so a name may fill its field exactly.
    /// </summary>
    private static void WriteName(byte[] d, int offset, string name, int maxLen)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        int len = Math.Min(bytes.Length, maxLen);
        Array.Copy(bytes, 0, d, offset, len);
    }

    private static void WriteVector3(byte[] d, int offset, Vector3 v)
    {
        BigEndian.WriteFloat(d, offset, v.X);
        BigEndian.WriteFloat(d, offset + 4, v.Y);
        BigEndian.WriteFloat(d, offset + 8, v.Z);
    }
}
