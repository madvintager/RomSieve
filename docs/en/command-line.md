# Command line

`romsieve-cli` runs the same analysis and the same sort as the interface, without a window: on a NAS, on a server
over SSH, or in a scheduled task. It uses the settings of the interface (or a [profile](settings-and-profiles.md)),
and only **simulates** unless you add `--execute`.

## Commands

| Command | Role |
|---|---|
| `romsieve-cli run` | Simulates the sort (default) or runs it (`--execute`) |
| `romsieve-cli undo` | Shows the last real sort; undoes it with `--yes` |
| `romsieve-cli dat` | Writes the filtered DAT of the catalog (`--output`), without reading the source |
| `romsieve-cli quarantine` | Shows the quarantine of the purges of the destination; empties it with `--empty --yes` |
| `romsieve-cli profiles` | Lists the saved profiles |
| `romsieve-cli --help` | Shows the help (also `run --help`...) |
| `romsieve-cli --version` | Shows the version |

## Examples

```bash
# Simulation with the settings of the interface, report and missing ROMs exported
romsieve-cli run --report report.json --miss-list missing.txt

# Real sort with a profile saved from the interface ("Save as profile…")
romsieve-cli run --profile "Amiga 1G1R" --execute

# Other folders, DAT or transfer mode than those of the settings
romsieve-cli run -s /roms/incoming -d /roms/sorted --dat "Commodore - Amiga.dat" --mode zip --execute

# A lot of DATs: a whole TOSEC pack, subfolders included, laid out as the pack
romsieve-cli run --dat /dats/TOSEC --dat-recursive --dir-dat-mirror --execute
# ... or two of its DATs only
romsieve-cli run --dat /dats/No-Intro --dat-only "Nintendo - Game Boy" --dat-only "Nintendo - Game Boy Color"

# Complete a non-empty destination, or purge it first (its content is set aside in _purged/)
romsieve-cli run --execute --append
romsieve-cli run --execute --purge --yes

# Undo the last real sort
romsieve-cli undo           # shows what would be undone
romsieve-cli undo --yes     # undoes it

# Quarantine of the purges of the destination (_purged folder)
romsieve-cli quarantine                         # shows what it holds
romsieve-cli quarantine --empty --yes           # empties it for good
romsieve-cli quarantine -d /mnt/roms/sorted     # the one of another destination

# Filtered DAT of the catalog: the games of the DAT the settings keep, without source nor simulation
romsieve-cli dat --output "Amiga (RomSieve).xml"
romsieve-cli dat --profile "Amiga 1G1R" --dat "Commodore - Amiga.dat" -o amiga-1g1r.dat
romsieve-cli dat --dat /dats/No-Intro -o /dats/No-Intro-1G1R      # a lot: a folder, a DAT per DAT
```

`quarantine` reads the destination of the settings or of the profile (`--profile`), or that of `--destination`.

## Options of `dat`

