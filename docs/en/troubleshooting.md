# Troubleshooting

### Many files are "Non-conforming"

* **Without a DAT**, RomSieve relies on the names: a TOSEC file must at least look like `Title (date)(publisher).ext`.
  Check that the **Convention** matches your files (the badge shows the effective one), or force it.
* **With a DAT**, a non-conforming file is absent from the DAT: wrong DAT (another system, another version), modified
  file, or file inside an archive whose member has no recorded CRC32 (some 7z and RAR archives).
* Non-conforming files are not lost: they are transferred to `_unmatched/` in the destination, or left in the source
  when **Isolate non-conforming files in _unmatched** (*Output* step) is unchecked.

### A filter seems to have no effect

The TOSEC settings of the **Filters** page only apply to TOSEC names, and the *No-Intro / Redump* settings to No-Intro and
Redump names. Check **Settings for:** and the convention badge. In 1G1R, some options (preferred media, modified releases) only decide between releases of the same
game.

With a DAT, the filters only see the files the DAT identifies: a file missing from the DAT stays non-conforming, even
with a tag you exclude, while **Also filter files missing from the DAT** is unchecked (see
[Files missing from the DAT](filters.md#files-missing-from-the-dat)). This is the case of a configuration saved by an
earlier version of RomSieve.

### "Incomplete disc" or "Incomplete game"

With **Complete sets only**, a game is excluded as soon as it lacks a file the DAT lists: the reason gives the number
of files found out of the number expected, for that disc only. The most common case is a Redump archive that holds a
single track (`(Track 8).bin`): it lacks the `.cue` and the other tracks, and the disc would not boot. Complete the
dump, or uncheck the option to keep partial discs (see [Multi-disc](filters.md#multi-disc)). "Incomplete game in the
destination", further down, is something else: a file that could not be written during the sort.

### 1G1R did not keep the release I wanted

The tooltip of the excluded release, or the **Selected Row Details** panel, tells which criterion decided (for
instance *FR preferred over EN*). Change the order of your languages or countries, or the
[criteria order](languages-regions-1g1r.md#changing-the-order), or right-click the release and choose
**Keep this version as 1G1R winner**.

### Two different games were grouped together, or one game was split

1G1R groups releases by title and system (and by parent / clone with a DAT). Use **Isolate all versions of "…"** in the
context menu to see the group, and a DAT when possible: it gives the most reliable grouping.

### "The destination is inside the source directory"

The destination may be neither the source, nor a parent of it, nor (with a recursive scan) a folder inside it. Choose a
destination outside the source, or uncheck *Scan subdirectories recursively*.

### The first analysis is slow

The first analysis reads every file to compute its checksum; on a hard disk drive, about the speed of a copy. The
checksums are then cached, and the next analyses of unchanged files are much faster. On an SSD or a NAS, raise
**CRC computation** (**Preferences…** window) to several files at a time.

### Hard links fail

Hard links only work within one disk (partition) and not on FAT32 / exFAT. Choose **Copy** or **Symlink**.

### Symbolic links fail on Windows

Creating symbolic links may require the Developer Mode (*Settings > System > For developers*) or administrator rights.

### Windows says "Windows protected your PC"

The executables are not signed. Click **More info**, then **Run anyway**.

### Where did my files go after a purge?

In `_purged/<date>/` inside the destination. **Undo Last Sort** puts them back; otherwise move them yourself or delete
the folder to free the space. See [Safety, purge and undo](safety-and-undo.md).

### Undo reports operations "not undone"

A file modified after the sort is kept, an original place already occupied is never overwritten, and a file
overwritten during the sort cannot be restored. The log lists each case with its path. When the report says that some
operations can be retried (a file opened by another program, an occupied original place), close the file or free the
place, then click **Undo Last Sort** again: only these operations are replayed (see
[Safety, purge and undo](safety-and-undo.md)).

### "Sort stopped"

The sort stopped before its end, and the message says why. **The destination is full** (the simulation and the
confirmation of the sort say so beforehand: "Not enough room"): free some space, then run the sort again with *Add to
a non-empty destination* checked and *Conflicts* set to **Skip**: the files already written are left as they are.
**The operation journal cannot be written**: check the folder of the settings (see
[Installation](installation.md)), its free space and its write permission. When the journal failed during the sort, the
message names the last file written, which the journal does not hold: **Undo Last Sort** will not remove it.

A game in error with the reason "Incomplete game in the destination" lacks one of its files (a track of a `.cue`, for
example): its other files were written. **Undo Last Sort** removes them, or run the sort again once the cause is fixed.

### "Unreadable folders skipped" or "Links to a folder not followed"

RomSieve could not read some folders of the source (the rights of another user, a folder of the system), or found links
to folders in it (symbolic links, junctions): it went on with the rest, and the files of these folders are not sorted.
Give yourself the right to read the folder, or choose the folder a link leads to as the source. The folders and files of
the system (recycle bins, `System Volume Information`, `lost+found`, `Thumbs.db`, `.DS_Store`...) are skipped without a
word: they never belong to a collection.

### "Unexpected error"

RomSieve met an error it did not expect. It saves a crash report and tells you where; the window stays open, but
restarting RomSieve is safer before a real sort. The command line prints the same report path and exits with code 1.
The reports are in the `crashes` folder next to the settings (see [Installation](installation.md)); please attach the
latest one when you report the problem. If RomSieve closes on its own, a report is saved as well.

### Reporting a problem

Report it on the [issues page](https://github.com/madvintager/RomSieve/issues/new/choose) of RomSieve: a form asks for
what is needed. Note the version (title bar, or `romsieve-cli --version`), the steps that lead to the problem, and
attach the report exported after a simulation (**Export** > **Report (JSON, CSV)...**), or the full log of the run
concerned: its path is given at the end of the log (`logs` folder, see [Installation](installation.md#where-romsieve-stores-its-files)).
After an unexpected error or a crash, also attach the latest crash report of the `crashes` folder: it holds the
version, the system and the technical details of the error, and the paths of the files concerned, if any.
