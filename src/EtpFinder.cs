using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace ETPLocalizer;

internal sealed record EtpEntry(string FileName, int Track, int BlockNum, byte[]? BlowfishKey);

internal static class EtpFinder
{
    // common/data/eventText/ja/current directory hash in data00000000.win32
    private const uint EtpDirHash = 0x719B9A66;

    private static readonly string[] SmldtNames =
    [
        "2DMAP", "BATTLE", "BG_AGENT_NAME", "BG_GIMMICK", "CAFE_PLAYMODECHANGE",
        "COMMANDWINDOW", "COMMUNICATIONWINDOW", "CONTINENTAL_NAME", "DUBBLE_SURECHIGAI",
        "EVENT", "GUILD", "HOUSING", "ITEM", "JOB", "KEYNAME", "LIVE", "LIVE_SAVE",
        "LOADING_TIPS", "LOCATIONTITLE", "MAGIC", "MAP", "MENU", "MENU_LOADING",
        "MONSTER", "NORIMONO", "NPC", "NPC_DB", "PC_SAVE_POPPOINT_NAME", "QUEST",
        "RACE", "SHOP", "SKILL", "STAGE_ID", "STORY", "SYSTEM", "TOWN", "UI",
    ];

    public static Dictionary<uint, byte[]> LoadBlowfishKeys(string dbPath)
    {
        var cache = new Dictionary<uint, byte[]>();
        if (!File.Exists(dbPath)) return cache;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // file_hash IS NOT NULL guard is essential: a keyed row with a NULL file_hash
            // would make reader.GetString(0) throw, and the catch below would silently
            // abort the whole load — dropping every key that comes after it in row order.
            cmd.CommandText = "SELECT file_hash, blowfish_key FROM files WHERE blowfish_key IS NOT NULL AND blowfish_key != '' AND file_hash IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // Defensive: never let one malformed row truncate the entire key map.
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                string hexHash = reader.GetString(0);
                string keyStr  = reader.GetString(1);
                byte[] keyBytes = Encoding.UTF8.GetBytes(keyStr);
                if (uint.TryParse(hexHash, System.Globalization.NumberStyles.HexNumber, null, out uint h)
                    && keyBytes.Length == 16)
                    cache[h] = keyBytes;
            }
        }
        catch { /* db unavailable — no keys */ }
        return cache;
    }

    private static (int track, int blockNum)? LookupEtp(IdxFile idx, string filename)
    {
        uint baseHash = DqxHash.HashString(filename, filename.Length);
        return idx.LookupEtp(EtpDirHash, baseHash);
    }

    private static List<string> LoadEventTextNames(DatFile dat, IdxFile idx)
    {
        const string xmlFile = "eventTextCategoryNo.xml";
        uint baseHash = DqxHash.HashString(xmlFile, xmlFile.Length);
        var result = idx.LookupEtp(EtpDirHash, baseHash);
        if (result is null) return [];
        var data = dat.ReadBlock(result.Value.track, result.Value.blockNum);
        if (data is null) return [];
        try
        {
            var xml = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿'));
            return xml.Root?
                .Elements("Category")
                .Select(e => e.Attribute("Name")?.Value)
                .Where(n => n is not null)
                .Cast<string>()
                .ToList() ?? [];
        }
        catch { return []; }
    }

    private static IEnumerable<string> KnownEtpFilenames(DatFile dat, IdxFile idx)
    {
        foreach (var name in LoadEventTextNames(dat, idx))
            yield return $"eventText{name}Client.etp";

        foreach (var name in SmldtNames)
            yield return $"smldt_msg_pkg_{name}.etp";

        for (int n = 1; n < 200; n++)
            yield return $"subPackage{n:D2}Client.etp";
    }

    public static List<EtpEntry> FindAll(DatFile dat, IdxFile idx, Dictionary<uint, byte[]> bfKeys)
    {
        var found = new List<EtpEntry>();
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        foreach (string fname in KnownEtpFilenames(dat, idx))
        {
            if (!seen.Add(fname)) continue;
            var loc = LookupEtp(idx, fname);
            if (loc is null) continue;
            uint baseHash = DqxHash.HashString(fname, fname.Length);
            bfKeys.TryGetValue(baseHash, out byte[]? bfKey);
            found.Add(new EtpEntry(fname, loc.Value.track, loc.Value.blockNum, bfKey));
        }

        found.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.Ordinal));
        return found;
    }

    public static (byte[]? data, string? reason) ReadEvtxBlock(DatFile dat, int track, int blockNum, byte[]? bfKey)
    {
        byte[]? data = dat.ReadBlock(track, blockNum);
        if (data is null)
            return (null, "DAT-level CRY block (blowfish encrypted at block level)");
        return TryUnwrapEvtx(data, bfKey);
    }

    // Shared unwrap/decrypt logic for both dat blocks and RPS sections.
    public static (byte[]? evtx, string? reason) TryUnwrapEvtx(byte[] data, byte[]? bfKey)
    {
        data = UnwrapCfx(data);

        if (IsEvtx(data))
            return (data, null);

        // CRY\t = packresource section encrypted with blowfish ECB.
        // Header: [magic 4B][decompSize 4B][hdrSize uint16 2B][...zeros...][encrypted block]
        if (data.Length >= 10 && data[0] == 'C' && data[1] == 'R' && data[2] == 'Y' && data[3] == '\t')
        {
            if (bfKey is null) return (null, "CRY encrypted (packresource runtime key, not extractable offline)");
            return TryUnwrapCry(data, bfKey);
        }

        // Generic blowfish: [uint32_le payload_size][ECB payload] (dat-file format)
        if (bfKey is not null)
        {
            try { data = BlowfishDecrypt(data, bfKey); }
            catch (Exception ex) { return (null, $"blowfish decryption failed: {ex.Message}"); }
            data = UnwrapCfx(data);
            if (!IsEvtx(data))
                return (null, $"unexpected magic after decrypt: {data[0]:X2}{data[1]:X2}{data[2]:X2}{data[3]:X2}");
            return (data, null);
        }

        string magic = data.Length >= 4
            ? $"{data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2}"
            : "(too short)";
        return (null, $"not EVTX (magic: {magic}), no bf key");
    }

    private static bool IsEvtx(byte[] d) =>
        d.Length >= 4 && d[0] == 'E' && d[1] == 'V' && d[2] == 'T' && d[3] == 'X';

    private static byte[] UnwrapCfx(byte[] d)
    {
        if (d.Length >= 16 && d[0] == 'C' && d[1] == 'F' && d[2] == 'X' && d[3] == '\t')
        {
            int hdrSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8));
            return d[hdrSize..];
        }
        return d;
    }

    private static (byte[]? evtx, string? reason) TryUnwrapCry(byte[] data, byte[] key)
    {
        // CRY header: magic(4) + payload_size(4) + version(4) + unk0(4) = 16 bytes fixed
        if (data.Length < 20) return (null, "CRY block too short");
        int payloadSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (payloadSize < 8 || 16 + payloadSize > data.Length)
            return (null, $"CRY payloadSize {payloadSize} out of bounds");
        byte[] encrypted = data[16..(16 + payloadSize)];
        var bf = new DqxBlowfish();
        bf.Init(false, key);
        byte[] decrypted = bf.Transform(encrypted);
        if (!IsEvtx(decrypted))
            return (null, $"CRY decrypt didn't yield EVTX: {decrypted[0]:X2} {decrypted[1]:X2} {decrypted[2]:X2} {decrypted[3]:X2}");
        return (decrypted, null);
    }

    // Format: uint32_le payload_size + blowfish ECB payload (dat-file sections)
    private static byte[] BlowfishDecrypt(byte[] data, byte[] key)
    {
        if (data.Length < 8) throw new ArgumentException("Block too short");
        int payloadSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        byte[] payload = data[4..(4 + payloadSize)];
        if (payload.Length % 8 != 0) throw new ArgumentException($"Payload size {payload.Length} not multiple of 8");
        var bf = new DqxBlowfish();
        bf.Init(false, key);
        return bf.Transform(payload);
    }
}
