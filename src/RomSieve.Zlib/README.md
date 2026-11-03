# RomSieve.Zlib

Raw Deflate compressor that writes, byte for byte, what zlib writes at level 9 (window of 15 bits, memory level 8,
default strategy). RomSieve needs it for its TorrentZip archives, which must be identical to those of trrntzip,
RomVault and igir: the `DeflateStream` of .NET compresses with zlib-ng, whose output differs.

## Origin and license

This folder is a library apart from the rest of RomSieve, under the **Microsoft Public License** (`License.txt`). Its
code comes from [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.50.4
(`src/SharpCompress/Compressors/Deflate`), which carries the managed zlib of DotNetZip (Ms-PL), itself derived from
jzlib (BSD license) and from zlib 1.1.3 (zlib license). Every file keeps its copyright and license notices;
`FlushType.cs` is SharpCompress's own code (MIT license). `ZlibDeflateStream.cs` was written for RomSieve and is
licensed under the Microsoft Public License like the rest of this folder. The notices of all of them are in
`THIRD-PARTY-NOTICES.txt`, at the root of the repository (section "RomSieve.Zlib").

## Changes from SharpCompress

* Namespace `RomSieve.Zlib`; every type internal but `ZlibDeflateStream`, the only entry point.
* Compression only: no inflate, no preset dictionary, no zlib header nor Adler-32 (`ZlibCodec` reduced to raw
  Deflate), no dependency on the rest of SharpCompress.
* **The block truncation heuristic is removed** (`_tr_tally` in `DeflateManager.cs`): DotNetZip ends a block early when
  it seems profitable, as zlib's `trees.c` does only when built with `TRUNCATE_BLOCK`, which it is not by default. With
  it, the output differs from zlib's on some data (the fixture `Literals And Copies` of the tests shows it). RomVault
  removes it too.
* The working arrays are allocated at their exact size instead of being rented from `ArrayPool`, where an array may be
  larger than asked.
* `ZlibDeflateStream` replaces the streams of SharpCompress (`DeflateStream`, `ZlibBaseStream`): it calls the codec as
  zlib's own loop does, and ends the stream when finished or disposed (SharpCompress 0.50.4 does not end it when
  `leaveOpen` is set).

The output is checked against reference archives of igir and RomVault by the tests of the Core
(`tests/RomSieve.Core.Tests/Fixtures/TorrentZip`).
