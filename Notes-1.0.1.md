RomSieve 1.0.1 clarifies the license of RomSieve and makes problems easier to report. The program itself is the same
as in 1.0.0: there is no need to update for a fix, and your settings, profiles and journals stay as they are.

## Changes

- **License.** RomSieve stays under the GNU GPL v3. It now grants an additional permission under section 7 of the
  GPL: RomSieve may be combined with the libraries it embeds, under their own licenses, including the Microsoft Public
  License (Avalonia, Avalonia DataGrid) and the UnRAR license (SharpCompress). Anyone can redistribute the
  executables while respecting every license involved. The permission is in the README and at the top of
  `THIRD-PARTY-NOTICES.txt`, which ships in each archive.
- **Reporting a problem.** The [issues page](https://github.com/madvintager/RomSieve/issues/new/choose) offers forms
  for a bug, a file or DAT that is not recognized, and a feature request. They ask for what helps: the version, the
  convention of the DAT, the log of the run and, after a crash, the crash report. The user guide (*Troubleshooting*)
  links to them.

## Downloads

- Windows x64: `RomSieve-1.0.1-win-x64.zip`
- Linux x64: `RomSieve-1.0.1-linux-x64.tar.gz`

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
