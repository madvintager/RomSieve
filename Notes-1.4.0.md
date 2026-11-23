RomSieve 1.4.0 sorts a ZX Spectrum collection for the model you use, filters the games of the No-Intro and Redump
DATs by their category, and writes the names of the games a DAT keeps as a plain list. Disks with two sides are now
handled side by side, in 1G1R, complete sets and playlists. Your settings, profiles and journals stay as they are, and
a sort made with an earlier version can still be undone.

## New

### ZX Spectrum machine

- **The model you play on.** The new **ZX Spectrum machine** list of the **Filters** page, under the Amiga machine,
  says which ZX Spectrum the collection is for: **ZX Spectrum 16K, 48K**, **ZX Spectrum 128K, +2** or
  **ZX Spectrum +2A, +3**. A game the model cannot run is excluded, with its reason ("Needs a ZX Spectrum 128K: does
  not run on the target machine").
- **One version per game.** The machine versions of a game (`Arc of Yesod`, `Arc of Yesod (48K-128K)`,
  `Arc of Yesod (128K)`) are now one game for 1G1R, and the version that makes the most of the model wins: the 128K
  one on a 128K. The new **ZX Spectrum machine** criterion of the cascade comes after *Original release*, so a clean
  48K-128K version still wins over a modified 128K one.
- **The TOSEC machine marks**, combined ones included (`(16K-48K)`, `(48K-128K)`, `(+2A-+3)`), are read in the system
  field and at the end of a title (*Apulija 13 v2.01 - 128K*), which then gives the title of the other versions. A
  game without a mark is a 48K game and is never left out; a Timex version stays a game of its own.
- A new configuration starts on the **ZX Spectrum 128K, +2**, the one the usual emulators and the MiSTer core emulate.
  A configuration or a profile saved by an earlier version keeps **Every machine**, which changes nothing.

### DAT categories

- **Games, applications, add-ons.** The new **DAT categories** section of the **Filters** page, under
  **Multi-disc**, lists the categories the No-Intro and Redump DATs give their games (`Games`, `Add-Ons`,
  `Applications`, `Demos`, `Preproduction`...), with the number of games of each once the DAT is read. Uncheck those
  you do not want: a game is excluded as soon as one of its categories is, and its reason names it.
- **Filed by its name** when its DAT gives it no category: an `(Enhancement Chip)` goes in `Add-Ons`.
- **A BIOS box.** **BIOS** (`[BIOS]`) keeps or excludes the BIOS by their name, whatever the category the DAT files
  them under. It is checked by default.
- A new configuration excludes `Add-Ons`: the Super Nintendo enhancement chips and the SuFami Turbo are not games.
  Emulators still need these chips in their BIOS folder, so keep a copy of them. A configuration or a profile saved by
  an earlier version excludes no category.

### List of the names of the games

- **Game Name List (\*.txt)**, a new file type of the filtered DAT of the catalog, writes the names of the games the
  DAT would hold, one per line, sorted, in UTF-8: a list for a script that compares it with a collection.
- With a lot of DATs or MAME software lists, RomSieve asks whether to write **Filtered DATs** or **Lists of names
  (.txt)**, then writes a list per DAT or per software list into the folder you choose.
- **On the command line**: `romsieve-cli dat --output <file>.txt`, or `--names` for a lot or software lists.

### Disks with two sides

- **Every side is kept.** 1G1R keeps one file per medium, and each side of a disk (`Disk 1 of 2 Side A`,
  `Disk 1 of 2 Side B`) is a medium of its own. When a release holds several dumps of one side, the cascade picks the
  best one, and the others become **Duplicates** whose reason names the kept file and the criterion that chose it.
- **Complete sets count the sides.** With **Complete sets only**, a TOSEC or No-Intro disk is only complete with each
  of its sides, and the reason names the missing one ("side A of disk 1 missing"). **Complete a set with the disks of
  another dump** also borrows the clean sides a disk lacks when its boot side only exists cracked.
- **Playlists.** The `.m3u` playlist of a game lists every side of its disks, in order, and is named without the disk,
  the side or the label of its first file (`Game (1988)(Publisher).m3u`). ES-DE game folders are named the same way.
- The **Media** column of the simulation table shows the side of a disk that has two: *1 of 2, side B*.

## Changes

- **TOSEC results may change for existing configurations**, as 1G1R now reads the TOSEC names more closely:
  - a `[re-release]` ranks after the original release, among the other re-releases;
  - a release on a single disk only loses to a complete set of floppies, the set a cracking group packed onto one
    floppy; a tape image, which holds the whole tape, is no longer concerned. The reason says when a complete set was
    preferred to a single-disk release;
  - a `[docs]` or a `[savegame]` dump is another dump of the game disk, and with equal marks the dump without the
    mention wins;
  - an article TOSEC puts at the end of the whole title, after a subtitle that already has its own, goes back to the
    front in folder names: `Question of Scruples - The Computer Edition, A` becomes
    `A Question of Scruples - The Computer Edition`.
- The reason of a duplicate names the criterion that really told it apart, and cites the file written as the 1G1R
  winner.
- **A DAT that cannot be read** (often a download cut short) is named by its path in the folder or the archive of
  DATs, with the line where reading stopped, and the message tells how to sort without it. The command line exits with
  code 3 instead of crashing.

## Downloads

- Windows x64: `RomSieve-1.4.0-win-x64.zip`
- Linux x64: `RomSieve-1.4.0-linux-x64.tar.gz`

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
- RomSieve provides no game, and no patch. It only sorts the files you already have.
