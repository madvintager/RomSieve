RomSieve 1.2.1 fixes the multi-disc sets of Redump: a set whose discs carry their own label stays whole, a disc
dumped in several pressings completes its set, and **Complete sets only** now sees a disc that lacks tracks. Your
settings, profiles and journals stay as they are, and a sort made with an earlier version can still be undone.

## Fixes

### Discs with their own label

- **One set, not several.** A Redump disc often carries its own label after its number:
  `Command & Conquer (USA) (Disc 1) (GDI Disc)` and `(Disc 2) (NOD Disc)`, `(Disc 1) (Allies)` and
  `(Disc 2) (Soviet)`. The label split the set into games of one disc each: disc 1 was kept alone and disc 2 excluded
  as an incomplete set (1/2). These discs now form a single set, and its playlist is named without the label of the
  first disc (`Command & Conquer (USA).m3u`).
- An edition stays apart: a tag that every disc carries (`(Disc 1) (Premium Box)` and `(Disc 2) (Premium Box)`), or one
  that tells a disc from another dump of the same disc (`(Disc 1) (EDC)` next to `(Disc 1)`), still makes a release of
  its own. RomSieve compares the discs of the DAT, when there is one, so the result does not depend on the discs the
  source holds.

### Complete sets only

- **A disc that lacks tracks is excluded.** The option only counted the discs of a set. A game now has to hold every
  file the DAT lists for each of its discs (the tracks, the `.cue`), whether it has one disc or several: a Redump
  archive that only holds `(Track 8).bin` is no longer kept, as that disc would not boot. The option also applies to
  TOSEC games of a single disk.
- **Clearer reasons.** The exclusion tells what is missing: "Incomplete multi-disk set (1/2 disks found)" for an absent
  disc, "Incomplete disc: 1/9 files of the DAT found" for a disc that lacks files, "Incomplete game" for a game without
  a descriptor. A game reported as "1/9 disks" was in fact one disc that lacked eight files.
- **Fairer counting.** The number of discs of a set comes from the DAT, so a set that lacks its last disc is seen as
  incomplete. A file only completes the disc whose name it bears, even when an identical track is on another disc. The
  ROMs the DAT says were never dumped (`nodump`) no longer count as missing, and a CHD linked to its game by **Identify
  CHDs by their name** stands for its whole disc.
- Choosing a winner by hand in the table now counts the tracks of a descriptor too.

Run a simulation before sorting again into a destination filled by 1.2.0: games kept then may now be excluded as
incomplete. Uncheck **Complete sets only** to keep partial discs.

## Changes

- **Complete a set with the disks of another dump** (formerly **Tolerate minor disk tag differences
  (trainers/fixes)**) now also applies to No-Intro and Redump. A disc dumped in several pressings, told apart by their
  mastering codes (`Phantasm (Japan) (Disc 6) (4M)` and `(Disc 6) (5M)`), completes the set whose other discs have none.
  Only the set with the most discs borrows, always the same dump, and never a disc of another edition
  (`(Premium Box)`). The option keeps its setting, checked by default, so a Redump set that was incomplete may now be
  kept. It is greyed out for MAME sets and software lists, where it has no effect. For TOSEC, nothing changes: the
  clean data disks still complete a set whose disk 1 carries a trainer `[t]` or a fix `[f]`.
- **Website.** RomSieve has a home page, in English and French, with download buttons for the latest release:
  <https://madvintager.github.io/RomSieve/>. The user guide of the latest release is online in `guide/`.

## Downloads

- Windows x64: `RomSieve-1.2.1-win-x64.zip`
- Linux x64: `RomSieve-1.2.1-linux-x64.tar.gz`

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
