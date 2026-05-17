using System.Buffers.Binary;
using System.IO.Compression;

namespace ETPLocalizer;

internal sealed class DatFile
{
    private const uint CryMagic = 0x09595243;
    private readonly string[] _tracks;

    private DatFile(string[] tracks) => _tracks = tracks;

    public static DatFile Open(string dataDir, string nameBase)
    {
        var tracks = new List<string>();
        for (int i = 0; ; i++)
        {
            string p = Path.Combine(dataDir, $"{nameBase}.dat{i}");
            if (!File.Exists(p)) break;
            tracks.Add(p);
        }
        if (tracks.Count == 0)
            throw new FileNotFoundException($"No dat files found for {nameBase}");
        return new DatFile(tracks.ToArray());
    }

    // Returns null for CRY (Blowfish) encrypted blocks — caller must handle.
    public byte[]? ReadBlock(int track, int blockNum)
    {
        string path = _tracks[track];
        long blockBase = (long)blockNum * 128;

        using var f = File.OpenRead(path);

        f.Seek(blockBase, SeekOrigin.Begin);
        var hdr4 = new byte[4];
        f.ReadExactly(hdr4);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr4);

        f.Seek(blockBase, SeekOrigin.Begin);
        var hdr = new byte[headerSize];
        f.ReadExactly(hdr);

        uint blockType  = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
        uint decompSz   = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
        uint datBlocks  = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(20));

        long payloadOff = blockBase + headerSize;
        int compBufSz = (int)(datBlocks * 128);

        f.Seek(payloadOff, SeekOrigin.Begin);
        var compBuf = new byte[compBufSz];
        f.ReadExactly(compBuf);

        if (compBuf.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(compBuf) == CryMagic)
            return null;

        if (blockType == 0) return compBuf;

        if (blockType == 1)
        {
            using var ms = new MemoryStream(compBuf);
            using var zs = new ZLibStream(ms, CompressionMode.Decompress);
            var result = new byte[decompSz];
            zs.ReadExactly(result);
            return result;
        }

        if (blockType == 2)
            return ReadType2(hdr, compBuf, (int)entryCount);

        return compBuf;
    }

    private static byte[] ReadType2(byte[] hdr, byte[] compBuf, int entryCount)
    {
        var parts = new List<byte[]>(entryCount);
        int bufOff = 0;

        for (int e = 0; e < entryCount; e++)
        {
            int compSpan = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(28 + e * 8));

            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(compBuf.AsSpan(bufOff));
            uint compSize   = BinaryPrimitives.ReadUInt32LittleEndian(compBuf.AsSpan(bufOff + 8));
            uint decompSize = BinaryPrimitives.ReadUInt32LittleEndian(compBuf.AsSpan(bufOff + 12));

            int streamStart = bufOff + (int)dataOffset;

            if (compSize == 128000)
            {
                var raw = new byte[decompSize];
                compBuf.AsSpan(streamStart, (int)decompSize).CopyTo(raw);
                parts.Add(raw);
            }
            else
            {
                using var ms = new MemoryStream(compBuf, streamStart, (int)compSize);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                var chunk = new byte[decompSize];
                ds.ReadExactly(chunk);
                parts.Add(chunk);
            }

            bufOff += compSpan;
        }

        int total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int pos = 0;
        foreach (var part in parts) { part.CopyTo(result, pos); pos += part.Length; }
        return result;
    }
}
