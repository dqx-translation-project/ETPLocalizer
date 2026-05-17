# ETPLocalizer

A CLI tool for extracting, editing, and rebuilding Dragon Quest X ETP files for PC. Also supports converting and rebuilding files for Wii.

Supports a full localization workflow:

- dump ETPs from `.dat` archive
- converts them to a json format
- translate the strings
- convert the modified json files back to native ETP

## requirements

- .NET 9 SDK (build only; releases are self-contained)
- Dragon Quest X PC installation (default data path used automatically)
- `dat_db.db` alongside `etp.exe` for Blowfish key lookup (required for encrypted ETPs)

## usage

```
etp.exe [--data-dir <path>] [--archive <name>] [--db <dat_db.db>] [-v] <command> [args]
```

### commands

| Command | Args | Description |
|---|---|---|
| `dump` | `<output_dir>` | Extract all ETPs from the archive |
| `tojson` | `<input.etp> [output.json]` | Parse a single ETP to JSON |
| `fromjson` | `<input.json> <ref.etp> [out.etp]` | Rebuild an ETP from a JSON |
| `all` | `<output_dir>` | Dump all ETPs + RPS to JSON (full workflow start) |
| `rebuild` | `<input_dir> <output_dir>` | Rebuild patched ETPs from translated JSONs |
| `hexdump` | `<input.etp> [-n <bytes>]` | Hex inspect an ETP file |
| `port-translations` | `<all_output_dir> [--url <zip>]` | Pull translations from the dqx_translations repo |

### full localization workflow

#### pc

```sh
# Dump everything to json in the working directory
etp.exe all .

# (optional) Port existing translations from dqx_translations project
# (also supports custom urls -- requires the same folder structure to exist!)
etp.exe port-translations .

# You would modify the json files here

# Rebuild patched game files
etp.exe rebuild . .
```

Output from `rebuild` lands in `common/data/...` matching the internal game's directory structure, ready to use with a mod loader.

#### wii

```sh
# Dump each file to json (you could script this)
etp.exe tojson <input.etp> .

# Modify your json files here. You could use port-translations, but the wii game
# doesn't receive updates anymore, so they won't change.

# Rebuild patched game files
etp.exe fromjson <input.json> <ref.etp> .
```

### Options (PC)

| Flag | Default | Description |
|---|---|---|
| `--data-dir` | `C:\Program Files (x86)\SquareEnix\DRAGON QUEST X\Game\Content\Data` | Game data directory |
| `--archive` | `data00000000.win32` | Archive name (without extension) |
| `--db` | `dat_db.db` (next to exe) | SQLite DB with Blowfish keys and path info |
| `-v` / `--verbose` | off | Verbose output |

## JSON format

Each JSON file maps a numeric string ID to an object with the Japanese source as key and the translation as value:

```json
{
  "42": { "日本語テキスト": "English translation here" },
  "43": { "別のテキスト": "" }
}
```

Leave the value empty to keep the original Japanese string in the rebuilt file.

## Building

```sh
dotnet publish -c Release
```

The output is a single self-contained `etp.exe` at `bin/Release/net9.0/win-x64/publish/`.

## Notes

- ETPs sourced from `common/data/eventText/ja/current/` in `data00000000.win32`
- The packresource RPS (`packageManagerRegistIncludeAutoClient.rps`) is also extracted and rebuilt
- Some ETPs are Blowfish-encrypted; `dat_db.db` must be present for those to be decrypted as they contain the keys necessary
- `port-translations` downloads from the [dqx-translation-project](https://github.com/dqx-translation-project/dqx_translations) by default; override with `--url`
