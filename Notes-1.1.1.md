RomSieve 1.1.1 fixes how 1G1R handles games on several floppy disks in the No-Intro DATs, such as those of the Acorn
Archimedes. Your settings, profiles and journals stay as they are, and a sort made with 1.1.0 or 1.0.x can still be
undone.

## Fixes

- **Floppy disk sets kept together.** The No-Intro DATs of floppy disks number the disks `(Disk 1)`, `(Disk 2)`...,
  sometimes with a label: `(Disk 1, Game Disk)`, `(Disk 2 - Courses)`. RomSieve did not read these names as disks of
  one set, so 1G1R could keep some disks of a game and leave out the others. The disks of a set are now kept or left
  out together. Without a total in the name (`(Disk 1 of 3)`), the set counts as many disks as the highest number
  found.
- **Sets whose disks are clones of one disk.** These DATs describe each disk as a game and make the disks clones of
  one of them, so the set could lose to another release on the **Parent rather than clone** criterion. Such a set now
  counts as a parent.
- **Incomplete sets.** A set that lacks disks cannot be played: it now loses to a complete release, including a
  release on a single disk, even when no other set of the game is complete. Before, a game whose first disk was missing
  could be kept instead of a playable release.
- **Demos.** The full game now wins over its demo (`(demo)`, `(demo-playable)`, `(Demo)`, `(Kiosk)`...) on the
  **Original release** criterion, in every convention. Before, a demo could win on the name, the last tiebreak, against
  a game on several disks, whose names are longer.
- **`.jfd` images.** The JASPP floppy images of the Acorn Archimedes (`.jfd`) are recognized as floppy disks. They keep
  the copy protections, and the **Format fidelity** criterion puts them with `.ipf` and `.stx`, before the sector images
  (`.adf`, `.dsk`...).

## Changes

- **1G1R may choose differently.** With No-Intro floppy DATs, or with collections that hold demos, a new simulation
  may keep other releases than 1.1.0 did. Run a simulation before sorting again into a destination filled by an earlier
  version: the **Conflicts** setting decides what happens to the files already there, as usual.

## Downloads

- Windows x64: `RomSieve-1.1.1-win-x64.zip`
- Linux x64: `RomSieve-1.1.1-linux-x64.tar.gz`

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
