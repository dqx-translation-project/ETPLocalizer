using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ETPLocalizer;

class Program
{
    private const string DefaultDataDir = @"C:\Program Files (x86)\SquareEnix\DRAGON QUEST X\Game\Content\Data";
    private const string DefaultArchive = "data00000000.win32";

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }

        string dataDir = DefaultDataDir;
        string archive = DefaultArchive;
        string? dbPath = null;
        bool verbose = false;
        var rest = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if      (args[i] == "--data-dir" && i + 1 < args.Length) { dataDir = args[++i]; }
            else if (args[i].StartsWith("--data-dir="))              { dataDir = args[i]["--data-dir=".Length..]; }
            else if (args[i] == "--archive" && i + 1 < args.Length)  { archive = args[++i]; }
            else if (args[i].StartsWith("--archive="))               { archive = args[i]["--archive=".Length..]; }
            else if (args[i] == "--db" && i + 1 < args.Length)       { dbPath = args[++i]; }
            else if (args[i].StartsWith("--db="))                    { dbPath = args[i]["--db=".Length..]; }
            else if (args[i] is "-v" or "--verbose")                  { verbose = true; }
            else rest.Add(args[i]);
        }

        if (rest.Count == 0) { PrintUsage(); return 1; }

        string cmd = rest[0];
        var cmdArgs = rest.Skip(1).ToArray();

        try
        {
            return cmd switch
            {
                "dump"              => CmdDump(cmdArgs, dataDir, archive, dbPath, verbose),
                "tojson"            => CmdToJson(cmdArgs),
                "fromjson"          => CmdFromJson(cmdArgs),
                "all"               => CmdAll(cmdArgs, dataDir, archive, dbPath, verbose),
                "rebuild"           => CmdRebuild(cmdArgs, verbose),
                "hexdump"           => CmdHexdump(cmdArgs),
                "port-translations" => CmdPortTranslations(cmdArgs, verbose),
                _          => Error($"Unknown command: {cmd}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // ── dump ──────────────────────────────────────────────────────────────────

    static int CmdDump(string[] args, string dataDir, string archive, string? dbPath, bool verbose)
    {
        if (args.Length < 1) return Error("Usage: dump <output_dir>");
        string outDir = args[0];
        Directory.CreateDirectory(outDir);

        var idx = new IdxFile(Path.Combine(dataDir, $"{archive}.idx"));
        var dat = DatFile.Open(dataDir, archive);
        var bfKeys = LoadBfKeys(dbPath);
        var entries = EtpFinder.FindAll(dat, idx, bfKeys);

        Console.WriteLine($"Found {entries.Count} ETP files");
        int ok = 0;
        foreach (var e in entries)
        {
            var (evtx, skipReason) = EtpFinder.ReadEvtxBlock(dat, e.Track, e.BlockNum, e.BlowfishKey);
            if (evtx is null) { Console.WriteLine($"  SKIP  {e.FileName}  ({skipReason})"); continue; }
            File.WriteAllBytes(Path.Combine(outDir, e.FileName), evtx);
            ok++;
            if (verbose)
            {
                string enc = e.BlowfishKey is not null ? " [bf]" : "";
                Console.WriteLine($"  OK    {e.FileName}{enc} ({evtx.Length:N0} bytes)");
            }
        }
        Console.WriteLine($"Dumped {ok}/{entries.Count} files to {outDir}");
        return 0;
    }

    // ── tojson ────────────────────────────────────────────────────────────────

    static int CmdToJson(string[] args)
    {
        if (args.Length < 1) return Error("Usage: tojson <input.etp> [output.json]");
        string inputPath  = args[0];
        string outputPath = args.Length > 1 ? args[1] : inputPath + ".json";
        if (Directory.Exists(outputPath))
            outputPath = Path.Combine(outputPath, Path.GetFileName(inputPath) + ".json");

        byte[] raw = File.ReadAllBytes(inputPath);
        var parsed = EvtxParser.Parse(raw);
        var jdata = EvtxToJson(parsed);

        using var f = File.Create(outputPath);
        JsonSerializer.Serialize(f, jdata, JsonWriteOpts);
        Console.WriteLine($"Written {jdata.Count} strings to {outputPath}");
        return 0;
    }

    // ── fromjson ──────────────────────────────────────────────────────────────

    static int CmdFromJson(string[] args)
    {
        if (args.Length < 2) return Error("Usage: fromjson <input.json> <reference.etp> [output.etp]");
        string jsonPath = args[0];
        string refPath  = args[1];
        string outPath  = args.Length > 2 ? args[2] : jsonPath.Replace(".json", ".rebuilt.etp");

        var jdata  = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(jsonPath))
                     ?? throw new InvalidDataException("Empty JSON");
        var refRaw = File.ReadAllBytes(refPath);
        var parsed = EvtxParser.Parse(refRaw);
        var newStrings = JsonToStrings(jdata);

        var origKeys = new HashSet<int>(parsed.Strings.Select(kv => kv.Key));
        var newKeys  = new HashSet<int>(newStrings.Keys);
        var added   = newKeys.Except(origKeys).OrderBy(x => x).ToList();
        var removed = origKeys.Except(newKeys).OrderBy(x => x).ToList();
        if (added.Count > 0)
            Console.WriteLine($"WARNING: {added.Count} keys in JSON not in original: [{string.Join(", ", added.Take(5))}]");
        if (removed.Count > 0)
            Console.WriteLine($"WARNING: {removed.Count} keys in original missing from JSON: [{string.Join(", ", removed.Take(5))}]");

        var ordered = parsed.Strings
            .Select(kv => new KeyValuePair<int, string>(kv.Key, newStrings.GetValueOrDefault(kv.Key, kv.Value)))
            .ToList();

        byte[] outBytes = EvtxParser.Build(parsed, ordered);
        File.WriteAllBytes(outPath, outBytes);
        Console.WriteLine($"Written {outBytes.Length:N0} bytes to {outPath}");
        return 0;
    }

    // ── all ───────────────────────────────────────────────────────────────────

    static int CmdAll(string[] args, string dataDir, string archive, string? dbPath, bool verbose)
    {
        if (args.Length < 1) return Error("Usage: all <output_dir>");
        string outDir    = args[0];
        string etpDir    = Path.Combine(outDir, "etp");
        string rpsDir    = Path.Combine(outDir, "rps");
        string jsonEnDir = Path.Combine(outDir, "json", "_lang", "en");
        string jsonJaDir = Path.Combine(outDir, "json", "_lang", "ja");
        Directory.CreateDirectory(etpDir);
        Directory.CreateDirectory(rpsDir);
        Directory.CreateDirectory(jsonEnDir);
        Directory.CreateDirectory(jsonJaDir);

        var idx    = new IdxFile(Path.Combine(dataDir, $"{archive}.idx"));
        var dat    = DatFile.Open(dataDir, archive);
        var bfKeys = LoadBfKeys(dbPath);

        // ── ETP directory ─────────────────────────────────────────────────────
        var entries = EtpFinder.FindAll(dat, idx, bfKeys);
        Console.WriteLine($"Found {entries.Count} ETP files in eventText directory");
        int ok = 0, skipped = 0, errors = 0;

        foreach (var e in entries)
        {
            var (evtx, skipReason) = EtpFinder.ReadEvtxBlock(dat, e.Track, e.BlockNum, e.BlowfishKey);
            if (evtx is null) { Console.WriteLine($"  SKIP {e.FileName}  ({skipReason})"); skipped++; continue; }

            File.WriteAllBytes(Path.Combine(etpDir, e.FileName), evtx);

            ParsedEvtx parsed;
            try { parsed = EvtxParser.Parse(evtx); }
            catch (Exception ex) { Console.WriteLine($"  ERROR parsing {e.FileName}: {ex.Message}"); errors++; continue; }

            WriteJson(jsonEnDir, jsonJaDir, Path.GetFileNameWithoutExtension(e.FileName) + ".json", parsed);
            ok++;
            if (verbose) Console.WriteLine($"  OK  {e.FileName}  ({parsed.Strings.Count} strings)");
        }
        Console.WriteLine($"eventText: {ok} ok, {skipped} skipped, {errors} errors");

        // ── packresource RPS ──────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Extracting packresource RPS...");
        var (rpsRaw, rpsEntries) = RpsExtractor.ExtractFromDat(dat, idx, bfKeys);

        if (rpsRaw is null)
        {
            Console.WriteLine("  RPS not found in archive");
        }
        else
        {
            File.WriteAllBytes(Path.Combine(rpsDir, RpsExtractor.RpsFileName), rpsRaw);
            Console.WriteLine($"  Saved {RpsExtractor.RpsFileName} ({rpsRaw.Length:N0} bytes, {rpsEntries.Count} sections)");

            int rpsOk = 0, rpsSkipped = 0, rpsErrors = 0;
            foreach (var e in rpsEntries)
            {
                if (e.Evtx is null)
                {
                    Console.WriteLine($"  SKIP {e.FileName}  ({e.SkipReason})");
                    rpsSkipped++;
                    continue;
                }

                File.WriteAllBytes(Path.Combine(rpsDir, e.FileName), e.Evtx);

                ParsedEvtx parsed;
                try { parsed = EvtxParser.Parse(e.Evtx); }
                catch (Exception ex) { Console.WriteLine($"  ERROR parsing {e.FileName}: {ex.Message}"); rpsErrors++; continue; }

                WriteJson(jsonEnDir, jsonJaDir, Path.GetFileNameWithoutExtension(e.FileName) + ".json", parsed);
                rpsOk++;
                if (verbose) Console.WriteLine($"  OK  {e.FileName}  ({parsed.Strings.Count} strings)");
            }
            Console.WriteLine($"  rps: {rpsOk} ok, {rpsSkipped} skipped, {rpsErrors} errors");
        }

        return 0;
    }

    // ── rebuild ───────────────────────────────────────────────────────────────
    // Reads from a previous `all` output and produces game-ready patched files.
    //
    // Input layout:
    //   <input_dir>/etp/<name>.etp               original ETPs from eventText dir
    //   <input_dir>/rps/<name>.etp               original ETPs extracted from RPS
    //   <input_dir>/rps/<RpsFileName>            original RPS binary
    //   <input_dir>/json/_lang/en/<name>.json        translation JSONs
    //
    // Output layout:
    //   <output_dir>/common/data/eventText/ja/current/<name>.etp
    //   <output_dir>/common/data/packresource/ja/current/<RpsFileName>

    static int CmdRebuild(string[] args, bool verbose)
    {
        if (args.Length < 2) return Error("Usage: rebuild <input_dir> <output_dir>");
        string inputDir  = args[0];
        string outputDir = args[1];

        string etpSrcDir  = Path.Combine(inputDir, "etp");
        string rpsSrcDir  = Path.Combine(inputDir, "rps");
        string jsonEnDir  = Path.Combine(inputDir, "json", "_lang", "en");

        string etpOutDir  = Path.Combine(outputDir, "common", "data", "eventText", "ja", "current");
        string rpsOutDir  = Path.Combine(outputDir, "common", "data", "packresource", "ja", "current");
        Directory.CreateDirectory(etpOutDir);
        Directory.CreateDirectory(rpsOutDir);

        if (!Directory.Exists(jsonEnDir))
            return Error($"No JSON dir found at {jsonEnDir}");

        var jsonFiles = Directory.GetFiles(jsonEnDir, "*.json");
        Console.WriteLine($"Found {jsonFiles.Length} JSON files");

        // Collect rebuilt RPS ETPs to repackage at the end
        var rpsModified = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int etpOk = 0, rpsOk = 0, errors = 0;

        foreach (string jsonPath in jsonFiles)
        {
            string jsonName = Path.GetFileName(jsonPath);                // e.g. eventTextAaaClient.json
            string etpName  = jsonName[..^".json".Length] + ".etp";     // e.g. eventTextAaaClient.etp

            Dictionary<string, JsonElement> jdata;
            try
            {
                jdata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                            File.ReadAllText(jsonPath)) ?? [];
            }
            catch (Exception ex) { Console.Error.WriteLine($"  ERROR reading {jsonName}: {ex.Message}"); errors++; continue; }

            var newStrings = JsonToStrings(jdata);
            bool hasTranslations = HasTranslations(jdata);

            // ── eventText ETP ──
            string etpRef = Path.Combine(etpSrcDir, etpName);
            if (File.Exists(etpRef))
            {
                try
                {
                    var rebuilt = RebuildEtp(etpRef, newStrings);
                    string dest = Path.Combine(etpOutDir, etpName);
                    File.WriteAllBytes(dest, rebuilt);
                    etpOk++;
                    if (verbose) Console.WriteLine($"  ETP  {etpName}  ({rebuilt.Length:N0} bytes)");
                }
                catch (Exception ex) { Console.Error.WriteLine($"  ERROR rebuilding etp/{etpName}: {ex.Message}"); errors++; }
            }

            // ── RPS ETP (collect for later repackaging) ──
            // Only replace sections that have actual translations; untranslated sections
            // are left unchanged so the original CRY/EVTX bytes are preserved exactly.
            string rpsRef = Path.Combine(rpsSrcDir, etpName);
            if (File.Exists(rpsRef) && hasTranslations)
            {
                try
                {
                    rpsModified[etpName] = RebuildEtp(rpsRef, newStrings);
                    rpsOk++;
                    if (verbose) Console.WriteLine($"  RPS  {etpName}");
                }
                catch (Exception ex) { Console.Error.WriteLine($"  ERROR rebuilding rps/{etpName}: {ex.Message}"); errors++; }
            }
        }

        Console.WriteLine($"Rebuilt: {etpOk} eventText ETPs, {rpsOk} RPS ETPs, {errors} errors");

        // ── Repackage RPS ──────────────────────────────────────────────────────
        string originalRpsPath = Path.Combine(rpsSrcDir, RpsExtractor.RpsFileName);
        if (File.Exists(originalRpsPath))
        {
            Console.WriteLine($"Repackaging {RpsExtractor.RpsFileName}...");
            try
            {
                byte[] originalRps = File.ReadAllBytes(originalRpsPath);
                byte[] rebuiltRps  = RpsExtractor.RebuildRps(originalRps, rpsModified);
                string rpsOut = Path.Combine(rpsOutDir, RpsExtractor.RpsFileName);
                File.WriteAllBytes(rpsOut, rebuiltRps);
                Console.WriteLine($"  Written {rebuiltRps.Length:N0} bytes to {rpsOut}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"  ERROR repackaging RPS: {ex.Message}"); errors++; }
        }
        else
        {
            Console.WriteLine($"  No original RPS found at {originalRpsPath}, skipping repackage");
        }

        return errors > 0 ? 1 : 0;
    }

    // ── hexdump ───────────────────────────────────────────────────────────────

    static int CmdHexdump(string[] args)
    {
        if (args.Length < 1) return Error("Usage: hexdump <input.etp> [-n <bytes>]");
        string inputPath = args[0];
        int n = 256;
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i] is "-n" or "--bytes") int.TryParse(args[i + 1], out n);

        byte[] raw = File.ReadAllBytes(inputPath);
        n = Math.Min(n, raw.Length);
        Console.WriteLine($"File: {inputPath} ({raw.Length:N0} bytes total, showing first {n})");
        for (int off = 0; off < n; off += 16)
        {
            var chunk = raw.AsSpan(off, Math.Min(16, n - off));
            var hex = string.Join(" ", chunk.ToArray().Select(b => b.ToString("X2")));
            var asc = new string(chunk.ToArray().Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());
            Console.WriteLine($"  {off:X6}  {hex,-48}  {asc}");
        }
        return 0;
    }

    // ── port-translations ─────────────────────────────────────────────────────
    // Downloads the dqx_translations repo zip and ports any filled-in translations
    // into the local json/_lang/en/ directory produced by `all`.
    //
    // Usage: port-translations <all_output_dir> [--url <zip_url>]

    private const string DefaultTranslationsZipUrl =
        "https://github.com/dqx-translation-project/dqx_translations/archive/refs/heads/main.zip";
    private const string RepoEnSubPath = "json/_lang/en/";

    static int CmdPortTranslations(string[] args, bool verbose)
    {
        if (args.Length < 1)
            return Error("Usage: port-translations <all_output_dir> [--url <zip_url>]");

        string rootDir = args[0];
        string zipUrl = DefaultTranslationsZipUrl;

        for (int i = 1; i < args.Length - 1; i++)
            if (args[i] == "--url" && i + 1 < args.Length) { zipUrl = args[i + 1]; break; }

        string localEnDir = Path.Combine(rootDir, "json", "_lang", "en");
        if (!Directory.Exists(localEnDir))
            return Error($"No json/_lang/en directory found under {rootDir}");

        Console.WriteLine($"Downloading {zipUrl}...");
        byte[] zipBytes;
        using (var http = new HttpClient())
        {
            try { zipBytes = http.GetByteArrayAsync(zipUrl).GetAwaiter().GetResult(); }
            catch (Exception ex) { return Error($"Download failed: {ex.Message}"); }
        }
        Console.WriteLine($"Downloaded {zipBytes.Length:N0} bytes");

        using var zip = new ZipArchive(new MemoryStream(zipBytes));

        var repoEntries = zip.Entries
            .Where(e => e.FullName.Contains(RepoEnSubPath, StringComparison.OrdinalIgnoreCase)
                     && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"Found {repoEntries.Count} JSONs in translation repo, scanning for translations...");

        int filesUpdated = 0, filesSkipped = 0, errors = 0, stringsPorted = 0;

        foreach (var entry in repoEntries)
        {
            string localName = entry.Name.Replace(".win32", "", StringComparison.OrdinalIgnoreCase);
            string localPath = Path.Combine(localEnDir, localName);
            if (!File.Exists(localPath))
            {
                Console.WriteLine($"  SKIP (no local file)  {localName}");
                filesSkipped++;
                continue;
            }

            Dictionary<string, JsonElement> repoData;
            try
            {
                using var stream = entry.Open();
                repoData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream) ?? [];
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR reading repo {entry.Name}: {ex.Message}");
                errors++;
                continue;
            }

            // Build jp_source → translation map (non-empty translations only)
            // Keyed by source string, not numeric ID, since IDs can change across ETP versions.
            var translations = new Dictionary<string, string>(repoData.Count);
            foreach (var (_, v) in repoData)
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                var prop = v.EnumerateObject().FirstOrDefault();
                string t = prop.Value.GetString() ?? "";
                if (t.Length > 0) translations[prop.Name] = t;
            }

            if (translations.Count == 0)
            {
                Console.WriteLine($"  SKIP (no translations)  {localName}");
                filesSkipped++;
                continue;
            }

            JsonObject? localObj;
            try { localObj = JsonNode.Parse(File.ReadAllText(localPath)) as JsonObject; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR reading local {entry.Name}: {ex.Message}");
                errors++;
                continue;
            }

            if (localObj is null) { errors++; continue; }

            int ported = 0;
            foreach (var localEntry in localObj)
            {
                if (localEntry.Value is not JsonObject innerObj || innerObj.Count == 0) continue;
                string jpKey = innerObj.First().Key;
                if (!translations.TryGetValue(jpKey, out string? translation)) continue;
                innerObj[jpKey] = translation;
                ported++;
            }

            if (ported > 0)
            {
                using var f = File.Create(localPath);
                JsonSerializer.Serialize(f, localObj, JsonWriteOpts);
                stringsPorted += ported;
                filesUpdated++;
                if (verbose) Console.WriteLine($"  OK  {localName}: {ported} strings");
            }
            else
            {
                Console.WriteLine($"  SKIP (0 strings matched)  {localName}");
                filesSkipped++;
            }
        }

        Console.WriteLine($"Ported {stringsPorted} strings into {filesUpdated} files ({filesSkipped} skipped, {errors} errors)");
        return errors > 0 ? 1 : 0;
    }

    // ── shared helpers ────────────────────────────────────────────────────────

    private static void WriteJson(string enDir, string jaDir, string jsonName, ParsedEvtx parsed)
    {
        using (var f = File.Create(Path.Combine(enDir, jsonName)))
            JsonSerializer.Serialize(f, EvtxToJson(parsed, japanese: false), JsonWriteOpts);
        using (var f = File.Create(Path.Combine(jaDir, jsonName)))
            JsonSerializer.Serialize(f, EvtxToJson(parsed, japanese: true), JsonWriteOpts);
    }

    private static byte[] RebuildEtp(string refPath, Dictionary<int, string> newStrings)
    {
        var refRaw = File.ReadAllBytes(refPath);
        var parsed = EvtxParser.Parse(refRaw);
        var ordered = parsed.Strings
            .Select(kv => new KeyValuePair<int, string>(kv.Key, newStrings.GetValueOrDefault(kv.Key, kv.Value)))
            .ToList();
        return EvtxParser.Build(parsed, ordered);
    }

    private static Dictionary<string, Dictionary<string, string>> EvtxToJson(ParsedEvtx parsed, bool japanese = false)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(parsed.Strings.Count);
        foreach (var kv in parsed.Strings)
            result[kv.Key.ToString()] = new Dictionary<string, string> { [kv.Value] = japanese ? kv.Value : "" };
        return result;
    }

    private static bool HasTranslations(Dictionary<string, JsonElement> jdata) =>
        jdata.Values.Any(v =>
            v.ValueKind == JsonValueKind.Object
                ? v.EnumerateObject().FirstOrDefault().Value.GetString()?.Length > 0
                : v.ValueKind == JsonValueKind.String && v.GetString()?.Length > 0);

    private static Dictionary<int, string> JsonToStrings(Dictionary<string, JsonElement> jdata)
    {
        var result = new Dictionary<int, string>(jdata.Count);
        foreach (var (k, v) in jdata)
        {
            if (!int.TryParse(k, out int key)) continue;
            if (v.ValueKind == JsonValueKind.Object)
            {
                var props = v.EnumerateObject().ToList();
                if (props.Count > 0)
                {
                    string jp = props[0].Name;
                    string en = props[0].Value.GetString() ?? "";
                    result[key] = en.Length > 0 ? en : jp;
                }
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                result[key] = v.GetString() ?? "";
            }
        }
        return result;
    }

    private static Dictionary<uint, byte[]> LoadBfKeys(string? explicitPath)
    {
        string path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, "dat_db.db");
        return File.Exists(path) ? EtpFinder.LoadBlowfishKeys(path) : [];
    }

    private static int Error(string msg) { Console.Error.WriteLine(msg); return 1; }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ETPLocalizer  —  DQX ETP extraction, JSON conversion and rebuild tool for PC and Wii

            Usage: etp.exe [--data-dir <path>] [--archive <name>] [--db <dat_db.db>] [-v] <command> [args]

            Commands:
              dump               <output_dir>                        Extract all ETPs from archive
              tojson             <input.etp> [output.json]           Parse ETP to JSON
              fromjson           <input.json> <ref.etp> [out.etp]    Rebuild ETP from JSON
              all                <output_dir>                        Dump all ETPs + RPS to JSON
              rebuild            <input_dir> <output_dir>            Rebuild ETPs from JSONs
              hexdump            <input.etp> [-n <bytes>]            Hex inspect an ETP file
              port-translations  <all_output_dir> [--url <zip_url>]  Download dqx_translations repo
                                                                     and port filled-in strings into
                                                                     <all_output_dir>/json/_lang/en/

            all output layout:
              etp/                      ETPs from eventText directory
              rps/                      ETPs extracted from packresource RPS + raw RPS
              json/_lang/en/            Translation targets (jp key → empty)
              json/_lang/ja/            Source strings   (jp key → jp value)

            rebuild output layout:
              common/data/eventText/ja/current/<name>.etp
              common/data/packresource/ja/current/packageManagerRegistIncludeAutoClient.rps

            port-translations downloads from the dqxclarity translation project by default:
              https://github.com/dqx-translation-project/dqx_translations/archive/refs/heads/main.zip
            Override with --url if needed.

            Defaults:
              --data-dir  C:\Program Files (x86)\SquareEnix\DRAGON QUEST X\Game\Content\Data
              --archive   data00000000.win32
              --db        dat_db.db (looked up next to the exe)
            """);
    }
}
