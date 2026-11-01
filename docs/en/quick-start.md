# Quick start

This page walks you through a first sort, from an unsorted folder to a clean tree. Allow a few minutes; nothing is
written to disk until the last step.

The column on the left of the window follows the steps of this page: **Collection**, **Selection** (**Filters**, then
**Languages and 1G1R**), **Output**, then **Results**. The buttons at the bottom (**Simulate (Dry-Run)**,
**Start Sorting**...) stay available from each of them.

## 1. Choose the folders

* In the **Collection** step, **Source** frame: the folder holding your unsorted files (ROMs, disk images, ZIP / 7z / RAR
  archives...). Keep *Scan subdirectories recursively* checked to include its subfolders. The folders and files of the
  system (recycle bins, `System Volume Information`, `Thumbs.db`...) are skipped; so are the folders that cannot be read
  and the links to a folder, which the log reports.
* In the **Output** step, **Destination** frame: the folder where the sorted tree will be built. Prefer an **empty** folder
  for a first try.

> The destination may be neither the source itself, nor a parent of the source, nor (with a recursive scan) a folder
> inside the source. RomSieve refuses these combinations to protect your files.

## 2. Optionally choose a DAT file

A DAT file is the official catalog of a collection (TOSEC, No-Intro or Redump), with the name and the checksums of each
ROM. With a DAT, RomSieve identifies your files by their **checksum**, whatever their name, and can tell you which ROMs
are **missing**. Without a DAT, it relies on the **file names** alone.

In the **Collection** step, **DAT catalogue** frame, click **Browse...** next to *File*. A summary appears below the
field (name, version, number of ROMs), and the badge next to **Convention**, in the same frame, shows the detected
convention.

See [Naming conventions and DAT files](naming-conventions-and-dats.md) for details.

## 3. Choose how to transfer the files

In the **Output** step:

* **Mode**: **Copy** (recommended for a first try: the source is untouched), **Move**, **Hard link**, **Symlink**
  or **ZIP** (one archive per game).
* **Conflicts**: what to do when a target file already exists: rename with a `_01` suffix, skip, or overwrite.
* **Pattern**: the folder tree to build, for instance `{publisher}/{year}/{title}/` (TOSEC default) or
  `{country}/{title}/` (*Presets* menu, *Country / Title (No-Intro, Redump)*). The preview, at the bottom of the
  *Directory tree* frame, shows where a few files of the source would go.

See [Destination and transfers](destination-and-transfers.md).

## 4. Adjust the filters (optional)

* The **Filters** page of the *Selection* step excludes bad dumps, hacks, demos, prototypes..., with settings for TOSEC and for
  No-Intro / Redump. The defaults are sensible: bad dumps, over/underdumps, infected images and modified releases are
  excluded.
* The **Languages and 1G1R** page keeps only your languages or countries, and with **1G1R** a single release
  per game. The quick presets (France, Europe, USA, Japan) are a good start.

See [Filters](filters.md) and [Languages, regions and 1G1R](languages-regions-1g1r.md).

## 5. Simulate

Click **Simulate (Dry-Run)**. The window switches to the **Results** step: RomSieve analyzes the source and fills the
**Simulation Preview** table with one row per file: its title, its status and the path where it **would** be written. Nothing is written to disk.

![Statistics banner](../images/stats_banner.png)

The banner above the table counts the files by status; click a chip to show only those rows:

| Status | Meaning |
|---|---|
| **Kept** (*Valid*) | Will be written to the destination. |
| **Excluded** | Removed by a filter or by 1G1R; the reason is shown. Left untouched in the source. |
| **Non-conforming** (*Unmatched*) | Name not understood, or file absent from the DAT. Will go to `_unmatched/` in the destination. |
| **Duplicates** | Same file found twice. Left untouched in the source. |

Check a few rows, go back to the settings (**Change the settings**, or a step of the column on the left) to change a
setting if needed, and simulate again: a second simulation of the same source highlights
what changed. See [The simulation table](simulation-table.md).

Once the simulation is over, **Start Sorting** is put forward and a sentence sums up what the sort will do, for example
"Copy 11 files + 1 non-conforming (14.61 MB) to D:\Retro\Amiga\Sorted". Changing a setting hides this summary until the next simulation.

## 6. Sort

Click **Start Sorting**. RomSieve asks for a confirmation summarizing the source, the destination and the mode.
If the destination is not empty, RomSieve adds the files to it when *Add to a non-empty destination* is checked; otherwise it offers to purge it first. The purged content is set aside, not deleted: see
[Safety, purge and undo](safety-and-undo.md).
To sort only a few games, check them or filter the table and use **Sort the Checked Games Shown**, in the menu of the arrow next to
**Start Sorting**: see [Sorting part of the simulation](simulation-table.md#sorting-part-of-the-simulation).

While the sort runs, a progress bar, the number of files processed and the remaining time are shown above the buttons,
and every operation is written to the **Operation Log** tab of the **Results** step, shown when the sort starts. The **Simulate (Dry-Run)** button becomes **Stop**: it
interrupts the sort cleanly, the file being written is removed and the source is never touched.

## 7. Undo if needed

**Undo Last Sort** reverses the last real sort: moved files go back to their original place, copies, links and
playlists are deleted, and purged content is put back. A file you modified after the sort is never deleted.

## What next?

* [Export](exports.md) a report, the list of missing ROMs or a DAT of your collection.
* Save your settings as a [profile](settings-and-profiles.md) to reuse them, also from the
  [command line](command-line.md).
