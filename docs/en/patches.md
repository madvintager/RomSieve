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
| **IPS** | `.ips` | the CRC32 written in the name of the patch, or in its readme (see [Matching](#matching)) | none: the format records no checksum |
| **IPS32** | `.ips` or `.ips32` | the CRC32 written in the name of the patch, or in its readme | none |
| **UPS** | `.ups` | the CRC32 and the size it records | CRC32 of the patch, of the original ROM and of the result |
| **BPS** | `.bps` | the CRC32 and the size it records | CRC32 of the patch, of the original ROM and of the result |

The format is read from the content of the file, not only from its extension. UPS and BPS are the safest: they only
apply to the right ROM and check their result. A damaged or truncated patch is refused before it is applied.

The other formats (xdelta, PPF, APS, BSDiff…) are not supported: their files are ignored. MAME arcade sets and the
software of MAME software lists are never patched.

## Folder of the patches

In the **Collection** step, the **Patches** card has the **Folder of the patches** field, with its **Browse...** button.
It is always shown, whatever the convention.

* The folder is read with its **subfolders**. Only the `.ips`, `.ips32`, `.ups` and `.bps` files are read, and the
  `.txt` and `.nfo` texts that may give the original ROM of an IPS (see [Matching](#matching)).
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
  `(0x47e70e08)`). This is the rule of igir.
* An IPS whose name gives no CRC32 takes it from its **readme**, if it has one. Most patches give it, in the form
  romhacking.net uses (`File/ROM CRC32: 47E70E08`) or another (`CRC32 : 0x47e70e08`). See
  [Readme of an IPS](#readme-of-an-ips).
* An IPS without a CRC32, neither in its name nor in its readme, is not applied.
* A ROM with a **copier header** (iNES for the NES, 512-byte header for the Super Nintendo, Lynx header) is looked for
  in both forms, with and without its header: a patch made for either finds its ROM. The patch applies to the form it
  expects, and the result is written as the patch produces it.
* When several files of the source have the same CRC32, the patch applies to the one the sort writes, otherwise to the
  first in the order of the paths.
* Several patches for the same ROM each give their own patched version.

### Readme of an IPS

A readme is a `.txt` or `.nfo` text file of at most 1 MB, stored next to the patch: in the same folder of the folder of
the patches, or in the same archive, at the same place. A readme on the disk never comes with a patch stored in an
archive, nor the reverse.

* A readme with the name of the patch (`Game (T-Fr).txt` for `Game (T-Fr).ips`, case ignored) is its own.
* Otherwise, the readmes of a folder or an archive that holds **a single patch** are its own.
* In a folder of several patches, a readme with the name of none comes with none: it does not tell which patch it is
  about.

RomSieve only reads the lines where the word **CRC** appears, and on them the CRC32 that follow it. A readme often also
gives the CRC32 of the patched result: it designates no ROM of the source, and it is ignored. The name of the patch
always prevails over its readme, and a UPS or a BPS, which records its original ROM itself, never reads its own.

A readme sometimes gives the CRC32 of a ROM **with and without its copier header**, on two lines (for example
`File CRC32` and `ROM CRC32`). An IPS does not tell which of the two forms it expects: RomSieve does not apply it. The
readme usually tells which form the patch was made for. Then write the CRC32 of that form in the name of the patch.

The detail panel of a patched version names the readme that gave the CRC32.

## The patched version

* **Name**: that of the original ROM, followed by a label of the patch in square brackets, with the extension of the
  original ROM (see [Name of the patched version](#name-of-the-patched-version)).
* **Folder**: the one the original ROM would have in the destination, even when 1G1R leaves it out.
* **Game**: the patched version belongs to the game of its original ROM. It stays outside 1G1R, multi-disk sets and
  filters: it is never left out instead of its origin, and never leaves it out.
* **Conflicts**: a collision with a file already in the destination follows the chosen conflict mode (see
  [Conflicts](destination-and-transfers.md#conflicts)). If two patches give the same name (the same patch as an IPS and
  as a BPS, for example), the second patched version takes the suffix `_01` whatever the mode: both are written.

### Name of the patched version

The name always starts from that of the original ROM, as the DAT gives it: the region and the revision come from the
DAT, never from the author of the patch, and each patched version sorts next to its original ROM. The label in square
brackets comes from the name of the patch:

| Patch | Original ROM | Patched version |
|---|---|---|
| `Game (Japan) (T-En).bps` | `Game (Japan).sfc` | `Game (Japan) [T-En].sfc` |
| `Game (T-En v1.2).bps` | `Game (Japan).sfc` | `Game (Japan) [T-En v1.2].sfc` |
| `Game (T-En) [47E70E08].ips` | `Game (Japan).sfc` | `Game (Japan) [T-En].sfc` |
| `SMB3_eng_v1.2.ups` | `Super Mario Bros. 3 (Japan).nes` | `Super Mario Bros. 3 (Japan) [SMB3_eng_v1.2].nes` |
| `Alpha (1990)(Ocean)[h Hack].bps` | `Alpha (1990)(Ocean).adf` | `Alpha (1990)(Ocean)[h Hack].adf` |

* What repeats the original ROM is removed: its whole name or its title, when the name of the patch starts with them,
  and its CRC32, with its brackets or parentheses. The parentheses and brackets left are removed.
* Square brackets mark a modified dump, as in TOSEC (`[h]`, `[tr]`): in No-Intro and Redump, parentheses describe an
  official release. For a TOSEC ROM, the label follows the name without a space, as a TOSEC flag.
* A **generic** patch name (`patch`, `patched`, `hack`, `fix`, `translation`, `rom`, `game`, `english`, `eng`, `update`,
  or a version alone such as `v1.1`) gives way to the name of the archive that holds the patch, or of its folder in that
  archive, then to the name of its folder in the patch folder, if that folder holds no other patch (a folder of several
  patches sorts them, by system or by kind, and tells nothing of each one). `CT Translation/patch.ips` gives
  `Chrono Trigger (Japan) [CT Translation].sfc`. Without a better label, the generic name is kept.
* A patch that gives no label (named as its original ROM, as emulators that apply a patch when they start a game ask)
  takes the label `patched`: `Game (Japan) [patched].sfc`. The patched version never has the name of its original ROM.

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
| Neither the name of the IPS patch nor a text that comes with it gives the CRC32 of the original ROM | Choose its ROM in the **Patches** window (see [Naming an IPS](#naming-an-ips)), or add the CRC32 to the name of the patch, for example `[1a2b3c4d]` |
| No identified ROM in the source for the CRC32 given by a readme | The original ROM is missing, or the DAT does not identify it |
| Unreadable patch | The file is truncated, damaged, or of another format |
| The name of the patch gives the CRC32 of several ROMs of the source | Only keep in the name the CRC32 of the right ROM |
| A readme gives the CRC32 of several ROMs of the source | Write in the name of the patch the CRC32 of the right ROM |
| A readme gives the CRC32 of a ROM with and without its copier header | Write in the name of the patch the CRC32 of the form it was made for |
| The original ROM is part of a game in several files | A `.cue` and its tracks, or a game on several disks: these games are not patched |
| The ROM does not have the size the patch expects | It is not the ROM the patch was made for |

Without a DAT, the patches are not read: the log says so.

## Naming an IPS

Most downloaded IPS have a CRC32 neither in their name nor in their readme. Rather than looking it up and renaming the
file by hand, open the **Patches** window: after a simulation, click "*n* patches not applied" in the banner of the
results (or "*n* patches to name", when no patch was left out but some IPS were applied thanks to their readme).

The window lists the patches not applied, then the IPS applied thanks to their readme. For the patch selected:

* **Original ROM** proposes the identified ROMs whose name or title starts the name of the patch, or that of its
  archive, or that of its folder when the folder holds it alone (`CT Translation/patch.ips`). Generic names (`patch`,
  `fix`, `v1.1`…) do not count. The whole name comes before the title alone, then the ROM the sort writes. For a patch
  whose name or readme designates several ROMs, those ROMs.
* **Other ROM** searches all the ROMs identified by the simulation, when no ROM proposed is the right one: a translated
  (`Super Mario Frères.ips` for `Super Mario Bros.`) or shortened title proposes none. **Take this ROM** (or a double
  click) chooses it.
* **Form** chooses, for a ROM with a copier header, the CRC32 with or without the header. An IPS does not tell which one
  it was made for: its readme usually does. The list is greyed out when the ROM has a single form.

A ROM proposed is never taken on its own: check each patch to rename, look at the **New name** shown, then click
**Rename**. The CRC32 is added before the extension (`Game (T-Fr).ips` becomes `Game (T-Fr) [47e70e08].ips`), the row
says what became of it, and the log records it. **Simulate again** closes the window and runs the simulation again,
which applies the patches renamed. The CRC32 added does not appear in the name of the patched version (see
[Name of the patched version](#name-of-the-patched-version)).

* **Check the IPS applied by a readme** checks the IPS the simulation already applied thanks to their readme: writing
  their CRC32 in their name makes other tools, such as igir, apply them too.
* A patch stored in an **archive** is never renamed, since an archive is never modified: the window gives the CRC32 of
  the ROM chosen, and the patch has to be extracted to write that CRC32 in its name.
* A patch whose name already gives a CRC32, a UPS or a BPS cannot be chosen for: a CRC32 in its name would not fix it.
* Nothing is overwritten: a name already taken leaves the patch as it was, and its row says so. Renaming is not recorded
  in the journal of the sorts, and **Undo Last Sort** does not undo it: it is undone by hand.

## In the results

* The table marks each patched version with a **Patched** badge. In the grouped view, it follows the kept release of its
  game. The detail panel names the patch, its format and the original ROM; that of an original ROM names its patched
  versions. A patched version cannot be chosen as the 1G1R winner.
* The banner counts the patched versions in the **Patched** chip, which shows them alone, and the patches not applied in
  "*n* patches not applied", whose tooltip lists them with their reasons and which a click opens in the **Patches**
  window (see [Naming an IPS](#naming-an-ips)).
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

`romsieve-cli patches` simulates the sort and lists its patches not applied, with the ROMs proposed for an IPS without
CRC32, then the IPS applied thanks to their readme; `--name-from-notes --yes` writes the CRC32 in the name of the latter.
