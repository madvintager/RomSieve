# Patches

A patch is a small file that only holds the **differences** between a ROM and a modified version of it: a
translation, a bug fix, a hack. It is shared instead of the modified ROM, since sharing the ROM would mean sharing the
game; each user applies it to their own copy.

RomSieve applies the patches of a folder to the ROMs of your collection: each patch whose original ROM is identified in
the source gives a **patched version**, written in the destination next to the original version. The original ROM is
**never modified**.

A patch only applies to the exact ROM it was made for: same region, same revision, same dump. RomSieve finds it for you
by its checksum (CRC32), which needs a [DAT file](naming-conventions-and-dats.md#dat-files).

## Supported formats

| Format | Extension | Original ROM found by | Checks |
|---|---|---|---|
| **IPS** | `.ips` | the CRC32 written in the name of the patch (see [Matching](#matching)) | none: the format records no checksum |
| **IPS32** | `.ips` or `.ips32` | the CRC32 written in the name of the patch | none |
| **UPS** | `.ups` | the CRC32 and the size it records | CRC32 of the patch, of the original ROM and of the result |
| **BPS** | `.bps` | the CRC32 and the size it records | CRC32 of the patch, of the original ROM and of the result |

The format is read from the content of the file, not only from its extension. UPS and BPS are the safest: they only
apply to the right ROM and check their result. A damaged or truncated patch is refused before it is applied.

The other formats (xdelta, PPF, APS, BSDiff…) are not supported: their files are ignored. MAME arcade sets and the
software of MAME software lists are never patched.

## Folder of the patches

In the **Collection** step, the **Patches** card has the **Folder of the patches** field, with its **Browse...** button.
It is always shown, whatever the convention.

* The folder is read with its **subfolders**. Only the `.ips`, `.ips32`, `.ups` and `.bps` files are read.
* The patches stored in ZIP, 7z or RAR **archives** of the folder are read too.
* The folder may be inside the source: its files are not sorted as files of the collection. If it is the source
  itself, only the patches are set aside, and your ROMs are sorted as usual.
* The destination must not overlap the folder of the patches, either way: under the **Output** step, a line says so
  (*Overlaps the patches*), and both the simulation and the sort are refused. A purge never touches the folder of the
  patches.

The log of the simulation tells how many patches were found and how many gave a patched version.

## Matching

Only a ROM **identified in the DAT** can receive a patch, whatever its status: a release left out by 1G1R receives its
patch like any other. This is often the case of a translation, made for the Japanese release of a game that 1G1R leaves
out in favour of the American release.

* A **UPS** or a **BPS** designates its original ROM by the CRC32 and the size it records.
* An **IPS** records nothing: its name must give the CRC32 of the original ROM, as 8 hexadecimal digits, between
  brackets, between parentheses or apart from the rest of the name (`Game (Japan) (T-En) [47E70E08].ips`,
  `(0x47e70e08)`). This is the rule of igir. An IPS without a CRC32 in its name is not applied.
* A ROM with a **copier header** (iNES for the NES, 512-byte header for the Super Nintendo, Lynx header) is looked for
  in both forms, with and without its header: a patch made for either finds its ROM. The patch applies to the form it
  expects, and the result is written as the patch produces it.
* When several files of the source have the same CRC32, the patch applies to the one the sort writes, otherwise to the
  first in the order of the paths.
* Several patches for the same ROM each give their own patched version.

## The patched version

* **Name**: that of the patch, with the extension of the original ROM. `Game (Japan) (T-En).bps` applied to
  `Game (Japan).sfc` gives `Game (Japan) (T-En).sfc`. For an IPS, the CRC32 is removed from the name, with its brackets
  or parentheses.
* **Folder**: the one the original ROM would have in the destination, even when 1G1R leaves it out.
* **Game**: the patched version belongs to the game of its original ROM. It stays outside 1G1R, multi-disk sets and
  filters: it is never left out instead of its origin, and never leaves it out.
* **Conflicts**: a collision with a file already in the destination follows the chosen conflict mode (see
  [Conflicts](destination-and-transfers.md#conflicts)). If the patched version has the name of its original ROM (a patch
  named as its ROM, as emulators that apply a patch when they start a game ask), the original ROM keeps its name, and
  the patched version takes the suffix `_01` whatever the mode: both are written.

### Write the patched versions only

Check **Write the patched versions only**, next to the field, so that an original ROM that at least one patched version
replaces is not written. The table shows it **Excluded**, with the reason *Replaced by its patched version*. A ROM that
1G1R already left out keeps its reason. The option is greyed out without a folder of the patches.

## Transfer modes

| Mode | Patched version |
|---|---|
| **Copy** | A file, written next to the original ROM |
| **Move** | A file, written **before** the original ROM leaves the source. With *Write the patched versions only*, the original ROM it replaces stays in the source. |
| **Hard link**, **Symlink** | A **copy**: a link cannot hold modified data. The log says so. |
| **ZIP** | An archive of its own, in the TorrentZip format, like that of any other game |

An original ROM stored in an archive is extracted next to the target while the patch is applied, then deleted. The
archive is never modified.

The writing follows the same safety rules as any other (see [Safety, purge and undo](safety-and-undo.md)): the file only
appears under its name once complete, and a stopped or failed sort leaves no truncated file. The CRC32 of the original
ROM is checked while the patch is applied: a ROM modified since it was identified is refused, even for an IPS. For a UPS
or a BPS, the CRC32 of the result is checked too: a wrong result puts the patched version in **Error**, leaving nothing
in the destination. With **Verify copied files**, the patched version is read again once written.

## Patches not applied

A patch that gives no patched version has its line in the log, with its reason:

| Reason | What to do |
|---|---|
| No identified ROM with this CRC32 in the source | The original ROM is missing, or the DAT does not identify it |
| The name of an IPS patch must give the CRC32 of the original ROM | Add the CRC32 to the name of the patch, for example `[1a2b3c4d]` |
| Unreadable patch | The file is truncated, damaged, or of another format |
| The name of the patch gives the CRC32 of several ROMs of the source | Only keep in the name the CRC32 of the right ROM |
| The original ROM is part of a game in several files | A `.cue` and its tracks, or a game on several disks: these games are not patched |
| The ROM does not have the size the patch expects | It is not the ROM the patch was made for |

Without a DAT, the patches are not read: the log says so.

## In the results

* The table marks each patched version with a **Patched** badge. In the grouped view, it follows the kept release of its
  game. The detail panel names the patch, its format and the original ROM; that of an original ROM names its patched
  versions. A patched version cannot be chosen as the 1G1R winner.
* The banner counts the patched versions in the **Patched** chip, which shows them alone, and the patches not applied in
  "*n* patches not applied", whose tooltip lists them with their reasons.
* In a [partial sort](simulation-table.md#sorting-part-of-the-simulation), a patched version follows the checkbox of
  its game.
* The [exports](exports.md) describe it: the report names its patch, and the DAT of your collection writes it with its
  own checksums.

## Undo

Each patched version is recorded in the journal of the sort: **Undo Last Sort** deletes it, unless it was modified since
the sort.

## On the command line

`--patches <folder>` gives the folder of the patches, and `--patch-only` only writes the patched versions (see
[Command line](command-line.md)). The summary counts the patched versions and the patches not applied.
