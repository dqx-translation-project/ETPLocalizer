using System.Buffers.Binary;
using System.Text;

namespace ETPLocalizer;

internal sealed class SedbresSection
{
    public int    Index;
    public int    Offset;
    public int    Size;
    public int    Flags;
    public string Name    = "";
    public string TypeTag = "";
    public byte[] Data    = [];
}

internal sealed class SedbresFile
{
    private static readonly byte[] Magic = "SEDBRES "u8.ToArray();

    public int    EntryCount;
    public int    FileBaseOffset;
    public int    PathTableOffset;
    public List<SedbresSection> Sections = [];

    private readonly byte[] _raw;

    public SedbresFile(byte[] data)
    {
        if (!data.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException($"Not a SEDBRES block (magic={Encoding.ASCII.GetString(data, 0, Math.Min(8, data.Length))})");

        _raw = data;

        int formatVer   = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x08));
        PathTableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x24));
        EntryCount      = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x28));

        int alignment  = formatVer >= 4103 ? 64 : formatVer >= 4003 ? 32 : 1;
        FileBaseOffset = (0x30 + 16 * EntryCount + alignment - 1) & ~(alignment - 1);

        // Parse index table (one 16-byte row per entry)
        Sections = new List<SedbresSection>(EntryCount);
        for (int i = 0; i < EntryCount; i++)
        {
            int e = 0x30 + i * 16;
            Sections.Add(new SedbresSection
            {
                Index  = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e)),
                Offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 4)),
                Size   = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 8)),
                Flags  = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 12)),
            });
        }

        // Read names from path table (null-terminated strings, one per entry)
        int pos = FileBaseOffset + PathTableOffset;
        for (int i = 0; i < EntryCount && pos < data.Length; i++)
        {
            int nullIdx = Array.IndexOf(data, (byte)0, pos);
            int end = nullIdx >= 0 ? nullIdx : data.Length;
            Sections[i].Name = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = (nullIdx >= 0 ? nullIdx : data.Length) + 1;
        }

        // Read type tags from the special RESOURCE_TYPE entry.
        // Its data is a flat array of 4-byte reversed type strings, one per entry.
        int rtIdx = Sections.FindIndex(s => s.Name == "RESOURCE_TYPE");
        if (rtIdx >= 0)
        {
            int rtDataPos = FileBaseOffset + Sections[rtIdx].Offset;
            for (int i = 0; i < EntryCount; i++)
            {
                int p = rtDataPos + i * 4;
                if (p + 4 > data.Length) break;
                // Bytes are stored reversed; strip leading null chars after reversing.
                var tag = new char[4];
                for (int j = 0; j < 4; j++) tag[j] = (char)data[p + (3 - j)];
                Sections[i].TypeTag = new string(tag).Trim('\0');
            }
        }

        // Populate section data
        for (int i = 0; i < EntryCount; i++)
        {
            var s = Sections[i];
            int start = FileBaseOffset + s.Offset;
            int end   = start + s.Size;
            if (start >= 0 && end <= data.Length)
                s.Data = data[start..end];
        }
    }

    // Non-metadata sections (skip RESOURCE_ID and RESOURCE_TYPE)
    public IEnumerable<SedbresSection> ResourceSections =>
        Sections.Where(s => s.Name != "RESOURCE_ID" && s.Name != "RESOURCE_TYPE");

    // Rebuild the RPS replacing sections whose Index is in replacements.
    // Preserves the original physical layout (slot sizes and inter-section gaps) so that
    // sections can be replaced with same-or-smaller content without changing file size.
    // Falls back to tight packing only if a replacement grows beyond its original slot.
    public byte[] Rebuild(IReadOnlyDictionary<int, byte[]> replacements)
    {
        // Sort sections by original offset to maintain physical layout order
        var ordered = Sections.OrderBy(s => s.Offset).ToList();
        var newDatas = ordered.Select(s => replacements.TryGetValue(s.Index, out var d) ? d : s.Data).ToList();

        // Compute original slot for each section (space from its offset to the next section's offset,
        // or to PathTableOffset for the last section). This includes inter-section gaps.
        var slotSizes = new int[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            int nextOff = i + 1 < ordered.Count ? ordered[i + 1].Offset : PathTableOffset;
            slotSizes[i] = nextOff - ordered[i].Offset;
        }

        // Prefer preserving original offsets. Fall back to tight packing only if a replacement
        // grew beyond its original slot (rare in practice for translation work).
        bool fitInSlots = Enumerable.Range(0, ordered.Count).All(i => newDatas[i].Length <= slotSizes[i]);

        var newOffsets = new int[ordered.Count];
        int newDataAreaSize;

        if (fitInSlots)
        {
            for (int i = 0; i < ordered.Count; i++) newOffsets[i] = ordered[i].Offset;
            newDataAreaSize = PathTableOffset;
        }
        else
        {
            int cur = 0;
            for (int i = 0; i < ordered.Count; i++) { newOffsets[i] = cur; cur += newDatas[i].Length; }
            newDataAreaSize = cur;
        }

        // Path table bytes live after all section data in the original file
        int pathTableAbsOff = FileBaseOffset + PathTableOffset;
        var pathTableBytes = _raw.AsSpan(pathTableAbsOff).ToArray();
        int newPathTableOffset = newDataAreaSize;

        int totalSize = FileBaseOffset + newDataAreaSize + pathTableBytes.Length;
        var output = new byte[totalSize];

        // Header: copy [0..0x10), patch file_size, copy [0x14..0x24), patch path_table_offset, copy [0x28..0x30)
        _raw.AsSpan(0, 0x10).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x10), (uint)totalSize);
        _raw.AsSpan(0x14, 0x10).CopyTo(output.AsSpan(0x14));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x24), (uint)newPathTableOffset);
        _raw.AsSpan(0x28, 8).CopyTo(output.AsSpan(0x28));

        // Alignment padding between index table and file base (preserve original bytes)
        int idxTableEnd = 0x30 + 16 * EntryCount;
        if (idxTableEnd < FileBaseOffset)
            _raw.AsSpan(idxTableEnd, FileBaseOffset - idxTableEnd).CopyTo(output.AsSpan(idxTableEnd));

        // Build lookup: section Index → new offset and new size
        var newOffsetByIndex = new Dictionary<int, int>();
        var newSizeByIndex   = new Dictionary<int, int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            newOffsetByIndex[ordered[i].Index] = newOffsets[i];
            newSizeByIndex[ordered[i].Index]   = newDatas[i].Length;
        }

        // Section entries (index table keeps original order in the table)
        for (int i = 0; i < Sections.Count; i++)
        {
            int eOff = 0x30 + i * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(eOff),      (uint)Sections[i].Index);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(eOff + 4),  (uint)newOffsetByIndex[Sections[i].Index]);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(eOff + 8),  (uint)newSizeByIndex[Sections[i].Index]);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(eOff + 12), (uint)Sections[i].Flags);
        }

        // Write section data in physical order; preserve original gap bytes when keeping original layout
        if (fitInSlots)
        {
            // Copy the entire original data area (preserving all gaps), then overwrite changed sections
            _raw.AsSpan(FileBaseOffset, PathTableOffset).CopyTo(output.AsSpan(FileBaseOffset));
            for (int i = 0; i < ordered.Count; i++)
            {
                if (replacements.ContainsKey(ordered[i].Index))
                    newDatas[i].CopyTo(output.AsSpan(FileBaseOffset + newOffsets[i]));
            }
        }
        else
        {
            int writePos = FileBaseOffset;
            foreach (var d in newDatas) { d.CopyTo(output.AsSpan(writePos)); writePos += d.Length; }
        }

        // Write path table
        pathTableBytes.CopyTo(output.AsSpan(FileBaseOffset + newPathTableOffset));

        return output;
    }
}
