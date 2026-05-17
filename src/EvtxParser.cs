using System.Buffers.Binary;
using System.Text;

namespace ETPLocalizer;

internal enum EvtxFileType { EventText = 0, SubPackage = 1, EventText2 = 2, Smldt = 4 }

internal sealed class ParsedEvtx
{
    public EvtxFileType FileType;
    public bool BigEndian;
    public int EntryCount;      // actual parsed string count
    public int OrigEntryCount;  // raw CMNH entry_count field
    public int MinIndex, MaxIndex;

    // Ordered strings: key = string_id (eventText/smldt) or char-offset (subPackage)
    public List<KeyValuePair<int, string>> Strings = [];

    // Layout markers (absolute byte offsets into Raw) for splice-based rebuild
    public byte[] Raw = [];
    public int CmnhEcAbs;
    public int BljaDszAbs;
    public int IndxDszAbs;
    public int IndxDataAbs, IndxDataEnd;
    public int TextDszAbs;
    public int TextDataAbs, TextDataEnd;
}

internal static class EvtxParser
{
    private static int ScanMagic(byte[] data, int start, ReadOnlySpan<byte> magic, int limit = 64)
    {
        int end = Math.Min(start + limit, data.Length - 4);
        for (int off = start; off < end; off += 4)
            if (data.AsSpan(off, 4).SequenceEqual(magic)) return off;
        return -1;
    }

