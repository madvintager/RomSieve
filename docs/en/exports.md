# Exports

The **Export** menu, at the right of the action bar, is available after a simulation or a sort, and as soon as a DAT
file is chosen for the filtered DAT of the catalog. Its first entries export the results of the last run; the last one,
under a line, only needs the DAT. Each is enabled only when its export is possible, and the menu itself when at least
one of them is. No export is ever written over the DAT read: choosing its file as the target is refused, so that the
DAT stays whole.

## Report (JSON, CSV)...

Saves the result of the last run:

* **JSON** (`.json`): every file with its status, reason, title, checksum and target path, plus the statistics;
  suited to scripts and archiving.
* **CSV** (`.csv`): the same table, to open in a spreadsheet.

## DAT of your collection...

Generates a DAT file listing the ROMs of your source the last run **kept**, so that other ROM managers (ClrMamePro,
RomVault, Igir...) can check your sorted collection:

* **Logiqx XML** (`.xml`), the most widely supported format;
* **ClrMamePro** (`.dat`).

A file identified by the DAT takes its size and checksums from the DAT, without being read again; the checksums of the
other files are computed while exporting. With a MAME DAT, each kept set is written as its machine in the DAT (ROMs,
disks, parent and BIOS), without reading the archives; a merged set is written with the clones whose ROMs its archive
holds. The message at the end counts the files of the DAT as a ROM manager does: a ROM that MAME lists several times in
a machine under the same name counts once. While the export runs, a progress bar shows the files already
written, and **Stop export** stops it without writing anything. The entry is available once at least one file is kept.

The DAT follows the check boxes of the table: while some games are unchecked
([Sorting part of the simulation](simulation-table.md#sorting-part-of-the-simulation)), it only describes the checked
games and the MAME sets they need, the same files as **Sort Selection** would write. With every game checked, it
describes the whole simulation.

## Miss List (TXT, CSV, fixdat)...

Lists the ROMs of the DAT that are **missing** from your collection. It requires a DAT and a finished run. Three
formats are offered:

* **Text** (`.txt`): a short header (DAT name and version, counts) then one missing ROM name per line;
* **CSV** (`.csv`): to open in a spreadsheet;
* **Fixdat Logiqx XML** (`.xml`): a standard DAT holding only the missing ROMs, grouped by game with their parent /
  clone relations. Load it in another ROM manager to look for exactly what you lack.

ROMs of games recognized only by the name of a CHD (see
[Naming conventions and DAT files](naming-conventions-and-dats.md#chd-files)) are neither found nor missing: they are
not listed.

## Filtered DAT of the catalog (no source)...

Generates the DAT of the games of the **catalog** (the DAT file chosen at the Collection step) that your settings keep,
**without a simulation and without reading the source**: the filters, the languages and countries, the 1G1R and, for
MAME, the machine filters, the categories, the ratings and the volume are applied to the games of the DAT as they would
be to a complete collection named after it. Load the result in a ROM manager (ClrMamePro, RomVault, Igir...) to build
or check a collection of the games you want only: RomSieve itself never rebuilds a set.

The two DAT exports describe different things:

| | DAT of your collection | Filtered DAT of the catalog |
|---|---|---|
| Needs | A finished run | A DAT file |
| Describes | The files of your collection that the run kept (the checked games only, if some are unchecked) | The games of the DAT your settings would keep |
| 1G1R winner | The best version among those you have | The best version in the DAT |

With the same settings and a complete collection, both keep the same games. Otherwise they differ: the same settings do
not choose among the same versions. For instance, with the priorities FR, then EU, then US, and a source that only
holds *Zelda (USA)*, the DAT of your collection names *Zelda (USA)* and the filtered DAT of the catalog *Zelda
(France)*, which you do not have; a game missing from your source is only in the latter. The filtered DAT of the
catalog thus describes the collection to reach, to load in a ROM manager, and the DAT of your collection what you have.
A few settings have no effect on the filtered DAT of the catalog:

* **Complete sets only**, the borrowing of clean disks and the "complete multi-disk set" criterion: every game of the
  catalog is complete.
* The checks made on files (SHA-1, copier headers, CHDs read) and the manual 1G1R choices of the results table.
* **Volume** (MAME): the size of a set is that of its ROMs as the DAT gives them, before compression, and its CHDs
  count for nothing, the DAT not giving their size.

Each game is written whole, with all its ROMs (and the tracks of a disc). A MAME set is written as its machine of the DAT,
like a non-merged set: the sets it needs (devices whose ROMs it does not list) are kept with it. A relation to a game
left out (the parent of a kept clone, the BIOS of a kept game) is removed, the game then standing alone. The header of
the DAT keeps its name, followed by " (RomSieve)", and its version. The format follows the extension chosen: `.xml`
for Logiqx XML, `.dat` for ClrMamePro (the DATs of MAME software lists are written into a folder: see below). The footer shows the step under way, and **Stop export** stops it without
writing anything; when the settings keep no game, no file is written.

## MAME software lists

With [MAME software lists](naming-conventions-and-dats.md#mame-software-lists), one list or a folder of lists, the DAT
of your collection and the filtered DAT of the catalog ask for a **folder**, not a file: a DAT per list is written at
its root, named after the list (`megadriv.xml`, `gamegear.xml`...), as a ROM manager keeps them in its `DatRoot` and
as MAME looks for them. The header of each DAT holds the name of the list alone: RomVault names the folder of a DAT
after its header, and MAME only finds the software of a list in the folder of that name.

* When the lists come from MAME's own files (its `hash` folder, or the output of `mame -getsoftlist`), the DATs are
  written as **MAME software lists**, the format MAME reads in its `hash` folder: each software kept is copied as it
  is from the file of MAME, with its parts, data areas and features, and a clone whose parent is left out loses its
  `cloneof`. The folder can be given to MAME as its `hashpath`. Otherwise (SabreTools DATs), they are written in
  Logiqx XML. The ClrMamePro format is not offered for the lists: it would lose what MAME needs.
* When the folder already holds DATs, RomSieve names them and asks before writing: the DAT of each exported list
  replaces the one of the same name, and the others stay. After the export, the journal names the DATs of the folder
  that the export did not replace: a ROM manager that reads the folder reads them too, so remove those of the lists
  you no longer want.
* RomSieve never writes over the files the lists were read from: choose another folder than MAME's `hash` folder.
* The fixdat (**Miss List (TXT, CSV, fixdat)...**, `.xml`) is written as a fixdat per list that lacks something, named
  `fixDat_<list>.xml`, into the folder of the file you choose. The Miss List names each software with its list
  (`gamegear/shinobi`).

## From the command line

`romsieve-cli run` writes the same exports with `--report`, `--miss-list` and `--fixdat`, and
`romsieve-cli dat --output <file>` the filtered DAT of the catalog (a folder for MAME software lists): see [Command line](command-line.md).
