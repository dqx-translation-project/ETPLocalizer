using System.Buffers.Binary;
using System.Text;

namespace ETPLocalizer.Wii;

internal sealed class WiiParsedEvtx
{
    public int EntryCount;
    public int OrigEntryCount;
    public int MinIndex, MaxIndex;

    public List<KeyValuePair<int, string>> Strings = [];

    public byte[] Raw = [];
    public int CmnhEcAbs;
    public int BljaDszAbs;
    public int IndxDszAbs;
    public int IndxDataAbs, IndxDataEnd;
    public int TextDszAbs;
    public int TextDataAbs, TextDataEnd;
}

internal static class WiiEvtxParser
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

    private static uint ReadU32(byte[] raw, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset));

    private static void WriteU32(byte[] raw, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(offset), value);

    public static WiiParsedEvtx Parse(byte[] raw)
    {
        if (raw.Length < 16 || raw[0] != 'X' || raw[1] != 'T' || raw[2] != 'V' || raw[3] != 'E')
            throw new InvalidDataException($"Not Wii XTVE data (magic={Encoding.ASCII.GetString(raw, 0, Math.Min(4, raw.Length))})");

        // Big-endian section tags are byte-reversed
        ReadOnlySpan<byte> magicCmnh = "HNMC"u8;
        ReadOnlySpan<byte> magicBlja = "AJLB"u8;
        ReadOnlySpan<byte> magicIndx = "XDNI"u8;
        ReadOnlySpan<byte> magicText = "TXET"u8;

        int offCmnh = (int)ReadU32(raw, 4);
        if (!raw.AsSpan(offCmnh, 4).SequenceEqual(magicCmnh))
            throw new InvalidDataException($"Expected HNMC at 0x{offCmnh:X}");

        int cmnhDoff = (int)ReadU32(raw, offCmnh + 4);
        int cmnhDsz  = (int)ReadU32(raw, offCmnh + 8);
        int cmnhDataAbs = offCmnh + cmnhDoff;

        int entryCount = (int)ReadU32(raw, cmnhDataAbs + 4);
        int minIdx     = (int)ReadU32(raw, cmnhDataAbs + 8);
        int maxIdx     = (int)ReadU32(raw, cmnhDataAbs + 12);

        // Wii always uses EventText layout regardless of filename

        int offBlja = offCmnh + cmnhDoff + cmnhDsz + 16;
        if (!raw.AsSpan(offBlja, 4).SequenceEqual(magicBlja))
            throw new InvalidDataException($"Expected AJLB at 0x{offBlja:X}, got {Encoding.ASCII.GetString(raw, offBlja, 4)}");

        int bljaDoff = (int)ReadU32(raw, offBlja + 4);
        int bljaDataAbs = offBlja + bljaDoff;

        int indxAbs = bljaDataAbs;
        if (!raw.AsSpan(indxAbs, 4).SequenceEqual(magicIndx))
        {
            int found = ScanMagic(raw, indxAbs, magicIndx, 32);
            if (found < 0) throw new InvalidDataException($"XDNI not found near 0x{indxAbs:X}");
            indxAbs = found;
        }

        int indxDoff = (int)ReadU32(raw, indxAbs + 4);
        int indxDsz  = (int)ReadU32(raw, indxAbs + 8);
        int indxDataAbs = indxAbs + indxDoff;

        int textSearchFrom = indxDataAbs + indxDsz;
        int textAbs = ScanMagic(raw, textSearchFrom, magicText, 64);
        if (textAbs < 0) throw new InvalidDataException($"TXET not found after 0x{textSearchFrom:X}");

        int textDoff = (int)ReadU32(raw, textAbs + 4);
        int textDsz  = (int)ReadU32(raw, textAbs + 8);
        int textDataAbs = textAbs + textDoff;

        // INDX: [u32 string_id][u32 byte_offset] × n, skip id==0
        var strings = new List<KeyValuePair<int, string>>();
        int n = indxDsz / 8;
        strings.Capacity = n;
        for (int i = 0; i < n; i++)
        {
            int sid  = (int)ReadU32(raw, indxDataAbs + i * 8);
            int toff = (int)ReadU32(raw, indxDataAbs + i * 8 + 4);
            if (sid == 0) continue;
            string s;
            try { s = ReadCStr(raw, textDataAbs + toff); }
            catch { s = ""; }
            strings.Add(new KeyValuePair<int, string>(sid, s));
        }

        return new WiiParsedEvtx
        {
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

    public static byte[] Build(WiiParsedEvtx parsed, IReadOnlyList<KeyValuePair<int, string>> newStrings)
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

        byte[] newIndx = BuildIndxBytes(indxEntries);
        byte[] newText = ConcatBytes(textParts);
        int n = indxEntries.Count;

        byte[] result = SpliceAndPatch(parsed, newIndx, newText);

        if (parsed.EntryCount == parsed.OrigEntryCount)
            WriteU32(result, parsed.CmnhEcAbs, (uint)n);

        return result;
    }

    private static byte[] SpliceAndPatch(WiiParsedEvtx parsed, byte[] newIndxData, byte[] newTextData)
    {
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

        WriteU32(raw, parsed.IndxDszAbs, (uint)newIndxData.Length);
        WriteU32(raw, textDszAbs, (uint)newTextData.Length);

        // XTVE: size = file_size - 32 (after XTVE header + CMNH section header)
        WriteU32(raw, 8, (uint)(raw.Length - 32));

        uint oldBljaDsz = ReadU32(raw, parsed.BljaDszAbs);
        WriteU32(raw, parsed.BljaDszAbs, (uint)(oldBljaDsz + deltaIndx + deltaText));

        return raw;
    }

    private static byte[] BuildIndxBytes(List<(int key, int offset)> entries)
    {
        var buf = new byte[entries.Count * 8];
        for (int i = 0; i < entries.Count; i++)
        {
            WriteU32(buf, i * 8,     (uint)entries[i].key);
            WriteU32(buf, i * 8 + 4, (uint)entries[i].offset);
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
