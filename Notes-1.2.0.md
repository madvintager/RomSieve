RomSieve 1.2.0 sorts a whole pack of DATs in one pass, reads DATs straight from their ZIP, 7z or RAR archive, and can
filter the files missing from the DAT by their name. Your settings, profiles and journals stay as they are, and a sort
made with 1.1.x or 1.0.x can still be undone.

## New

### A whole pack of DATs in one sort

- **Lot of DATs.** Choose a **folder** of No-Intro, Redump or TOSEC DATs (**Browse...**, then **Folder of DATs…**, or
  `--dat <folder>` on the command line) instead of a single DAT. The source is analysed once, each file is sorted with
  the first DAT that knows it, and each DAT keeps its own convention, filters and 1G1R. A lot may mix the three
  conventions.
- **DATs in an archive.** The DAT field also takes a **ZIP, 7z or RAR archive**, as downloaded, read without
  extracting anything. An archive of a single DAT (a Redump DAT) is that DAT; an archive of several DATs (a No-Intro
  daily pack, a TOSEC pack) is a lot.
- **DATs of the lot window.** Check the DATs to sort with, search them by name, path or convention, and show only the
  DATs checked or unchecked. Your choice is saved in the settings and the profiles (`--dat-only <name>` on the command
  line). **Also read the subfolders of the folder of DATs** (`--dat-recursive`) takes in a pack sorted by maker.
- **A folder per DAT** in the destination, checked by default, and **Mirror the folders of the DATs**
  (`--dir-dat-mirror`) to lay the destination out as the pack. Unchecked, the `{dat}` variable places the DAT anywhere
  in the pattern.
- **Large packs stay light.** Only the headers of the DATs are read to list them, and only the DATs that may know a
  file of the source are read whole: a small source sorted with a whole TOSEC pack (3,000 DATs, more than a million
  ROMs) keeps a few tens of MB in memory. The name and convention of each DAT are cached, so a pack opens again in a
  moment.
- **Exports per DAT.** The completion of the banner details each DAT; the Miss List, the fixdat and `romsieve-cli dat`
  write a DAT per DAT of the lot into a folder.

### Files missing from the DAT (#2)

- **Also filter files missing from the DAT**, on the **Filters** page, puts the files the DAT does not know through the
  filters, by the tags of their own name: a demo, a bad dump or a translation you exclude no longer ends in
  `_unmatched/` but stays in the source, **Excluded**. Files the filters keep stay non-conforming, as before.
- The box is checked for new configurations. A configuration or a profile saved by an earlier version keeps its
  result: the box is unchecked there.

## Changes

- **1G1R may choose differently.** The No-Intro order gains the **Complete set**, **Format fidelity**, **Latest date**
  and **Most disks (incomplete set)** criteria, for the DATs of floppy disks. The dates of development versions in
  No-Intro and Redump names (`(Proto) (2000-01-10)`) now count, and with a DAT the format of a file is read from the
  name the DAT gives it. Run a simulation before sorting again into a destination filled by an earlier version.
- **Criteria greyed out** when the DAT cannot use them: **Amiga chipset** when no name of the DAT marks a chipset,
  **Preferred media type** and **Format fidelity** when all its files share the same media and format. The 1G1R
  criteria are also laid out in aligned columns.
- **Pattern variables** that do not apply are greyed out in the **Insert Variable** menu instead of hidden.
- **Update banner.** An available update is announced at each startup until it is installed or ignored, and a failed
  check is tried again at the next startup.
- **Exported report.** The suggested file name is `romsieve_report.json`, whatever the language and the convention.

## Fixes

- **Insert Variable menu.** The `{category}` variable (MAME) was missing, and `{dump_status}` was only offered with a
  MAME DAT. Each entry now inserts the variable it names.
- **Forced 1G1R winner.** Choosing a winner by hand in the table no longer turns into a kept release a file the
  identification set aside: missing from the DAT, SHA-1 mismatch, damaged archive or invalid CHD.

## Downloads

- Windows x64: `RomSieve-1.2.0-win-x64.zip`
- Linux x64: `RomSieve-1.2.0-linux-x64.tar.gz`

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
