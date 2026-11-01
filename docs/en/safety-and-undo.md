# Safety, purge and undo

RomSieve handles collections of several terabytes. It is designed so that a wrong setting can always be corrected.

## What RomSieve never does

* **A simulation writes nothing**, anywhere.
* **The source is never modified** in Copy, Hard link, Symlink and ZIP modes. In Move mode, only the kept and
  non-conforming files leave it; excluded files and duplicates stay, and inspected archives are never moved.
* **The destination can neither be the source, nor contain it**, nor (with a recursive scan) lie inside it: the sorted
  files would be analyzed again, and a purge would empty the source. RomSieve refuses these folders, also when a
  symbolic link or a junction leads from one to the other: the paths are compared as written, then with their links
  resolved. Mapped network drives and bind mounts are not resolved.
* **Nothing is written outside the destination.** A file name read from an archive or a DAT that is an absolute path
  (`C:/…`, `/home/…`), holds a `..` segment or a null character could write anywhere on the disk, which some archives
  found on the Internet are crafted to do. Such a file is in error from the simulation on, with the reason "Not written:
  the name … is unsafe or invalid", and nothing is written for it nor for the other files of its game; the other files
  are sorted as usual. Every target is checked once more just before it is written.
* **A purge deletes nothing** (see below).
* **An interrupted sort leaves no partial file**: **Stop** (the **Simulate (Dry-Run)** button while a run is in
  progress) removes the file being written, even in the middle of a large copy. A file is written under a temporary
  name (`<name>.<id>.romsieve-tmp`) and only takes its own name once complete: a crash or a power cut leaves at most
  this temporary file, which an analysis skips and a later sort removes when it writes the same file (an hour later at
  least). Closing RomSieve during a sort asks for a confirmation, stops the sort and closes once it has stopped; during
  an undo, RomSieve closes at its end.
* **A failed transfer leaves no partial file either**: when a network share goes away or a file is locked, the file
  being written is removed, the file is marked in error and the log gives the message, and the sort goes on with the
  next files. A game whose files could not all be written is in error as a whole. In Move mode, a file stays in the
  source until its copy is complete and written to the disk itself, beyond the caches of the system (a power cut or a
  disk unplugged right after the sort loses nothing); an undo puts a moved file back the same way. The copy is never
  removed once the source is gone. **Undo Last Sort** removes what
  the sort did write.
* **A sort stops when going on would do harm**, and says why in a message: when the destination is full (every next file
  would fail as well), and when the operation journal cannot be written (what follows could not be undone; a sort never
  starts without its journal). The files not transferred stay in the source, in error with the reason "Not sorted: the
  sort stopped before this file".
* **Undo never overwrites** a file at its original place, and **never deletes a file modified** since the sort.

## Purging a non-empty destination

If the destination already holds files and *Add to a non-empty destination* is unchecked,
RomSieve offers to **purge** it before the sort:

* the previous content is **moved** into `_purged/<date>/` inside the destination. On the same disk, this is instant
  and takes no extra space;
* nothing is deleted: **Undo Last Sort** puts this content back;
* to free the disk space, empty the quarantine once you are satisfied with the result (see below).

The `_purged` folder of a previous purge is never touched by the next one: each purge has its own dated folder.

The folders and files of the system (`System Volume Information`, `$RECYCLE.BIN`, `lost+found`, `.Trash-1000`,
`.DS_Store`...) do not count: the root of a USB stick or of a memory card that holds only them is an empty destination,
and a purge never moves them.

### Emptying the quarantine

The quarantine grows with each purge. In the *Output* step, the *Destination* frame says what it holds (*Quarantine of
the purges (_purged): 3 purge(s), 1245 file(s), 12.40 GB.*) and offers **Empty the quarantine…**. The line is there as
soon as a destination is chosen: with nothing set aside yet, it says *Quarantine of the purges (_purged): empty.* and its
button stays greyed. Once confirmed, its content is **deleted for good**: this is the only deletion of RomSieve.

* Undoing the sorts that purged this content can no longer put it back: the undo does the rest and reports the missing
  items.
* The sorted files are never touched, and a link kept in the quarantine is deleted without touching its target.
* Only the quarantine of a destination marked by RomSieve is shown and emptied: the `_purged` folder of a folder
  without the marker is an ordinary folder.

On the [command line](command-line.md): `romsieve-cli quarantine` shows it, `romsieve-cli quarantine --empty --yes`
empties it.

### The `.romsieve` marker

Each real sort leaves a hidden file named `.romsieve` at the root of its destination. When you ask to purge a folder
**without** this marker (for instance a folder chosen by mistake, or a folder sorted by another tool), a second confirmation, *Directory Not Created by RomSieve*, makes sure you picked the right folder. On the
[command line](command-line.md), such a purge also requires `--force-foreign-purge`.

A destination that only holds the marker and `_purged` counts as empty. When a former destination is later used as a
source, its `_purged` folder is ignored.

## Undoing a sort

Every real sort writes a **journal** of the operations it performed (see
[where RomSieve stores its files](installation.md#where-romsieve-stores-its-files)). **Undo Last Sort** reads the
journal of the last sort that has not been undone yet, shows what will be undone (date, number of operations, mode,
folders) and, after your confirmation, reverses it:

| Operation of the sort | Undo |
|---|---|
| Move | The file goes back to its original place |
| Copy, extraction, ZIP archive, playlist, `.romsieve` marker | The file is deleted, if unchanged |
| Hard link, symbolic link | The link is deleted (the source keeps the data) |
| Rewritten descriptor (`.cue`...) | The rewritten copy is deleted; in Move mode, the original descriptor is restored |
| Purge | The content set aside in `_purged` goes back to its place |

Folders left empty are removed. Clicking **Undo Last Sort** again undoes the sort before, and so on.

Some operations cannot be undone; they are listed in the log and in the final report:

* the target was **modified** after the sort: it is kept;
* a file **already exists** at the original place: nothing is overwritten;
* a file **overwritten** during the sort (with *Overwrite* conflicts) cannot be restored;
* the target no longer exists.

An operation stopped by an occupied original place or by a read or write error (a file opened by another program)
**can be retried**: the report says so, and the sort is still offered. Close the file or free its place, then click
**Undo Last Sort** again: only the operations left are replayed. The other cases are final.

> Undo works from the journal. Do not delete the `journals` folder if you might want to undo a sort. RomSieve keeps
> the journals of the last 50 real sorts: an older sort can no longer be undone.
