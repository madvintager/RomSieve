#nullable disable

// ZlibCodec.cs
// ------------------------------------------------------------------
//
// Modified for RomSieve (2026), from SharpCompress 0.50.4
// (https://github.com/adamhathcock/sharpcompress): namespace RomSieve.Zlib,
// compression only (no inflate, no dictionary, no zlib header); see
// README.md of this folder for the list of changes.
//
//
// Copyright (c) 2009 Dino Chiesa and Microsoft Corporation.
// All rights reserved.
//
// This code module is part of DotNetZip, a zipfile class library.
//
// ------------------------------------------------------------------
//
// This code is licensed under the Microsoft Public License.
// See the file License.txt for the license details.
// More info on: http://dotnetzip.codeplex.com
//
// ------------------------------------------------------------------
//
// last saved (in emacs):
// Time-stamp: <2009-November-03 15:40:51>
//
// ------------------------------------------------------------------
//
// This module defines a Codec for ZLIB compression and
// decompression. This code extends code that was based the jzlib
// implementation of zlib, but this code is completely novel.  The codec
// class is new, and encapsulates some behaviors that are new, and some
// that were present in other classes in the jzlib code base.  In
// keeping with the license for jzlib, the copyright to the jzlib code
// is included below.
//
// ------------------------------------------------------------------
//
// Copyright (c) 2000,2001,2002,2003 ymnk, JCraft,Inc. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//
// 1. Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
//
// 2. Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in
// the documentation and/or other materials provided with the distribution.
//
// 3. The names of the authors may not be used to endorse or promote products
// derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED ``AS IS'' AND ANY EXPRESSED OR IMPLIED WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
// FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL JCRAFT,
// INC. OR ANY CONTRIBUTORS TO THIS SOFTWARE BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA,
// OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
// LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
// NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
// EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// -----------------------------------------------------------------------
//
// This program is based on zlib-1.1.3; credit to authors
// Jean-loup Gailly(jloup@gzip.org) and Mark Adler(madler@alumni.caltech.edu)
// and contributors of zlib.
//
// -----------------------------------------------------------------------

using System;
using System.Globalization;

namespace RomSieve.Zlib;

/// <summary>
/// Encoder for the Deflate format: the state of a compression and its input and output buffers.
/// </summary>
internal sealed class ZlibCodec
{
    /// <summary>The buffer from which data is taken.</summary>
    public byte[] InputBuffer;

    /// <summary>An index into the InputBuffer array, indicating where to start reading.</summary>
    public int NextIn;

    /// <summary>The number of bytes available in the InputBuffer, starting at NextIn.</summary>
    public int AvailableBytesIn;

    /// <summary>Total number of bytes read so far, through all calls to Deflate().</summary>
    public long TotalBytesIn;

    /// <summary>Buffer to store output data.</summary>
    public byte[] OutputBuffer;

    /// <summary>An index into the OutputBuffer array, indicating where to start writing.</summary>
    public int NextOut;

    /// <summary>The number of bytes available in the OutputBuffer, starting at NextOut.</summary>
    public int AvailableBytesOut;

    /// <summary>Total number of bytes written to the output so far, through all calls to Deflate().</summary>
    public long TotalBytesOut;

    /// <summary>Used for diagnostics, when something goes wrong!</summary>
    public string Message;

    internal DeflateManager dstate;

    internal uint _adler32;

    /// <summary>The compression level to use in this codec.</summary>
    public CompressionLevel CompressLevel = CompressionLevel.Default;

    /// <summary>The number of Window Bits to use.</summary>
    public int WindowBits = ZlibConstants.WindowBitsDefault;

    /// <summary>The compression strategy to use.</summary>
    public CompressionStrategy Strategy = CompressionStrategy.Default;

    /// <summary>
    /// Initialize the ZlibCodec for deflation operation, using the specified CompressionLevel, the specified number
    /// of window bits, and no RFC 1950 header (raw Deflate).
    /// </summary>
    public int InitializeDeflate(CompressionLevel level, int bits)
    {
        CompressLevel = level;
        WindowBits = bits;
        dstate = new DeflateManager { WantRfc1950HeaderBytes = false };
        return dstate.Initialize(this, CompressLevel, WindowBits, Strategy);
    }

    /// <summary>Deflate one batch of data.</summary>
    public int Deflate(FlushType flush)
    {
        if (dstate is null)
        {
            throw new ZlibException("No Deflate State!");
        }
        return dstate.Deflate(flush);
    }

    /// <summary>End a deflation session.</summary>
    public int EndDeflate()
    {
        if (dstate is null)
        {
            throw new ZlibException("No Deflate State!");
        }
        _ = dstate.End();
        dstate = null;
        return ZlibConstants.Z_OK;
    }

    // Flush as much pending output as possible. All deflate() output goes
    // through this function so some applications may wish to modify it
    // to avoid allocating a large strm->next_out buffer and copying into it.
    // (See also read_buf()).
    internal void flush_pending()
    {
        var len = dstate.pendingCount;

        if (len > AvailableBytesOut)
        {
            len = AvailableBytesOut;
        }
        if (len == 0)
        {
            return;
        }

        if (
            dstate.pending.Length <= dstate.nextPending
            || OutputBuffer.Length <= NextOut
            || dstate.pending.Length < (dstate.nextPending + len)
            || OutputBuffer.Length < (NextOut + len)
        )
        {
            throw new ZlibException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Invalid State. (pending.Length={0}, pendingCount={1})",
                    dstate.pending.Length,
                    dstate.pendingCount
                )
            );
        }

        Array.Copy(dstate.pending, dstate.nextPending, OutputBuffer, NextOut, len);

        NextOut += len;
        dstate.nextPending += len;
        TotalBytesOut += len;
        AvailableBytesOut -= len;
        dstate.pendingCount -= len;
        if (dstate.pendingCount == 0)
        {
            dstate.nextPending = 0;
        }
    }

    // Read a new buffer from the current input stream, update the
    // total number of bytes read.  All deflate() input goes through
    // this function so some applications may wish to modify it to avoid
    // allocating a large strm->next_in buffer and copying from it.
    // (See also flush_pending()).
    internal int read_buf(byte[] buf, int start, int size)
    {
        var len = AvailableBytesIn;

        if (len > size)
        {
            len = size;
        }
        if (len == 0)
        {
            return 0;
        }

        AvailableBytesIn -= len;

        // RomSieve: no Adler-32, which only the zlib header (RFC 1950) uses
        Array.Copy(InputBuffer, NextIn, buf, start, len);
        NextIn += len;
        TotalBytesIn += len;
        return len;
    }
}
