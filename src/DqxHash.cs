namespace ETPLocalizer;

internal static class DqxHash
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            uint c = (uint)i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            t[i] = c;
        }
        return t;
    }

    // Case-insensitive CRC32 variant: init=0xFFFFFFFF, no final XOR, A-Z lowercased.
    public static uint HashString(string s, int length)
    {
        if (length == 0) return 0;
        uint state = 0xFFFFFFFF;
        for (int i = 0; i < length; i++)
        {
            byte c = (byte)s[i];
            if (c >= 65 && c <= 90) c += 32;
            state = CrcTable[(state ^ c) & 0xFF] ^ (state >> 8);
        }
        return state;
    }

    public static ulong HashPath(string path)
    {
        int lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
        {
            uint h = HashString(path, path.Length);
            return ((ulong)h << 32) | h;
        }
        uint dirHash  = HashString(path, lastSlash);
        uint baseHash = HashString(path[(lastSlash + 1)..], path.Length - lastSlash - 1);
        return ((ulong)dirHash << 32) | baseHash;
    }
}
