using System.Buffers.Binary;

namespace ETPLocalizer;

// Parser for ManagedPackageDataClient.win32.pkg — the key file bundled inside the RPS.
// Contains groups of Blowfish keys, each XOR-encoded with 0x4D.
internal static class ManagedPkg
{
    public static List<byte[]> LoadKeys(byte[] data)
    {
        if (data.Length < 16) return [];

        int groupCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0));
        // header: group_count(4) + unk0(4) + unk1(4) + fmt_ver(4) = 16 bytes

        // Group header table: (unk0, unk1, keys_count, ranges_count) × group_count
        var groupHeaders = new List<(int keysCount, int rangesCount)>(groupCount);
        int pos = 16;
        for (int i = 0; i < groupCount; i++)
        {
            if (pos + 16 > data.Length) break;
            int keysCount   = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8));
            int rangesCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12));
            groupHeaders.Add((keysCount, rangesCount));
            pos += 16;
        }

        // 16-byte alignment
        int rem = pos % 16;
        if (rem != 0) pos += 16 - rem;

        // Skip group ranges table: (ranges_count / 2 * 8) bytes per group
        foreach (var (_, rangesCount) in groupHeaders)
            pos += rangesCount / 2 * 8;

        // 16-byte alignment
        rem = pos % 16;
        if (rem != 0) pos += 16 - rem;

        // Read and XOR-decode keys (each key is 16 raw bytes XOR 0x4D).
        // Each decoded byte is treated as a Unicode codepoint, then UTF-8 encoded — matching
        // Python's `"".join(chr(b ^ 0x4D) for b in raw).encode("utf-8")`. Bytes > 0x7F produce
        // 2-byte UTF-8 sequences, so the resulting key may be longer than 16 bytes.
        var keys = new List<byte[]>();
        foreach (var (keysCount, _) in groupHeaders)
        {
            for (int k = 0; k < keysCount; k++)
            {
                if (pos + 16 > data.Length) break;
                var chars = new char[16];
                for (int j = 0; j < 16; j++)
                    chars[j] = (char)(data[pos + j] ^ 0x4D);
                keys.Add(System.Text.Encoding.UTF8.GetBytes(chars));
                pos += 16;
            }
        }

        return keys;
    }
}
