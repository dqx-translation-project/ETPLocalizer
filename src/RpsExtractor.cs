using System.Buffers.Binary;

namespace ETPLocalizer;

internal sealed record RpsEntry(int SectionIndex, string FileName, byte[]? Evtx, string? SkipReason);

internal static class RpsExtractor
{
    public const string RpsLogicalPath =
        "common/data/packresource/ja/current/packageManagerRegistIncludeAutoClient.rps";
    public const string RpsFileName =
        "packageManagerRegistIncludeAutoClient.rps";

    private const string PkgSectionName = "ManagedPackageDataClient.win32";

    // bfKeys parameter kept for API compatibility but unused — CRY sections use pkg keys.
    public static (byte[]? rpsRaw, List<RpsEntry> entries) ExtractFromDat(
        DatFile dat, IdxFile idx, Dictionary<uint, byte[]> bfKeys)
    {
        var loc = idx.LookupPath(RpsLogicalPath);
        if (loc is null) return (null, []);

        byte[]? rpsRaw = dat.ReadBlock(loc.Value.track, loc.Value.blockNum);
        if (rpsRaw is null) return (null, []);

        SedbresFile sedbres;
        try { sedbres = new SedbresFile(rpsRaw); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  WARN  RPS parse failed: {ex.Message}");
            return (rpsRaw, []);
        }

        // Load Blowfish keys from the pkg file bundled inside the same RPS
        var pkgKeys = LoadPkgKeys(sedbres);

        var entries = new List<RpsEntry>();
        foreach (var sec in sedbres.ResourceSections)
        {
            if (sec.TypeTag != "etp" && sec.TypeTag != "cry") continue;

            string fileName = DeriveEtpName(sec);

            // Check actual data magic — some etp-typed sections contain CRY-encrypted data.
            // Python detects this the same way (magic bytes take precedence over RESOURCE_TYPE).
            if (IsCry(sec.Data))
            {
                // Try pkg keys first, then fall back to dat_db.db key for this filename.
                // Same blowfish key works for CRY wrapper regardless of the eventText wrapper format.
                var candidates = new List<byte[]>(pkgKeys);
                uint nameHash = DqxHash.HashString(fileName, fileName.Length);
                if (bfKeys.TryGetValue(nameHash, out byte[]? dbKey))
                    candidates.Add(dbKey);
                var (evtx, reason) = TryDecryptCry(sec.Data, candidates);
                entries.Add(new RpsEntry(sec.Index, fileName, evtx, reason));
            }
            else if (IsEvtx(sec.Data))
            {
                entries.Add(new RpsEntry(sec.Index, fileName, sec.Data, null));
            }
            else
            {
                entries.Add(new RpsEntry(sec.Index, fileName, null,
                    $"unexpected magic: {MagicStr(sec.Data)}"));
            }
        }

        return (rpsRaw, entries);
    }

    // Rebuild the RPS replacing etp-type sections with new EVTX content.
    // CRY sections that have a translation are replaced with plain EVTX; untranslated CRY sections are left unchanged.
    public static byte[] RebuildRps(byte[] originalRps, IReadOnlyDictionary<string, byte[]> modifiedEtps)
    {
        var sedbres = new SedbresFile(originalRps);

        var replacements = new Dictionary<int, byte[]>();
        foreach (var sec in sedbres.ResourceSections)
        {
            if (sec.TypeTag != "etp") continue;
            string name = DeriveEtpName(sec);
            if (modifiedEtps.TryGetValue(name, out byte[]? newEvtx))
                replacements[sec.Index] = newEvtx; // replace CRY sections with plain EVTX; game accepts both
            // if no replacement, CRY sections stay unchanged (re-encryption not supported)
        }

        return sedbres.Rebuild(replacements);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private static List<byte[]> LoadPkgKeys(SedbresFile sedbres)
    {
        var pkgSec = sedbres.Sections.FirstOrDefault(s =>
        {
            string n = s.Name.Replace('\\', '/').TrimStart('/');
            int slash = n.LastIndexOf('/');
            string basename = slash >= 0 ? n[(slash + 1)..] : n;
            return basename.Equals(PkgSectionName, StringComparison.OrdinalIgnoreCase);
        });
        if (pkgSec is null || pkgSec.Data.Length == 0) return [];
        try { return ManagedPkg.LoadKeys(pkgSec.Data); }
        catch { return []; }
    }

    private static (byte[]? evtx, string? reason) TryDecryptCry(byte[] data, List<byte[]> keys)
    {
        // CRY header: magic(4) + outer_size(4) + version(4) + unk0(4) + inner_payload_size(4) = 20 bytes
        // outer_size = inner_payload_size + 4; encrypted data starts at offset 20
        if (data.Length < 24)
            return (null, $"CRY block too short ({data.Length} bytes)");

        int payloadSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        if (payloadSize < 8 || payloadSize % 8 != 0 || 20 + payloadSize > data.Length)
            return (null, $"CRY payloadSize {payloadSize} out of bounds");

        byte[] encrypted = data[20..(20 + payloadSize)];

        if (keys.Count == 0)
            return (null, "CRY: no pkg keys available");

        foreach (var key in keys)
        {
            var bf = new DqxBlowfish();
            bf.Init(false, key);
            byte[] decrypted = bf.Transform(encrypted);
            if (IsEvtx(decrypted)) return (decrypted, null);
        }

        return (null, $"CRY: no matching key in pkg ({keys.Count} candidates tried)");
    }

    // Derive the output ETP filename from a section's raw name.
    // Path table names may have leading separators, subdirs, or .win32 extension; normalise to bare .etp name.
    public static string DeriveEtpName(SedbresSection sec)
    {
        // Normalise separators then take the bare filename (like Python's .replace("\\","/").lstrip("/"))
        string name = sec.Name.Replace('\\', '/').TrimStart('/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        if (name.EndsWith(".win32", StringComparison.OrdinalIgnoreCase))
            return name[..^".win32".Length] + ".etp";
        if (!name.EndsWith(".etp", StringComparison.OrdinalIgnoreCase))
            return name + ".etp";
        return name;
    }

    private static bool IsEvtx(byte[] d) =>
        d.Length >= 4 && d[0] == 'E' && d[1] == 'V' && d[2] == 'T' && d[3] == 'X';

    private static bool IsCry(byte[] d) =>
        d.Length >= 4 && d[0] == 'C' && d[1] == 'R' && d[2] == 'Y' && d[3] == '\t';

    private static string MagicStr(byte[] d) =>
        d.Length >= 4 ? $"{d[0]:X2}{d[1]:X2}{d[2]:X2}{d[3]:X2}" : "(too short)";
}
