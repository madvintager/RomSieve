RomSieve 1.2.2 can write an archive that holds a single game as it is, instead of extracting its content: a disc image
of several gigabytes is neither decompressed nor compressed again. Your settings, profiles and journals stay as they
are, and a sort made with an earlier version can still be undone.

## New

### Preserve single-game archives

- **An archive written as it is.** The new option **Preserve single-game archives**, in the *Writing the files* frame of
  the **Output** step, writes a source archive (ZIP, 7z, RAR) as it is when it holds a single game (one file, or a
  descriptor and its tracks) and the sort keeps every file it holds. The archive is copied, moved or linked, according
  to the mode. An archive that holds one more file, such as a readme or a `.nfo`, has its content extracted, as before.
- **Named after its game.** The archive takes the name the DAT gives its file, with its own extension: `tetris.7z`,
  which holds `tetris.gb` identified as `Tetris (World).gb`, becomes `Tetris (World).7z`. The files it holds keep
  their names.
- **Counted as an archive.** The space needed on the destination and the FAT32 warning count the size of the archive,
  and **Verify copied files** reads a copied archive again as a whole.
- **Playlists.** The `.m3u` playlist of a multi-disk game lists the archives of its disks. Some emulators open a
  `.cue` inside a `.zip`, but most open none inside a `.7z` or a `.rar`.
- The option is unchecked by default, and greyed out in ZIP mode, which always writes a new TorrentZip archive. On the
  command line, `romsieve-cli run --preserve-archives` turns it on for the run.

## Downloads

- Windows x64: `RomSieve-1.2.2-win-x64.zip`
- Linux x64: `RomSieve-1.2.2-linux-x64.tar.gz`

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
