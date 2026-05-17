using System.Text.Encodings.Web;
using System.Text.Json;

namespace ETPLocalizer.Wii;

internal static class WiiCommands
{
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private enum Kind { Standalone, RpsSection }

    private sealed class EtpRecord
    {
        public Kind Kind;
        public string StandalonePath = "";   // abs path under wii/<>, only set for Standalone
        public string RpsAbsPath = "";       // abs path of parent RPS, only set for RpsSection
        public string RpsRelPath = "";       // rel path of parent RPS under wii/, only set for RpsSection
        public int RpsSectionIndex;          // SEDBRES section index, only set for RpsSection
        public string SectionName = "";      // raw SEDBRES name (with backslashes), only set for RpsSection
        public byte[] Bytes = [];            // the EVTX bytes for this record
        public string JsonRelPath = "";      // path under <output>/json/, e.g. "GD/S4MJ/.../foo.wii.json"
                                              // or "GD/.../foo.wii.rps/pkg/1030/bar.wii.json"
    }

    // ── all-wii ──────────────────────────────────────────────────────────────

    public static int CmdAll(string[] args, bool verbose)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: all-wii <wii_input_dir> <output_dir>"); return 1; }

        string wiiInputDir = Path.GetFullPath(args[0]);
        string outputDir   = Path.GetFullPath(args[1]);
        string outWiiDir   = Path.Combine(outputDir, "wii");
        string outJsonDir  = Path.Combine(outputDir, "json");

        if (!Directory.Exists(wiiInputDir)) { Console.Error.WriteLine($"Not a directory: {wiiInputDir}"); return 1; }

        Directory.CreateDirectory(outWiiDir);
        Directory.CreateDirectory(outJsonDir);

        // Copy input tree → outputDir/wii for use as the rebuild reference repository.
        Console.WriteLine($"Copying {wiiInputDir} → {outWiiDir}...");
        int copied = MirrorDirectory(wiiInputDir, outWiiDir);
        Console.WriteLine($"  Copied {copied} files");

        var records = DiscoverRecords(outWiiDir, verbose);
        Console.WriteLine($"Discovered {records.Count(r => r.Kind == Kind.Standalone)} standalone ETPs " +
                          $"+ {records.Count(r => r.Kind == Kind.RpsSection)} RPS sections");

        int ok = 0, errors = 0;
        foreach (var rec in records)
        {
            try
            {
                var parsed = WiiEvtxParser.Parse(rec.Bytes);
                var jdata = EvtxToJson(parsed);
                string outPath = Path.Combine(outJsonDir, rec.JsonRelPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var f = File.Create(outPath);
                JsonSerializer.Serialize(f, jdata, JsonWriteOpts);
                ok++;
                if (verbose) Console.WriteLine($"  OK  {rec.JsonRelPath}  ({parsed.Strings.Count} strings)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR  {rec.JsonRelPath}: {ex.Message}");
                errors++;
            }
        }

        Console.WriteLine($"Wrote {ok} JSONs to {outJsonDir} ({errors} errors)");
        return errors > 0 ? 1 : 0;
    }

    // ── rebuild-wii ──────────────────────────────────────────────────────────

    public static int CmdRebuild(string[] args, bool verbose)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: rebuild-wii <input_dir> <output_dir>"); return 1; }

        string inputDir   = Path.GetFullPath(args[0]);
        string outputDir  = Path.GetFullPath(args[1]);
        string inWiiDir   = Path.Combine(inputDir, "wii");
        string inJsonDir  = Path.Combine(inputDir, "json");

        if (!Directory.Exists(inWiiDir))  { Console.Error.WriteLine($"Missing {inWiiDir} (was all-wii run?)"); return 1; }
        if (!Directory.Exists(inJsonDir)) { Console.Error.WriteLine($"Missing {inJsonDir} (was all-wii run?)"); return 1; }

        Directory.CreateDirectory(outputDir);

        var records = DiscoverRecords(inWiiDir, verbose);

        // Group RPS sections by parent RPS for batch repackaging.
        var rpsGroups = records
            .Where(r => r.Kind == Kind.RpsSection)
            .GroupBy(r => r.RpsAbsPath)
            .ToDictionary(g => g.Key, g => g.ToList());

