RomSieve 1.3.0 applies your patches (translations, fixes, hacks) to the ROMs of your collection without ever modifying
them, and organizes the destination for Batocera, RetroPie, ES-DE, Recalbox or MiSTer. Your settings, profiles and
journals stay as they are, and a sort made with an earlier version can still be undone.

## New

### Patches

- **A folder of patches.** The new **Patches** card of the **Collection** step takes a folder of IPS, IPS32, UPS and BPS
  patches, loose or in ZIP, 7z or RAR archives. Each patch whose original ROM is identified in the source gives a
  patched version, a new file: the original ROM is never modified, nor is the patch.
- **Matched by their checksum.** A UPS or a BPS is matched by the checksum it records. An IPS is matched by the CRC32
  in its name (`Game (T-En) [47E70E08].ips`) or in its readme, with or without the copier header of the ROM. For the
  NES, older patches made for the iNES 1.0 header of the time are found too.
- **Named after the original ROM.** `Game (Japan) (T-En).bps` applied to `Game (Japan).sfc` gives
  `Game (Japan) [T-En].sfc`, next to the original ROM. For the NES and the Lynx, the header of the original ROM is
  written back in front of a patch made for the data alone.
- **Outside 1G1R.** A translation of a Japanese release is applied even when 1G1R keeps another release. **Write the
  patched versions only**, in the **Output** step, writes a game that has a patched version in that form only.
- **A folder for the patched versions.** The **Patched versions** field of the **Output** step, with its variables
  (`{origin_folder}`, `{dat_folder}`...), presets and live preview, puts them elsewhere: in a `Patches/{dat}/{title}`
  folder that keeps the folder of each DAT clean, or in a subfolder of each game.
- **The Patches window.** **Manage patches…**, the "*n* patches not applied" link of the results, or **View the
  patches** opens a window that shows every patch, grouped by state, with its reason. Choose the ROM of an IPS that
  gives none (from the ROMs its name or its readme proposes, or by a search by name or CRC32), review the suggestions
  before applying them, or set aside a patch you do not want, then simulate again without closing the window. Your
  choices are kept in `patch_links.json`, next to the settings, and follow a patch that is renamed or moved.
- **Your patch files stay as they are.** A patch is only renamed when you ask for it (**Add the CRC32 to the file
  name**, for other tools), and **Undo** gives it back its name.
- **Everywhere.** Patched versions are written in every transfer mode (as a copy with links, in an archive of their own
  in ZIP mode), journaled and removed by **Undo Last Sort**. The table shows them with the **Patched** badge and chip,
  and the reports and the DAT of your collection name them.
- **Apply the patches**, next to the title of the card, keeps the folder in the settings without reading it.
- **On the command line**: `run --patches <folder>`, `--patch-only` and `--no-patches`, and a new `patches` command that
  lists the patches of a simulation and saves your choices (`--link --rom`, `--ignore`, `--auto`, with `--yes`).

### Frontends

- **The ROM folder of your frontend.** The new **Frontend** list, on the **Layout** line of the **Output** step, puts
  each DAT into the folder Batocera, RetroPie, ES-DE, Recalbox or MiSTer gives its platform (`snes`, `psx`,
  `GAMEBOY`...). The destination is then the `roms/` folder of the frontend (`games/` for MiSTer), and a whole pack of
  DATs sorted in one pass gives it directly. RomSieve knows about sixty platforms, and an AGA Amiga release goes into
  the folder of the AGA Amigas.
- **Recommended settings.** Choosing a frontend offers the output settings it reads best (layout, subfolders,
  playlists), and only lists what would change. Refusing keeps your settings.
- **Multi-disk games as the frontend expects them.** A new placement of the playlist, **In a folder named after it
  (ES-DE)**, which ES-DE reads as a single game, and a new **One folder per disc game** option, which the CD cores of
  MiSTer require.
- **Warnings, not refusals.** A DAT RomSieve does not know keeps its name as folder, platform folders that gather
  several DATs are named in the log, and the simulation warns about the archives the frontend would not read. The
  **DATs of the lot** window shows the folder of each DAT.
- **On the command line**: `run --frontend <frontend>` (also with `patches`), and the summary repeats the notes of the
  frontend, even with `--quiet`.

### Also

- The **Calculated Target Path** column of the simulation table can be widened beyond the window, with a horizontal
  scroll bar.
- The **DAT** column, the detail panel and the reports name the DAT with a single DAT too, not only with a lot.
- The sort of the simulation shown keeps it on screen, its boxes locked, until the results of the sort replace it.

## Changes

- **`_purged` and `.romsieve` are reserved.** A pattern whose first folder is `_purged` or `.romsieve` is now refused,
  since emptying the quarantine would delete what the sort wrote there. A value or a DAT that would give one of these
  names at the root of the destination takes a final `_` (`_purged_`).
- The **DAT** column of the CSV report, and the `dat` field of the JSON report, are now filled with a single DAT as well.

## Downloads

- Windows x64: `RomSieve-1.3.0-win-x64.zip`
- Linux x64: `RomSieve-1.3.0-linux-x64.tar.gz`

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