    private static string ReadCStr(byte[] data, int offset)
    {
        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0) end = data.Length;
        return Encoding.UTF8.GetString(data, offset, end - offset);
    }

    private static uint ReadU32(byte[] raw, int offset, bool be) =>
        be ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset))
           : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));

    private static void WriteU32(byte[] raw, int offset, uint value, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(offset), value);
        else    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
    }

    public static ParsedEvtx Parse(byte[] rawIn)
    {
        byte[] raw = rawIn;

        // Unwrap CFX\t container (little-endian, PC-only)
        if (raw.Length >= 16 && raw[0] == 'C' && raw[1] == 'F' && raw[2] == 'X' && raw[3] == '\t')
        {
            int hdrSize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8));
            raw = raw[hdrSize..];
        }

        bool be = raw.Length >= 4 && raw[0] == 'X' && raw[1] == 'T' && raw[2] == 'V' && raw[3] == 'E';

        if (raw.Length < 16 || (!be && (raw[0] != 'E' || raw[1] != 'V' || raw[2] != 'T' || raw[3] != 'X')))
            throw new InvalidDataException($"Not EVTX data (magic={Encoding.ASCII.GetString(raw, 0, Math.Min(4, raw.Length))})");

        // Section magic bytes differ by endianness (big-endian has byte-reversed tags)
        ReadOnlySpan<byte> magicCmnh = be ? "HNMC"u8 : "CMNH"u8;
        ReadOnlySpan<byte> magicBlja = be ? "AJLB"u8 : "BLJA"u8;
        ReadOnlySpan<byte> magicIndx = be ? "XDNI"u8 : "INDX"u8;
        ReadOnlySpan<byte> magicText = be ? "TXET"u8 : "TEXT"u8;

        int offCmnh = (int)ReadU32(raw, 4, be);

        if (!raw.AsSpan(offCmnh, 4).SequenceEqual(magicCmnh))
            throw new InvalidDataException($"Expected CMNH at 0x{offCmnh:X}");

        int cmnhDoff = (int)ReadU32(raw, offCmnh + 4, be);
        int cmnhDsz  = (int)ReadU32(raw, offCmnh + 8, be);
        int cmnhDataAbs = offCmnh + cmnhDoff;

        int entryCount  = (int)ReadU32(raw, cmnhDataAbs + 4,  be);
        int minIdx      = (int)ReadU32(raw, cmnhDataAbs + 8,  be);
        int maxIdx      = (int)ReadU32(raw, cmnhDataAbs + 12, be);

        // Big-endian (Wii) files all use the EventText INDX layout regardless of filename.
        // For little-endian (PC) files, byte 15 carries the file type.
        EvtxFileType fileType;
        if (be)
        {
            fileType = EvtxFileType.EventText;
        }
        else
        {
            fileType = raw[15] switch
            {
                1 => EvtxFileType.SubPackage,
                4 => EvtxFileType.Smldt,
                _ => EvtxFileType.EventText,
            };
        }

        // BLJA follows CMNH header + CMNHData + FOOT (16 bytes)
        int offBlja = offCmnh + cmnhDoff + cmnhDsz + 16;
        if (!raw.AsSpan(offBlja, 4).SequenceEqual(magicBlja))
            throw new InvalidDataException($"Expected BLJA at 0x{offBlja:X}, got {Encoding.ASCII.GetString(raw, offBlja, 4)}");

        int bljaDoff = (int)ReadU32(raw, offBlja + 4, be);
        int bljaDsz  = (int)ReadU32(raw, offBlja + 8, be);
        int bljaDataAbs = offBlja + bljaDoff;

        // INDX
        int indxAbs = bljaDataAbs;
        if (!raw.AsSpan(indxAbs, 4).SequenceEqual(magicIndx))
        {
            int found = ScanMagic(raw, indxAbs, magicIndx, 32);
            if (found < 0) throw new InvalidDataException($"INDX not found near 0x{indxAbs:X}");
            indxAbs = found;
        }

        int indxDoff = (int)ReadU32(raw, indxAbs + 4, be);
        int indxDsz  = (int)ReadU32(raw, indxAbs + 8, be);
        int indxDataAbs = indxAbs + indxDoff;

        // TEXT — scan past FOOT
        int textSearchFrom = indxDataAbs + indxDsz;
        int textAbs = ScanMagic(raw, textSearchFrom, magicText, 64);
        if (textAbs < 0) throw new InvalidDataException($"TEXT not found after 0x{textSearchFrom:X}");

        int textDoff = (int)ReadU32(raw, textAbs + 4, be);
        int textDsz  = (int)ReadU32(raw, textAbs + 8, be);
        int textDataAbs = textAbs + textDoff;

        // Build string list — format differs by file type
        var strings = new List<KeyValuePair<int, string>>();

        if (fileType == EvtxFileType.EventText || be)
        {
            // INDX: [u32 string_id][u32 byte_offset] × n, skip id==0
            // Big-endian files always use this format regardless of filename.
            int n = indxDsz / 8;
            strings.Capacity = n;
            for (int i = 0; i < n; i++)
            {
                int sid  = (int)ReadU32(raw, indxDataAbs + i * 8,     be);
                int toff = (int)ReadU32(raw, indxDataAbs + i * 8 + 4, be);
                if (sid == 0) continue;
                string s;
                try { s = ReadCStr(raw, textDataAbs + toff); }
                catch { s = ""; }
                strings.Add(new KeyValuePair<int, string>(sid, s));
            }
        }
        else if (fileType == EvtxFileType.Smldt)
        {
            // INDX header: [u16 shortIdCount][u16 shortOffCount][u32 unk][u32 longIdStart][u32 shortOffStart][u32 longOffStart]
            // Followed by parallel tables of string IDs and char-offsets (offset × 2 = byte pos in TEXT)
            int shortIdCount  = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(indxDataAbs));
            int shortOffCount = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(indxDataAbs + 2));
            int longIdStart   = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(indxDataAbs + 8));
            int shortOffStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(indxDataAbs + 12));
            int longOffStart  = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(indxDataAbs + 16));

            var allIds = new List<int>(shortIdCount);
            for (int i = 0; i < shortIdCount; i++)
                allIds.Add(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(indxDataAbs + 20 + i * 2)));
            int longIdCount = (shortOffStart - longIdStart) / 4;
            for (int i = 0; i < longIdCount; i++)
                allIds.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(indxDataAbs + longIdStart + i * 4)));

            int shortOffIdx = 0, longOffIdx = 0;
            var seen = new HashSet<int>();
            strings.Capacity = allIds.Count;
            foreach (int sid in allIds)
            {
                int offset = shortOffIdx < shortOffCount
                    ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(indxDataAbs + shortOffStart + shortOffIdx++ * 2))
                    : (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(indxDataAbs + longOffStart + longOffIdx++ * 4));

                if (!seen.Add(offset)) continue;
                string s;
                try { s = ReadCStr(raw, textDataAbs + offset * 2); }
                catch { s = ""; }
                strings.Add(new KeyValuePair<int, string>(sid, s));
            }
        }
        else // SubPackage (LE only)
        {
            // INDX: [u16 unk][u16 shortCount][16 bytes unk][short offset table][optional 0xCDAB pad][long offset table]
            // offset_count (total) from CMNH data field 3 (maxIdx); offset × 2 = byte pos in TEXT; offset itself = key
            int offsetCount = maxIdx;
            int shortCount  = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(indxDataAbs + 2));

            var shortOffsets = new List<int>(shortCount);
            for (int i = 0; i < shortCount; i++)
                shortOffsets.Add(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(indxDataAbs + 20 + i * 2)));

            int longTableStart = 20 + shortCount * 2;
            if ((indxDataAbs + longTableStart) % 4 != 0)
                longTableStart += 2; // skip 0xCDAB alignment pad

            int longCount = offsetCount - shortCount;
            var longOffsets = new List<int>(Math.Max(0, longCount));
            for (int i = 0; i < longCount; i++)
                longOffsets.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(indxDataAbs + longTableStart + i * 4)));

            var seen = new HashSet<int>();
            strings.Capacity = offsetCount;
            foreach (int offset in shortOffsets.Concat(longOffsets))
            {
                if (offset == 0 || !seen.Add(offset)) continue;
                string s;
                try { s = ReadCStr(raw, textDataAbs + offset * 2); }
                catch { s = ""; }
                strings.Add(new KeyValuePair<int, string>(offset, s));
            }
        }

        return new ParsedEvtx
        {
            FileType       = fileType,
            BigEndian      = be,
            EntryCount     = strings.Count,
            OrigEntryCount = entryCount,
            MinIndex       = minIdx,
            MaxIndex       = maxIdx,
            Strings        = strings,
            Raw            = raw,
            CmnhEcAbs      = cmnhDataAbs + 4,
            BljaDszAbs     = offBlja + 8,
            IndxDszAbs     = indxAbs + 8,
            IndxDataAbs    = indxDataAbs,
            IndxDataEnd    = indxDataAbs + indxDsz,
            TextDszAbs     = textAbs + 8,
            TextDataAbs    = textDataAbs,
            TextDataEnd    = textDataAbs + textDsz,
        };
    }

    public static byte[] Build(ParsedEvtx parsed, IReadOnlyList<KeyValuePair<int, string>> newStrings) =>
        parsed.FileType switch
        {
            EvtxFileType.Smldt      => BuildSmldt(parsed, newStrings),
            EvtxFileType.SubPackage => BuildSubPackage(parsed, newStrings),
            _                       => BuildEventText(parsed, newStrings),
        };

    private static byte[] BuildEventText(ParsedEvtx parsed, IReadOnlyList<KeyValuePair<int, string>> newStrings)
    {
        var textParts   = new List<byte[]>(newStrings.Count);
        var indxEntries = new List<(int key, int offset)>(newStrings.Count);
        int cursor = 0;
        foreach (var kv in newStrings)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(kv.Value + "\0");
            indxEntries.Add((kv.Key, cursor));
            textParts.Add(encoded);
            cursor += encoded.Length;
        }

        byte[] newIndx = BuildIndxBytes(indxEntries, parsed.BigEndian);
        byte[] newText = ConcatBytes(textParts);
        int n = indxEntries.Count;

        byte[] result = SpliceAndPatch(parsed, newIndx, newText);

        if (parsed.EntryCount == parsed.OrigEntryCount)
            WriteU32(result, parsed.CmnhEcAbs, (uint)n, parsed.BigEndian);

        return result;
    }

    private static byte[] BuildSmldt(ParsedEvtx parsed, IReadOnlyList<KeyValuePair<int, string>> newStrings)
    {
        byte[] raw = parsed.Raw;
        int dataAbs = parsed.IndxDataAbs;

        int shortIdCount  = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(dataAbs));
        int shortOffCount = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(dataAbs + 2));
        int longIdStart   = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(dataAbs + 8));
        int shortOffStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(dataAbs + 12));
        int longOffStart  = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(dataAbs + 16));

        // Read all string IDs in original order
        var allIds = new List<int>(shortIdCount);
        for (int i = 0; i < shortIdCount; i++)
            allIds.Add(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(dataAbs + 20 + i * 2)));
        int longIdCount = (shortOffStart - longIdStart) / 4;
        for (int i = 0; i < longIdCount; i++)
            allIds.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(dataAbs + longIdStart + i * 4)));

        // Read all original offsets in parallel order
        var origOffsets = new List<int>(allIds.Count);
        int si = 0, li = 0;
        for (int i = 0; i < allIds.Count; i++)
        {
            int off = si < shortOffCount
                ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(dataAbs + shortOffStart + si++ * 2))
                : (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(dataAbs + longOffStart + li++ * 4));
            origOffsets.Add(off);
        }

        var stringMap = new Dictionary<int, string>(newStrings.Count);
        foreach (var kv in newStrings) stringMap[kv.Key] = kv.Value;

        // First string ID per unique original offset is the primary (dedup owner)
        var origOffToPrimary = new Dictionary<int, int>();
        for (int i = 0; i < allIds.Count; i++)
            origOffToPrimary.TryAdd(origOffsets[i], allIds[i]);

        // Build TEXT — even-padded strings, char offsets (byte pos / 2)
        var textBuf = new MemoryStream();
        var origOffToNewCharOff = new Dictionary<int, int>();
        var seenOff = new HashSet<int>();

        for (int i = 0; i < allIds.Count; i++)
        {
            int origOff = origOffsets[i];
            if (!seenOff.Add(origOff)) continue;
            int primaryId = origOffToPrimary[origOff];
            byte[] encoded = Encoding.UTF8.GetBytes(stringMap.GetValueOrDefault(primaryId, "") + "\0");
            if (encoded.Length % 2 != 0) Array.Resize(ref encoded, encoded.Length + 1);
            origOffToNewCharOff[origOff] = (int)(textBuf.Length / 2);
            textBuf.Write(encoded);
        }
        byte[] newText = textBuf.ToArray();

        // Build INDX
        var indx = new MemoryStream();
        indx.Write(new byte[20]); // header placeholder — patched below

        indx.Write(raw, dataAbs + 20, shortIdCount * 2); // short string IDs verbatim
        if (indx.Length % 4 != 0) indx.Write(new byte[] { 0xCD, 0xAB });
        long longStrStart_new = indx.Length;

        indx.Write(raw, dataAbs + longIdStart, longIdCount * 4); // long string IDs verbatim
        long shortOffStart_new = indx.Length;

        bool wroteDiv = false;
        long shortOffEnd = 0, longOffStart_new = 0;
        for (int i = 0; i < allIds.Count; i++)
        {
            int newOff = origOffToNewCharOff[origOffsets[i]];
            if (newOff <= 65535 && !wroteDiv)
            {
                indx.WriteByte((byte)(newOff & 0xFF));
                indx.WriteByte((byte)(newOff >> 8));
            }
            else
            {
                if (!wroteDiv)
                {
                    shortOffEnd = indx.Length;
                    if (indx.Length % 4 != 0) indx.Write(new byte[] { 0xCD, 0xAB });
                    longOffStart_new = indx.Length;
                    wroteDiv = true;
                }
                var b4 = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)newOff);
                indx.Write(b4);
            }
        }
        if (!wroteDiv) { shortOffEnd = indx.Length; longOffStart_new = indx.Length; }

        while (indx.Length % 16 != 0) indx.WriteByte(0);

        byte[] indxBytes = indx.ToArray();
        int shortOffCount_new = (int)((shortOffEnd - shortOffStart_new) / 2);
        BinaryPrimitives.WriteUInt16LittleEndian(indxBytes.AsSpan(0), (ushort)shortIdCount);
        BinaryPrimitives.WriteUInt16LittleEndian(indxBytes.AsSpan(2), (ushort)shortOffCount_new);
        raw.AsSpan(dataAbs + 4, 4).CopyTo(indxBytes.AsSpan(4)); // preserve unk
        BinaryPrimitives.WriteUInt32LittleEndian(indxBytes.AsSpan(8),  (uint)longStrStart_new);
        BinaryPrimitives.WriteUInt32LittleEndian(indxBytes.AsSpan(12), (uint)shortOffStart_new);
        BinaryPrimitives.WriteUInt32LittleEndian(indxBytes.AsSpan(16), (uint)longOffStart_new);

        return SpliceAndPatch(parsed, indxBytes, newText);
    }

    private static byte[] BuildSubPackage(ParsedEvtx parsed, IReadOnlyList<KeyValuePair<int, string>> newStrings)
    {
        byte[] raw = parsed.Raw;
        int dataAbs = parsed.IndxDataAbs;
        int offsetCount = parsed.MaxIndex;

        int shortCount = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(dataAbs + 2));
        var origShortOffs = new List<int>(shortCount);
        for (int i = 0; i < shortCount; i++)
            origShortOffs.Add(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(dataAbs + 20 + i * 2)));

        int longTableStart = 20 + shortCount * 2;
        if ((dataAbs + longTableStart) % 4 != 0) longTableStart += 2;
        int longCount = offsetCount - shortCount;
        var origLongOffs = new List<int>(Math.Max(0, longCount));
        for (int i = 0; i < longCount; i++)
            origLongOffs.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(dataAbs + longTableStart + i * 4)));

        var stringMap = new Dictionary<int, string>(newStrings.Count);
        foreach (var kv in newStrings) stringMap[kv.Key] = kv.Value;

        // TEXT: 2-byte null at char 0, then even-padded strings starting at char 1
        var textBuf = new MemoryStream();
        textBuf.Write(new byte[2]);
        var origOffToNewCharOff = new Dictionary<int, int>();
        foreach (var kv in newStrings)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(kv.Value + "\0");
            if (encoded.Length % 2 != 0) Array.Resize(ref encoded, encoded.Length + 1);
            origOffToNewCharOff[kv.Key] = (int)(textBuf.Length / 2);
            textBuf.Write(encoded);
        }
        byte[] newText = textBuf.ToArray();

        // Build INDX: preserve 20-byte header prefix, rebuild offset tables
        var indx = new MemoryStream();
        indx.Write(raw, dataAbs, 20);

        bool wroteDiv = false;
        long shortOffEnd = 0, longOffStart_new = 0;
        foreach (int origOff in origShortOffs.Concat(origLongOffs))
        {
            if (origOff == 0)
            {
                if (!wroteDiv) { indx.WriteByte(0); indx.WriteByte(0); }
                else           { indx.Write(new byte[4]); }
                continue;
            }

            int newOff = origOffToNewCharOff.GetValueOrDefault(origOff, 0);
            if (newOff <= 65535 && !wroteDiv)
            {
                indx.WriteByte((byte)(newOff & 0xFF));
                indx.WriteByte((byte)(newOff >> 8));
            }
            else
            {
                if (!wroteDiv)
                {
                    shortOffEnd = indx.Length;
                    if (indx.Length % 4 != 0) indx.Write(new byte[] { 0xCD, 0xAB });
                    longOffStart_new = indx.Length;
                    wroteDiv = true;
                }
                var b4 = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)newOff);
                indx.Write(b4);
            }
        }
        if (!wroteDiv) { shortOffEnd = indx.Length; longOffStart_new = indx.Length; }

        while (indx.Length % 16 != 0) indx.WriteByte(0);

        byte[] indxBytes = indx.ToArray();
        int newShortCount = (int)((shortOffEnd - 20) / 2);
        BinaryPrimitives.WriteUInt16LittleEndian(indxBytes.AsSpan(2), (ushort)newShortCount);
        indxBytes[4] = 0; indxBytes[5] = 0; // mirror smilelib: u32 write zeros bytes 4-5
        BinaryPrimitives.WriteUInt32LittleEndian(indxBytes.AsSpan(16), (uint)longOffStart_new);

        return SpliceAndPatch(parsed, indxBytes, newText);
    }

    private static byte[] SpliceAndPatch(ParsedEvtx parsed, byte[] newIndxData, byte[] newTextData)
    {
        bool be = parsed.BigEndian;

        int indxPad = (16 - newIndxData.Length % 16) % 16;
        if (indxPad > 0) Array.Resize(ref newIndxData, newIndxData.Length + indxPad);

        int textPad = (16 - newTextData.Length % 16) % 16;
        if (textPad > 0) Array.Resize(ref newTextData, newTextData.Length + textPad);

        int iStart = parsed.IndxDataAbs;
        int iEnd   = parsed.IndxDataEnd;
        byte[] raw = Splice(parsed.Raw, iStart, iEnd, newIndxData);
        int deltaIndx = newIndxData.Length - (iEnd - iStart);

        int tStart     = parsed.TextDataAbs + deltaIndx;
        int tEnd       = parsed.TextDataEnd + deltaIndx;
        int textDszAbs = parsed.TextDszAbs  + deltaIndx;

        raw = Splice(raw, tStart, tEnd, newTextData);
        int deltaText = newTextData.Length - (tEnd - tStart);

        WriteU32(raw, parsed.IndxDszAbs, (uint)newIndxData.Length, be);
        WriteU32(raw, textDszAbs, (uint)newTextData.Length, be);

        // EVTX: size = file_size - 16 (after EVTX header)
        // XTVE: size = file_size - 32 (after XTVE header + CMNH section header)
        WriteU32(raw, 8, (uint)(raw.Length - (be ? 32 : 16)), be);

        uint oldBljaDsz = ReadU32(raw, parsed.BljaDszAbs, be);
        WriteU32(raw, parsed.BljaDszAbs, (uint)(oldBljaDsz + deltaIndx + deltaText), be);

        return raw;
    }

    private static byte[] BuildIndxBytes(List<(int key, int offset)> entries, bool be)
    {
        var buf = new byte[entries.Count * 8];
        for (int i = 0; i < entries.Count; i++)
        {
            WriteU32(buf, i * 8,     (uint)entries[i].key,    be);
            WriteU32(buf, i * 8 + 4, (uint)entries[i].offset, be);
        }
        return buf;
    }

    private static byte[] ConcatBytes(List<byte[]> parts)
    {
        int total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }

    private static byte[] Splice(byte[] src, int start, int end, byte[] replacement)
    {
        var result = new byte[src.Length - (end - start) + replacement.Length];
        src.AsSpan(0, start).CopyTo(result);
        replacement.CopyTo(result, start);
        src.AsSpan(end).CopyTo(result.AsSpan(start + replacement.Length));
        return result;
    }
}
