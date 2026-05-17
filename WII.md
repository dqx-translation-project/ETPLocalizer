# Wii workflow

Dragon Quest X for the Wii is a frozen release - the game hasn't shipped an update in over ten years and never will again. That means a one-shot localization pass against the disc files is enough; there are no patches to track and nothing to keep in sync. The Wii commands (`all-wii`, `rebuild-wii`) are tailored to that reality.

## How the Wii data differs from PC

- **No archive.** PC ships everything inside `data00000000.win32` and uses `.idx` lookups + Blowfish keys to get at individual files. The Wii ships a normal directory tree on the disc, and the tool reads directly from it.
- **Big-endian XTVE.** PC ETPs start with the `EVTX` magic in little-endian; Wii ETPs start with `XTVE` (byte-reversed) and use BE for every header field. The Wii parser is a dedicated big-endian implementation under `src/Wii/` - separate from the PC parser so PC churn can't break Wii output.
- **Different RPS contents.** PC's `packageManagerRegistIncludeAutoClient.rps` is a SEDBRES container of CRY-encrypted ETP sections. The Wii RPS is the same SEDBRES container format (little-endian header, same as PC) but every ETP section inside is plain XTVE - **no encryption layer**, so no `dat_db.db` or Blowfish keys are required.
- **Multiple RPS files.** PC has one main RPS. The Wii tree has several: `packageManagerRegistIncludeAutoClient.wii.rps`, `packageManagerRegistIncludeAutoOffline.wii.rps`, plus more under `Offline/ServerJunction/...` and the per-title subfolders.

## What you need

- A directory containing the Wii disc's `Data/` tree. The expected shape is:
  ```
  <wii_input>/GD/S4MJ/content/
    Data/eventText/ja/current/*.wii.etp
    Data/eventText/ja/offline/*.wii.etp
    Data/packresource/current/*.wii.rps
    Data/packresource/ja/...
    Offline/Data/...
    Offline/ServerJunction/...
  ```
- `etp.exe` (from `dotnet publish -c Release`). No `dat_db.db` is needed for the Wii commands.

## Step 1: dump everything to JSON

```sh
etp.exe all-wii <wii_input_dir> <output_dir>
```

This produces two siblings under `<output_dir>`:

| Subdir | Contents |
|---|---|
| `wii/` | An exact byte-for-byte copy of `<wii_input_dir>`. `rebuild-wii` reads from here as the reference for every original ETP and RPS, so the JSON folder stays purely editable and unrelated to the binary layout. |
| `json/` | A mirror of the Wii tree where each ETP has been replaced by an editable JSON. |

Layout rules for `json/`:

- A standalone `.wii.etp` becomes a `.wii.json` at the **same relative path**. For example, `GD/S4MJ/content/Data/eventText/ja/current/eventTextCsA11Client.wii.etp` becomes `json/GD/S4MJ/content/Data/eventText/ja/current/eventTextCsA11Client.wii.json`.
- A `.wii.rps` becomes a **directory of the same name**, holding one `.wii.json` per ETP-typed SEDBRES section inside the RPS. The section's internal path (e.g. `pkg\1030\KaikyuuDataClient.wii`) is preserved as the path under the RPS directory: `json/.../packageManagerRegistIncludeAutoClient.wii.rps/pkg/1030/KaikyuuDataClient.wii.json`.
- Non-ETP / non-RPS files in the Wii tree are not exposed in `json/` (they're copied through verbatim during rebuild). Only ETP-typed sections inside RPSs are extracted - other section types (textures, data tables, etc.) stay packed.

Typical scale: ~560 standalone ETPs + ~400 RPS sections = roughly 950 JSON files.

## Step 2: edit the JSONs

Each JSON maps a numeric string ID to a `{ japanese_source: english_translation }` object:

```json
{
  "14678": { "「キヒヒヒ！\n　また　来おったか　エテーネの民よ！...": "" },
  "14679": { "「時を超えるチカラを秘めた...": "Translated text here" }
}
```

- Leave the value empty (`""`) to keep the original Japanese in the rebuilt ETP.
- Fill the value with English (or any target language) to substitute it during rebuild.
- The Japanese key is the source-of-truth and must stay verbatim. Translation matches are done by string ID, not by the JP key, so don't worry about it changing.

The JSON format is identical to the PC workflow's format, so you can reuse the same editing tools and pipelines.

## Step 3: rebuild patched files

```sh
etp.exe rebuild-wii <output_dir> <patched_output_dir>
```

Where `<output_dir>` is the directory you passed to `all-wii` (so the command reads `output_dir/wii/` for originals and `output_dir/json/` for translations).

Output behaviour:

- The output tree mirrors the original Wii layout exactly - `<patched_output_dir>/GD/S4MJ/content/...` is a drop-in replacement for the input partition.
- Every standalone `.wii.etp` is either patched (if its JSON has at least one non-empty translation) or copied verbatim.
- Every `.wii.rps` is either repackaged (if at least one of its contained sections has translations) or copied verbatim. Repackaging replaces only the translated sections; everything else in the RPS keeps its original bytes.
- All other files (textures, audio, server-junction data, etc.) are copied through unchanged.

The console summary at the end reports patched/copied/error counts for both ETPs and RPSs.

## Verifying the round-trip

A clean round-trip with no translations should produce byte-identical output to the input:

```sh
etp.exe all-wii <wii_input_dir> .\wii_out
etp.exe rebuild-wii .\wii_out .\wii_patched
# Then spot-check with fc /b that .\wii_patched\... matches <wii_input_dir>\... bytewise
```

When you do add a translation, the rebuilt ETP differs only in the TEXT/INDX regions that hold the affected string - the rest of the file (header, other sections, alignment padding) is preserved.

## Things to be aware of

- **Don't move JSON files around.** The lookup from original ETP -> JSON is path-based. Renaming or relocating a JSON means rebuild will treat that ETP as having no translation and copy it through unchanged.
- **Don't translate keys.** The JP source string in the key position is used as a fallback (when the translation value is empty) and as the reference for porting translations across versions. Edit the value, not the key.
- **No `port-translations` for Wii.** The `port-translations` command is PC-only (it expects the flat PC JSON layout under `json/_lang/en/`). The Wii game's text has never matched the PC release closely enough to make automatic porting useful anyway - the Wii tree is the offline (single-player) DQX, while the PC release is the online MMO.
- **Encryption.** None of the Wii ETPs or RPS sections are encrypted. The current Wii extractor silently skips non-XTVE sections.

## Code layout

All Wii-specific logic lives under `src/Wii/`:

- `WiiEvtxParser.cs` - big-endian XTVE parse + build. Self-contained, never references the PC parser.
- `WiiRpsExtractor.cs` - Thin wrapper around the shared `SedbresFile` container that filters to XTVE sections.
- `WiiCommands.cs` - `CmdAll` and `CmdRebuild`, plus the discovery and path-mirroring logic.

The PC code (`src/EvtxParser.cs`, `src/RpsExtractor.cs`) is little-endian only and contains no Wii branches. This isolation is intentional: the Wii game is frozen, so once the Wii code works it should never need updating, while the PC parser can keep evolving with future game updates without risking the Wii output.
