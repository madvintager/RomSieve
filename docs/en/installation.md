# Installation

## Download

Each release provides one archive per platform:

| Platform | Archive |
|---|---|
| Windows 10/11 (x64) | `RomSieve-<version>-win-x64.zip` |
| Linux (x64) | `RomSieve-<version>-linux-x64.tar.gz` |

Each archive holds:

* `RomSieve` (`RomSieve.exe` on Windows): the graphical interface;
* `romsieve-cli` (`romsieve-cli.exe`): the [command line](command-line.md);
* `README.md` and `LICENSE` (GPLv3);
* `THIRD-PARTY-NOTICES.txt` and the `licenses` folder: the copyright notices and licenses of the components that both
  programs embed (.NET, Avalonia, SkiaSharp, SharpCompress...). **Third-Party Licenses**, in the
  [preferences window](settings-and-profiles.md#preferences), opens this file;
* `docs/`: this user guide, in English and French, to read offline in a browser (open `docs/index.html`).

Both programs are **self-contained**: no .NET installation is required. Each one is a single file of about 75 to 100 MB.

### Checking a download

Each archive comes with a `.sha256` file. Put both in the same folder, then:

```bash
sha256sum -c RomSieve-1.2.0-linux-x64.tar.gz.sha256
```

On Windows, in PowerShell, compare the hash shown by `Get-FileHash RomSieve-1.2.0-win-x64.zip` with the content of
the `.sha256` file.

## First launch

### Windows

Extract the archive anywhere (for instance `C:\Programs\RomSieve`) and run `RomSieve.exe`.

The executables are not signed: on the first launch, Windows SmartScreen may display *"Windows protected your PC"*.
Click **More info**, then **Run anyway**.

### Linux

```bash
tar -xzf RomSieve-1.2.0-linux-x64.tar.gz
cd RomSieve-1.2.0-linux-x64
./RomSieve
```

The archive keeps the execute permission. If it was lost (copy through a Windows share, for instance), restore it with
`chmod +x RomSieve romsieve-cli`.

Both programs use a few system libraries, which most Linux desktops already have:

* **ICU** (`libicu`) and **OpenSSL** (`libssl`), for both programs;
* for the interface only, **X11** (`libX11`, `libICE`, `libSM`) and **Fontconfig**. Under Wayland, the interface goes
  through XWayland, which GNOME and KDE provide out of the box.

If one is missing, RomSieve does not start and names it in the terminal (*"Couldn't find a valid ICU package"*, or
*"Unable to load shared library 'libSM.so.6'"*, for instance). To install them:

```bash
# Debian, Ubuntu (ICU and OpenSSL are always installed there)
sudo apt install libx11-6 libice6 libsm6 libfontconfig1
# Fedora
sudo dnf install libX11 libICE libSM fontconfig libicu openssl-libs
# Arch
sudo pacman -S libx11 libice libsm fontconfig icu openssl
```

## Where RomSieve stores its files

| File | Windows | Linux |
|---|---|---|
| Settings (`config.json`) | `%APPDATA%\RomSieve\` | `~/.config/RomSieve/` (or `$XDG_CONFIG_HOME/RomSieve/`) |
| Named profiles (`profiles/*.json`) | `%APPDATA%\RomSieve\profiles\` | `~/.config/RomSieve/profiles/` |
| Journals of the sorts (`journals/`) | `%APPDATA%\RomSieve\journals\` | `~/.config/RomSieve/journals/` |
| Logs of the runs (`logs/`) | `%APPDATA%\RomSieve\logs\` | `~/.config/RomSieve/logs/` |
| Crash reports (`crashes/`) | `%APPDATA%\RomSieve\crashes\` | `~/.config/RomSieve/crashes/` |
| Checksum cache (`crc_cache.json`) | `%LOCALAPPDATA%\RomSieve\` | `~/.cache/RomSieve/` (or `$XDG_CACHE_HOME/RomSieve/`) |
| Header cache (`header_cache.json`) | `%LOCALAPPDATA%\RomSieve\` | `~/.cache/RomSieve/` (or `$XDG_CACHE_HOME/RomSieve/`) |

* The **settings** are saved when you close RomSieve and before each real sort: the next launch restores your
  folders, filters and window layout.
* The **journals** record every file written by a real sort; they are what makes [undo](safety-and-undo.md) possible.
  RomSieve keeps those of the last 50 real sorts; older ones are deleted when a sort begins, and their sorts can no
  longer be undone.
* The **logs of the runs** keep every line of the log of each simulation, sort or undo started from the interface,
  whose panel only shows the last ones; the end of the run gives the path of its file. RomSieve keeps the last 20.
* The **crash reports** are only written after an unexpected error (see [Troubleshooting](troubleshooting.md#reporting-a-problem)).
  You can delete them at any time.
* The **checksum cache** remembers the CRC32 of each file (invalidated when the file changes), so that a second
  analysis of the same collection is much faster.
* The **header cache** remembers what the headers of archives (their files, with their size and CRC32) and of CHDs
  say, also invalidated when the file changes. An unchanged archive is not opened again, which matters most on a
  network share: there, reading the headers of a whole MAME collection takes minutes, and a second analysis is
  several times faster.

To start over with default settings, close RomSieve and delete `config.json`.

## Updating

Replace the executables with those of the new archive. Your settings, profiles, journals and cache are kept: they are
stored outside the program folder.

## Building from source

See the *Building from Source* section of the [README](../../README.md) (.NET 10 SDK required).