| Option | Role |
|---|---|
| `-o`, `--output <file>` | DAT file to write (required): `.xml` for Logiqx XML, `.dat` for ClrMamePro. With [MAME software lists](naming-conventions-and-dats.md#mame-software-lists), the **folder** where a DAT per list is written at the root (`megadriv.xml`...), as MAME software lists when they were read from MAME's `hash` files, otherwise in Logiqx XML; with a [lot of DATs](naming-conventions-and-dats.md#several-dats), the **folder** where a DAT per DAT of the lot is written, each in the format of its own. A file `.xml` or `.dat` is then refused (code 2), and the DATs of the folder that the export did not replace are named |
| `-p`, `--profile <name>` | Uses a saved profile instead of the settings of the interface |
| `--dat <file>` | DAT file of the catalog, or folder of DATs: a lot of No-Intro, Redump or TOSEC DATs, or MAME software lists |
| `--dat-recursive` | Also reads the DATs of the subfolders of the folder of DATs |
| `--dat-only <name>` | Only uses this DAT of the lot (see `run`) |
| `--categories <file>` | Category file of the MAME machines |
| `--ratings <file>` | Rating file of the MAME machines |

`dat` reads neither the source nor the destination: see [Exports](exports.md) for what the filtered DAT holds. When
the settings keep no game, no file is written and the exit code is 4.

## Options of `run`

| Option | Role |
|---|---|
| `--execute` | Actually sorts. Without it, nothing is written. |
| `-p`, `--profile <name>` | Uses a saved profile instead of the settings of the interface |
| `-s`, `--source <folder>` | Source folder |
| `-d`, `--destination <folder>` | Destination folder |
| `--dat <file>` | DAT file, or folder of DATs: a lot of No-Intro, Redump or TOSEC DATs, or MAME software lists. Once only. |
| `--dat-recursive` | Also reads the DATs of the subfolders of the folder of DATs (a lot) |
| `--dat-only <name>` | Only sorts with this DAT of the lot, named by the name of its header, case ignored. Repeatable; replaces the DATs unchecked in the settings. A name the lot does not have is refused (code 2), with the names of the lot |
| `--dir-dat-mirror` | Places the folder of each DAT of the lot under the folders of its path in the folder of DATs. Refused (code 2) when **A folder per DAT** is unchecked in the settings |
| `--chd <folder>` | Folder of the CHDs of a MAME collection, when they are not in the source |
| `--categories <file>` | Category file of the MAME machines (`catver.ini`, `catlist.ini`) |
| `--ratings <file>` | Rating file of the MAME machines (`bestgames.ini`) |
| `--mode <mode>` | `copy`, `move`, `hardlink`, `symlink` or `zip` |
| `--append` | Completes a non-empty destination |
| `--purge` | Purges a non-empty destination first (requires `--execute` and `--yes`) |
| `-y`, `--yes` | Confirms a purge or an undo |
| `--force-foreign-purge` | Allows purging a folder without the `.romsieve` marker |
| `--report <file>` | Exports the report (`.json` or `.csv`) |
| `--miss-list <file>` | Exports the missing ROMs (`.txt` or `.csv`, requires a DAT) |
| `--fixdat <file>` | Exports the fixdat of the missing ROMs (requires a DAT); with MAME software lists or a lot of DATs, the folder where a fixdat per list or per DAT is written (`fixDat_megadriv.xml`...) |

Options common to all commands:

| Option | Role |
|---|---|
| `--config <file>` | Another settings file. Its profiles, journals, crash reports and checksum cache are those next to it. |
| `--lang <fr\|en\|es>` | Language of the messages (by default, the one of the interface, otherwise English) |
| `-q`, `--quiet` | Shows only warnings, errors and the summary |

Every other setting (filters, 1G1R, pattern, conflicts...) comes from the settings file or the profile: prepare it in
the interface, then save it as a profile.

With a [lot of DATs](naming-conventions-and-dats.md#several-dats), the summary gives, after the totals, a line per DAT
with a game found (*Nintendo - Game Boy: 12 games found, 14/1600 ROMs (0.9 %)*), then the number of the other DATs; with
`--miss-list` or `--fixdat`, a line counts the complete DATs and those without any game found, which have nothing to
export.

## Safeguards

* Without `--execute`, nothing is written.
* The source must exist, the DAT must be found, and the destination may not overlap the source. A folder of DATs the
  sort would refuse (two DATs of the same name, a DAT of MAME machines, software lists mixed with other DATs, every DAT
  unchecked) is refused before anything else, with code 3.
* A real sort into a non-empty destination requires `--append`, or `--purge --yes`; a folder without the `.romsieve`
  marker also requires `--force-foreign-purge` to be purged. **Add to a non-empty destination**, checked in the
  configuration or the profile, does not replace `--append`, just as the interface still asks for a confirmation; and
  `--purge --yes` purges the destination even when this option is checked.
* `undo` only shows the sort to undo until `--yes` is added. After an incomplete undo (a locked file, an occupied
  original place), `undo --yes` resumes the operations left only.
* `quarantine --empty` requires `--yes`, and only touches the quarantine of a destination marked by RomSieve: the
  deleted content can no longer be put back by undoing the sorts that purged it.
* **Ctrl+C** stops the run cleanly: the file being written is removed.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unexpected error (a crash report is saved in `crashes/` next to the settings, and its path printed) |
| 2 | Invalid arguments (unknown option, invalid value, incompatible options, `--dat` repeated, `--dat-only` without a lot or of a DAT not in it, `--dir-dat-mirror` without a folder per DAT, unsupported export format, `dat --output` over the DAT read) |
| 3 | Invalid configuration (settings file or profile missing or unreadable, source, DAT or destination not found, folder of DATs refused, overlapping folders) |
| 4 | Finished, but some files are in error (including a sort stopped by a full destination or a journal that cannot be written), an export failed (or `dat` kept no game), some operations could not be undone or some items of the quarantine could not be deleted |
| 5 | Cancelled (Ctrl+C) |
| 6 | Confirmation required (`--yes`, `--append` / `--purge --yes`, `--force-foreign-purge`, or `quarantine --empty` without `--yes`) |
| 7 | Nothing to undo |

For instance, in a script:

```bash
romsieve-cli run --profile "Daily" --execute --append --quiet
case $? in
  0) echo "Sorted" ;;
  4) echo "Sorted with errors, see the log" ;;
  *) echo "Not sorted" ;;
esac
```
