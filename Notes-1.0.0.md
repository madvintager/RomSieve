RomSieve 1.0.0 is the first release of RomSieve, a free and open-source (GPL v3) ROM organizer for Windows and Linux.
It audits a collection against its DAT, keeps one release per game (1G1R) and rebuilds a clean folder tree. You see
the full result before anything is written. It downloads nothing: it sorts the collection you already have.

## Highlights

- **Three preservation standards.** TOSEC (vintage computers and early consoles), No-Intro (cartridge consoles) and
  Redump (optical media). RomSieve also sorts **MAME** sets (non-merged or merged, one archive per machine) and
  **MAME software lists**, one or several per sort. Split collections are recognized too: a split clone is sorted
  with its parent, which it needs to run.
- **Full TOSEC support.** RomSieve reads the whole TOSEC naming convention: title, version, date, publisher, system,
  video standard, countries, languages, media (*Disk 1 of 3*, *Side B*) and dump flags (`[!]`, `[a]`, `[cr]`, `[t]`,
  `[h]`, `[tr Fr]`...). Incomplete multi-disk sets are completed with clean disks from other dumps.
- **DAT audit.** Files are identified by size and CRC32 (SHA-1 optional), so badly named files are still recognized.
  ZIP, 7z and RAR archives are read without extraction, and CHD files by their header. Logiqx XML and ClrMamePro DATs
  are supported, parent/clone relations included. An option checks the contents of the archives: a damaged file, or a
  damaged MAME set, is isolated instead of being sorted as complete.
- **Deterministic 1G1R.** Releases are ranked by a cascade of 18 criteria: your languages and countries in order of
  priority, video standard, dump quality, complete multi-disk sets, version, revision... Each convention has its own
  default order, and you can reorder it. Four presets (France, Europe, USA, Japan) set everything up in one click. The
  same input always gives the same result.
- **Simulation first.** A dry run fills a table with every file: its status, the reason for it and where it will go.
  It also warns if the destination may not have enough room. Right-click a game to force another release as the 1G1R
  winner, or uncheck games to sort only part of the collection.
- **Safe file operations.** RomSieve can copy, move, create hard or symbolic links, or write one ZIP per game. Folder
  patterns such as `{publisher}/{title}/` are shown with a live preview. Files that do not match go to `_unmatched/`,
  and no partial file is left behind. Purging a destination deletes nothing: its content is set aside in `_purged/`.
  Every real sort is logged, so the last one can be undone.
- **Exports.** JSON and CSV reports, miss list, fixdat, DAT of the collection, and filtered DAT of a catalog.
- **Command line.** `romsieve-cli` runs the same pipeline as the interface, with the same safeguards and documented
  exit codes. It only simulates unless you pass `--execute`.
- **Interface in English, French and Spanish**, in the language of the system at the first launch, with light, dark
  and system themes.

## Downloads

- Windows x64: `RomSieve-1.0.0-win-x64.zip`
- Linux x64: `RomSieve-1.0.0-linux-x64.tar.gz`

Each archive holds the interface (`RomSieve`) and the command line (`romsieve-cli`). Both are self-contained, so no
.NET installation is needed. The archive also holds the user guide in English and French, which opens in a browser
(`docs/index.html`), and the licenses of the third-party components. Check a download with
`sha256sum -c <archive>.sha256`.

## Good to know

- **Windows:** the executables are not signed, so SmartScreen may warn on the first launch.
- **Linux:** RomSieve needs ICU and OpenSSL, plus X11 and Fontconfig for the interface. Most desktops already have
  them, and the user guide (*Installation*) gives the commands to install them. Under Wayland, the interface runs
  through XWayland.
- **macOS** is not supported yet.
- RomSieve provides no game. It only sorts the files you already have.
