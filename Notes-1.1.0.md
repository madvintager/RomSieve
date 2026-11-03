RomSieve 1.1.0 writes its ZIP archives in the **TorrentZip** format, the one RomVault and igir recognize. Your
settings, profiles and journals stay as they are, and a sort made with 1.0.x can still be undone.

## New

- **TorrentZip archives.** In the **ZIP** mode, each archive is now byte for byte the one that trrntzip, RomVault and
  igir write from the same files. The format fixes everything that could differ from one tool to another: the order
  of the files, their date, the compression and the headers. An archive therefore only depends on its content. A ROM
  manager takes a sorted collection as already in order, without compressing it again, and a collection shared with
  others stays identical from one user to the next. MAME sets and the software of MAME lists are still copied as
  they are, never compressed again.
- **Checked archives.** With **Verify copied files**, RomSieve also checks the TorrentZip signature of each archive,
  in addition to the content of each file. An archive that fails the check is deleted and the source is kept.

## Changes

- **The archives change.** An archive written by 1.1.0 is not byte for byte the one 1.0.x wrote for the same files,
  even though the files inside are the same: their order, the encoding of their names and the compression differ. If
  you sort again in ZIP mode into a destination filled by 1.0.x, the **Conflicts** setting decides what happens, as
  usual.
- **Slower compression.** To match the archives of the other tools byte for byte, RomSieve uses the compression of
  zlib instead of the faster one built into .NET. Writing ZIP archives takes about 1.5 times as long as in 1.0.x. The
  other modes are as fast as before.
- **Licenses.** The compressor is a library of its own, `RomSieve.Zlib`, taken from SharpCompress and under the
  Microsoft Public License. It is listed in `THIRD-PARTY-NOTICES.txt`, which ships in each archive.

## Downloads

- Windows x64: `RomSieve-1.1.0-win-x64.zip`
- Linux x64: `RomSieve-1.1.0-linux-x64.tar.gz`

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
