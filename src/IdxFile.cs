using System.Buffers.Binary;

namespace ETPLocalizer;

internal sealed class IdxFile
{
    private readonly string _path;
    private readonly uint _blockAOff, _blockASize;
    private byte[]? _blockA;

    public IdxFile(string path)
    {
        _path = path;
        using var f = File.OpenRead(path);
        var hdr = new byte[2048];
        f.ReadExactly(hdr);

        if (hdr[0] != 'S' || hdr[1] != 'M' || hdr[2] != 'P' || hdr[3] != 'K')
            throw new InvalidDataException("Bad IDX magic");

        uint B2(int n) => BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1024 + n * 4));
        _blockAOff  = B2(2);
        _blockASize = B2(3);
    }

    private int NumPrimary => (int)(_blockASize / 16);

    private void Load()
    {
        if (_blockA != null) return;
        using var f = File.OpenRead(_path);
        f.Seek(_blockAOff, SeekOrigin.Begin);
        _blockA = new byte[_blockASize];
        f.ReadExactly(_blockA);
    }

    private int BinarySearchPrimary(ulong targetHash)
    {
        Load();
        int lo = 0, hi = NumPrimary - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            ulong h = BinaryPrimitives.ReadUInt64LittleEndian(_blockA.AsSpan(mid * 16));
            if (h == targetHash) return mid * 16;
            if (h < targetHash) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    // Direct lookup by dir_hash + base_hash (bypasses path string construction).
    public (int track, int blockNum)? LookupEtp(uint dirHash, uint baseHash)
    {
        return LookupByFullHash(((ulong)dirHash << 32) | baseHash);
    }

    // Lookup by full logical path (e.g. "common/data/packresource/ja/current/foo.rps").
    public (int track, int blockNum)? LookupPath(string path)
    {
        return LookupByFullHash(DqxHash.HashPath(path));
    }

    private (int track, int blockNum)? LookupByFullHash(ulong fullHash)
    {
        Load();
        int off = BinarySearchPrimary(fullHash);
        if (off < 0) return null;
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(_blockA!.AsSpan(off + 8));
        int track    = (int)((packed >> 1) & 7);
        int blockNum = (int)(packed >> 4);
        return (track, blockNum);
    }
}