        int etpOk = 0, etpCopied = 0, etpErr = 0;
        foreach (var rec in records.Where(r => r.Kind == Kind.Standalone))
        {
            string rel = Path.GetRelativePath(inWiiDir, rec.StandalonePath);
            string dest = Path.Combine(outputDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                string jsonPath = Path.Combine(inJsonDir, rec.JsonRelPath);
                if (File.Exists(jsonPath) && TryLoadTranslations(jsonPath, out var newStrings))
                {
                    var parsed = WiiEvtxParser.Parse(rec.Bytes);
                    var ordered = parsed.Strings
                        .Select(kv => new KeyValuePair<int, string>(kv.Key, newStrings.GetValueOrDefault(kv.Key, kv.Value)))
                        .ToList();
                    byte[] rebuilt = WiiEvtxParser.Build(parsed, ordered);
                    File.WriteAllBytes(dest, rebuilt);
                    etpOk++;
                    if (verbose) Console.WriteLine($"  ETP  {rel}  ({rebuilt.Length:N0} bytes)");
                }
                else
                {
                    File.Copy(rec.StandalonePath, dest, overwrite: true);
                    etpCopied++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR  {rel}: {ex.Message}");
                etpErr++;
            }
        }

        int rpsRebuilt = 0, rpsCopied = 0, rpsErr = 0;
        var allRpsAbs = Directory.GetFiles(inWiiDir, "*.wii.rps", SearchOption.AllDirectories);
        foreach (string rpsAbs in allRpsAbs)
        {
            string rel = Path.GetRelativePath(inWiiDir, rpsAbs);
            string dest = Path.Combine(outputDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            try
            {
                if (!rpsGroups.TryGetValue(rpsAbs, out var sectionRecords))
                {
                    File.Copy(rpsAbs, dest, overwrite: true);
                    rpsCopied++;
                    continue;
                }

                var replacements = new Dictionary<int, byte[]>();
                foreach (var rec in sectionRecords)
                {
                    string jsonPath = Path.Combine(inJsonDir, rec.JsonRelPath);
                    if (!File.Exists(jsonPath)) continue;
                    if (!TryLoadTranslations(jsonPath, out var newStrings)) continue;

                    var parsed = WiiEvtxParser.Parse(rec.Bytes);
                    var ordered = parsed.Strings
                        .Select(kv => new KeyValuePair<int, string>(kv.Key, newStrings.GetValueOrDefault(kv.Key, kv.Value)))
                        .ToList();
                    replacements[rec.RpsSectionIndex] = WiiEvtxParser.Build(parsed, ordered);
                }

                if (replacements.Count == 0)
                {
                    File.Copy(rpsAbs, dest, overwrite: true);
                    rpsCopied++;
                    continue;
                }

                byte[] originalRps = File.ReadAllBytes(rpsAbs);
                byte[] rebuilt = WiiRpsExtractor.Rebuild(originalRps, replacements);
                File.WriteAllBytes(dest, rebuilt);
                rpsRebuilt++;
                if (verbose) Console.WriteLine($"  RPS  {rel}  ({replacements.Count} sections replaced)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR  {rel}: {ex.Message}");
                rpsErr++;
            }
        }

        // Copy every other file in the wii tree verbatim (preserving the partition layout).
        int otherCopied = 0;
        foreach (string src in Directory.GetFiles(inWiiDir, "*", SearchOption.AllDirectories))
        {
            if (src.EndsWith(".wii.etp", StringComparison.OrdinalIgnoreCase)) continue;
            if (src.EndsWith(".wii.rps", StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Path.GetRelativePath(inWiiDir, src);
            string dest = Path.Combine(outputDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            otherCopied++;
        }

        Console.WriteLine($"ETPs: {etpOk} patched, {etpCopied} copied, {etpErr} errors");
        Console.WriteLine($"RPSs: {rpsRebuilt} rebuilt, {rpsCopied} copied, {rpsErr} errors");
        Console.WriteLine($"Other files copied: {otherCopied}");
        return etpErr + rpsErr > 0 ? 1 : 0;
    }

    // ── discovery ────────────────────────────────────────────────────────────

    private static List<EtpRecord> DiscoverRecords(string wiiRoot, bool verbose)
    {
        var records = new List<EtpRecord>();

        foreach (string etpPath in Directory.GetFiles(wiiRoot, "*.wii.etp", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(wiiRoot, etpPath).Replace('\\', '/');
            records.Add(new EtpRecord
            {
                Kind = Kind.Standalone,
                StandalonePath = etpPath,
                Bytes = File.ReadAllBytes(etpPath),
                JsonRelPath = SwapEtpForJson(rel),
            });
        }

        foreach (string rpsPath in Directory.GetFiles(wiiRoot, "*.wii.rps", SearchOption.AllDirectories))
        {
            string rpsRel = Path.GetRelativePath(wiiRoot, rpsPath).Replace('\\', '/');
            byte[] rpsRaw;
            try { rpsRaw = File.ReadAllBytes(rpsPath); }
            catch (Exception ex) { Console.Error.WriteLine($"  ERROR reading {rpsRel}: {ex.Message}"); continue; }

            List<WiiRpsEntry> sections;
            try { sections = WiiRpsExtractor.Extract(rpsRaw); }
            catch (Exception ex) { Console.Error.WriteLine($"  ERROR parsing {rpsRel}: {ex.Message}"); continue; }

            if (verbose) Console.WriteLine($"  RPS  {rpsRel}: {sections.Count} ETP sections");

            foreach (var sec in sections)
            {
                string secRel = SectionPathToJsonRel(sec.SectionName);
                records.Add(new EtpRecord
                {
                    Kind = Kind.RpsSection,
                    RpsAbsPath = rpsPath,
                    RpsRelPath = rpsRel,
                    RpsSectionIndex = sec.SectionIndex,
                    SectionName = sec.SectionName,
                    Bytes = sec.Evtx,
                    JsonRelPath = rpsRel + "/" + secRel,
                });
            }
        }

        return records;
    }

    // ── path helpers ─────────────────────────────────────────────────────────

    private static string SwapEtpForJson(string s) =>
        s.EndsWith(".wii.etp", StringComparison.OrdinalIgnoreCase) ? s[..^".wii.etp".Length] + ".wii.json"
        : s.EndsWith(".etp",    StringComparison.OrdinalIgnoreCase) ? s[..^".etp".Length] + ".json"
        : s + ".json";

    // Convert a raw SEDBRES section name (e.g. "pkg\\1030\\KaikyuuDataClient.wii") into a JSON
    // path component relative to its parent RPS dir (e.g. "pkg/1030/KaikyuuDataClient.wii.json").
    private static string SectionPathToJsonRel(string sectionName)
    {
        string s = sectionName.Replace('\\', '/').TrimStart('/');
        if (s.EndsWith(".wii", StringComparison.OrdinalIgnoreCase)) return s + ".json";
        if (s.EndsWith(".wii.etp", StringComparison.OrdinalIgnoreCase)) return s[..^".wii.etp".Length] + ".wii.json";
        if (s.EndsWith(".etp", StringComparison.OrdinalIgnoreCase)) return s[..^".etp".Length] + ".json";
        return s + ".json";
    }

    // ── JSON I/O ─────────────────────────────────────────────────────────────

    private static Dictionary<string, Dictionary<string, string>> EvtxToJson(WiiParsedEvtx parsed)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(parsed.Strings.Count);
        foreach (var kv in parsed.Strings)
            result[kv.Key.ToString()] = new Dictionary<string, string> { [kv.Value] = "" };
        return result;
    }

    // Read a translation JSON and return string IDs → translated text (or original JP for blank entries).
    // Returns false when the file has no non-empty translations (callers then copy the original verbatim).
    private static bool TryLoadTranslations(string jsonPath, out Dictionary<int, string> translations)
    {
        translations = new Dictionary<int, string>();
        Dictionary<string, JsonElement>? jdata;
        try { jdata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(jsonPath)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ERROR reading {jsonPath}: {ex.Message}");
            return false;
        }
        if (jdata is null) return false;

        bool anyTranslated = false;
        foreach (var (k, v) in jdata)
        {
            if (!int.TryParse(k, out int key)) continue;
            if (v.ValueKind != JsonValueKind.Object) continue;
            var prop = v.EnumerateObject().FirstOrDefault();
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            string jp = prop.Name;
            string en = prop.Value.GetString() ?? "";
            if (en.Length > 0) { translations[key] = en; anyTranslated = true; }
            else                translations[key] = jp;
        }
        return anyTranslated;
    }

    // ── tree mirroring ───────────────────────────────────────────────────────

    private static int MirrorDirectory(string srcDir, string dstDir)
    {
        int count = 0;
        foreach (string src in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, src);
            string dst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            count++;
        }
        return count;
    }
}
