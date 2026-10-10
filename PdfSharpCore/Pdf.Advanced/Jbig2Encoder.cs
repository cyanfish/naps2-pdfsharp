// JBIG2 generic region encoder (lossless).
//
// Ported from jbig2enc (https://github.com/agl/jbig2enc): jbig2arith.cc, jbig2enc.cc, jbig2structs.h,
// jbig2segments.h. Copyright 2006 Google Inc. Author: Adam Langley.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in
// compliance with the License. You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed under the License is
// distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and limitations under the License.
//
// Modified for NAPS2.PdfSharp: reduced to generic region coding only (no symbol/text region coding, which is
// lossy), operates on packed byte rows, and always produces PDF-embedded output (no file header).

using System;

namespace PdfSharpCore.Pdf.Advanced
{
    /// <summary>
    /// Encodes 1-bit images as a JBIG2 generic region, suitable for a PDF stream with the /JBIG2Decode filter.
    /// The encoding is lossless.
    /// </summary>
    internal static class Jbig2Encoder
    {
        private const int SegmentTypeImmediateGenericRegion = 38;
        private const int SegmentTypePageInformation = 48;

        /// <summary>
        /// Encodes a 1-bit image as an embedded JBIG2 stream (a page information segment followed by an immediate
        /// generic region segment, with no file header or globals).
        /// </summary>
        /// <param name="data">Packed rows, top-down, most significant bit = leftmost pixel.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="stride">Bytes per row in <paramref name="data"/>.</param>
        /// <param name="oneIsBlack">
        /// True if a set bit in <paramref name="data"/> means black. JBIG2 uses 1 = black; the /JBIG2Decode filter
        /// inverts this so that its output matches /DeviceGray (0 = black).
        /// </param>
        public static byte[] EncodeGenericRegion(byte[] data, int width, int height, int stride, bool oneIsBlack)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            int rowBytes = (width + 7) / 8;
            if (stride < rowBytes) throw new ArgumentOutOfRangeException(nameof(stride));
            if (data.Length < (long)stride * (height - 1) + rowBytes)
                throw new ArgumentException("Data is too short for the given dimensions.", nameof(data));

            int wordsPerRow = (width + 31) / 32;
            uint[] words = PackWords(data, width, height, stride, wordsPerRow, oneIsBlack);

            var coder = new ArithmeticEncoder();
            // TPGD (duplicate line removal) is lossless and generally makes the output both smaller and faster
            coder.EncodeBitmap(words, width, height, wordsPerRow, true);
            coder.Finish();

            const int pageInfoSize = 19;
            const int genericRegionHeaderSize = 26;
            const int segmentHeaderSize = 11;
            var output = new ByteWriter(2 * segmentHeaderSize + pageInfoSize + genericRegionHeaderSize + coder.Length);

            // Page information segment (T.88 7.4.8)
            WriteSegmentHeader(output, 0, SegmentTypePageInformation, pageInfoSize);
            output.WriteUInt32((uint)width);
            output.WriteUInt32((uint)height);
            output.WriteUInt32(0); // x resolution: unknown (PDF doesn't use it)
            output.WriteUInt32(0); // y resolution: unknown
            output.WriteByte(0x01); // page flags: eventually lossless
            output.WriteUInt16(0); // striping information: not striped

            // Immediate generic region segment (T.88 7.4.6)
            WriteSegmentHeader(output, 1, SegmentTypeImmediateGenericRegion,
                (uint)(genericRegionHeaderSize + coder.Length));
            // Region segment information field (T.88 7.4.1)
            output.WriteUInt32((uint)width);
            output.WriteUInt32((uint)height);
            output.WriteUInt32(0); // x location
            output.WriteUInt32(0); // y location
            output.WriteByte(0); // external combination operator: OR
            // Generic region segment flags: MMR = 0, GBTEMPLATE = 0, TPGDON = 1
            output.WriteByte(0x08);
            // Adaptive template pixels A1..A4 (the defaults for template 0)
            output.WriteSByte(3);
            output.WriteSByte(-1);
            output.WriteSByte(-3);
            output.WriteSByte(-1);
            output.WriteSByte(2);
            output.WriteSByte(-2);
            output.WriteSByte(-2);
            output.WriteSByte(-2);
            output.WriteBytes(coder.Buffer, coder.Length);

            return output.ToArray();
        }

        /// <summary>
        /// Converts packed byte rows to big-endian 32-bit words per row with 1 = black and zeroed padding bits, which
        /// is the layout the generic region coder works on.
        /// </summary>
        private static uint[] PackWords(byte[] data, int width, int height, int stride, int wordsPerRow,
            bool oneIsBlack)
        {
            int rowBytes = (width + 7) / 8;
            uint lastWordMask = (width & 31) == 0 ? uint.MaxValue : uint.MaxValue << (32 - (width & 31));
            var words = new uint[wordsPerRow * height];
            for (int y = 0; y < height; y++)
            {
                int src = y * stride;
                int dst = y * wordsPerRow;
                for (int i = 0; i < wordsPerRow; i++)
                {
                    uint w = 0;
                    int b = i * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        w <<= 8;
                        if (b + k < rowBytes) w |= data[src + b + k];
                    }
                    words[dst + i] = oneIsBlack ? w : ~w;
                }
                words[dst + wordsPerRow - 1] &= lastWordMask;
            }
            return words;
        }

        /// <summary>
        /// Writes a segment header (T.88 7.2) with no referred-to segments, associated with page 1.
        /// </summary>
        private static void WriteSegmentHeader(ByteWriter w, uint number, int type, uint dataLength)
        {
            w.WriteUInt32(number);
            w.WriteByte(type & 0x3f); // segment header flags: 1-byte page association, not deferred
            w.WriteByte(0); // referred-to segment count and retention flags
            w.WriteByte(1); // page association
            w.WriteUInt32(dataLength);
        }

        private sealed class ByteWriter
        {
            private byte[] _buffer;
            private int _length;

            public ByteWriter(int capacity)
            {
                _buffer = new byte[Math.Max(capacity, 16)];
            }

            private void Ensure(int count)
            {
                if (_length + count > _buffer.Length)
                    Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
            }

            public void WriteByte(int value)
            {
                Ensure(1);
                _buffer[_length++] = (byte)value;
            }

            public void WriteSByte(int value) => WriteByte((byte)(sbyte)value);

            public void WriteUInt16(int value)
            {
                WriteByte(value >> 8);
                WriteByte(value);
            }

            public void WriteUInt32(uint value)
            {
                WriteByte((int)(value >> 24));
                WriteByte((int)(value >> 16));
                WriteByte((int)(value >> 8));
                WriteByte((int)value);
            }

            public void WriteBytes(byte[] data, int count)
            {
                Ensure(count);
                Buffer.BlockCopy(data, 0, _buffer, _length, count);
                _length += count;
            }

            public byte[] ToArray()
            {
                var result = new byte[_length];
                Buffer.BlockCopy(_buffer, 0, result, 0, _length);
                return result;
            }
        }

        /// <summary>
        /// The JBIG2 MQ arithmetic encoder (T.88 Annex E) with the adaptive contexts for generic region coding.
        /// </summary>
        private sealed class ArithmeticEncoder
        {
            private const int MaxContexts = 65536;

            /// <summary>Context used for the TPGD "typical prediction" bit of template 0 (T.88 6.2.5.7).</summary>
            private const int TpgdContext = 0x9b25;

            // T.88 Table E.1. Index 46 is a fixed-probability state that is never used by an encoder, so it is
            // omitted. Each state exists twice: for MPS = 0 (index n) and for MPS = 1 (index n + 46).
            private static readonly ushort[] BaseQe =
            {
                0x5601, 0x3401, 0x1801, 0x0ac1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801,
                0x3001, 0x2401, 0x1c01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401,
                0x3001, 0x2801, 0x2401, 0x2201, 0x1c01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101,
                0x0ac1, 0x09c1, 0x08a1, 0x0521, 0x0441, 0x02a1, 0x0221, 0x0141, 0x0111, 0x0085,
                0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001,
            };

            private static readonly byte[] BaseNextMps =
            {
                1, 2, 3, 4, 5, 38, 7, 8, 9, 10,
                11, 12, 13, 29, 15, 16, 17, 18, 19, 20,
                21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
                31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
                41, 42, 43, 44, 45, 45,
            };

            private static readonly byte[] BaseNextLps =
            {
                1, 6, 9, 12, 29, 33, 6, 14, 14, 14,
                17, 18, 20, 21, 14, 14, 15, 16, 17, 18,
                19, 19, 20, 21, 22, 23, 24, 25, 26, 27,
                28, 29, 30, 31, 32, 33, 34, 35, 36, 37,
                38, 39, 40, 41, 42, 43,
            };

            private static readonly ushort[] Qe = new ushort[92];
            private static readonly byte[] NextMps = new byte[92];
            private static readonly byte[] NextLps = new byte[92];

            static ArithmeticEncoder()
            {
                for (int s = 0; s < 46; s++)
                {
                    // States whose LPS transition also flips the MPS sense ("SWITCH" column of Table E.1)
                    bool sw = s == 0 || s == 6 || s == 14;

                    // MPS = 0
                    Qe[s] = BaseQe[s];
                    NextMps[s] = BaseNextMps[s];
                    NextLps[s] = (byte)(BaseNextLps[s] + (sw ? 46 : 0));

                    // MPS = 1
                    Qe[s + 46] = BaseQe[s];
                    NextMps[s + 46] = (byte)(BaseNextMps[s] + 46);
                    NextLps[s + 46] = (byte)(BaseNextLps[s] + (sw ? 0 : 46));
                }
            }

            private uint _c;
            private uint _a = 0x8000;
            private int _ct = 12;
            private byte _b;
            private int _bp = -1;

            private byte[] _out = new byte[4096];
            private int _outLen;

            private readonly byte[] _context = new byte[MaxContexts];

            /// <summary>The output buffer; only the first <see cref="Length"/> bytes are valid.</summary>
            public byte[] Buffer => _out;

            /// <summary>Number of bytes produced so far (final once <see cref="Finish"/> has been called).</summary>
            public int Length => _outLen;

            private void Emit()
            {
                if (_outLen == _out.Length) Array.Resize(ref _out, _out.Length * 2);
                _out[_outLen++] = _b;
            }

            /// <summary>The BYTEOUT procedure of the standard.</summary>
            private void ByteOut()
            {
                bool useRightBlock;
                if (_b == 0xff)
                {
                    useRightBlock = true;
                }
                else if (_c < 0x8000000)
                {
                    useRightBlock = false;
                }
                else
                {
                    _b += 1;
                    if (_b != 0xff)
                    {
                        useRightBlock = false;
                    }
                    else
                    {
                        _c &= 0x7ffffff;
                        useRightBlock = true;
                    }
                }

                if (_bp >= 0) Emit();

                if (useRightBlock)
                {
                    _b = (byte)(_c >> 20);
                    _bp++;
                    _c &= 0xfffff;
                    _ct = 7;
                }
                else
                {
                    _b = (byte)(_c >> 19);
                    _bp++;
                    _c &= 0x7ffff;
                    _ct = 8;
                }
            }

            /// <summary>A merging of the ENCODE, CODELPS and CODEMPS procedures of the standard.</summary>
            private void EncodeBit(byte[] context, int index, int d)
            {
                int state = context[index];
                int mps = state >= 46 ? 1 : 0;
                uint qe = Qe[state];

                if (d != mps)
                {
                    // CODELPS
                    _a -= qe;
                    if (_a < qe) _c += qe;
                    else _a = qe;
                    context[index] = NextLps[state];
                }
                else
                {
                    // CODEMPS
                    _a -= qe;
                    if ((_a & 0x8000) != 0)
                    {
                        _c += qe;
                        return;
                    }

                    if (_a < qe) _a = qe;
                    else _c += qe;
                    context[index] = NextMps[state];
                }

                // RENORME
                do
                {
                    _a <<= 1;
                    _c <<= 1;
                    _ct -= 1;
                    if (_ct == 0) ByteOut();
                } while ((_a & 0x8000) == 0);
            }

            /// <summary>
            /// The FLUSH procedure of the standard: terminates the arithmetic-coded data and appends the 0xFF 0xAC
            /// marker.
            /// </summary>
            public void Finish()
            {
                // SETBITS
                uint tempc = _c + _a;
                _c |= 0xffff;
                if (_c >= tempc) _c -= 0x8000;

                _c <<= _ct;
                ByteOut();
                _c <<= _ct;
                ByteOut();
                Emit();
                if (_b != 0xff)
                {
                    _b = 0xff;
                    Emit();
                }
                _b = 0xac;
                Emit();
            }

            /// <summary>
            /// Encodes a bitmap as a generic region using template 0 with the default adaptive template pixels
            /// (A1=(3,-1), A2=(-3,-1), A3=(2,-2), A4=(-2,-2)).
            /// </summary>
            /// <param name="data">Rows of big-endian words, 1 = black, with padding bits zeroed.</param>
            /// <param name="mx">Width in pixels.</param>
            /// <param name="my">Height in pixels.</param>
            /// <param name="wordsPerRow">Words per row in <paramref name="data"/>.</param>
            /// <param name="duplicateLineRemoval">Whether TPGD (typical prediction of duplicate lines) is used.</param>
            public void EncodeBitmap(uint[] data, int mx, int my, int wordsPerRow, bool duplicateLineRemoval)
            {
                byte[] context = _context;

                int ltp = 0, sltp = 0;

                for (int y = 0; y < my; y++)
                {
                    // The w* values hold words from each of the three rows that form the context: w1 is two rows up,
                    // w2 one row up and w3 is the current row. The next bit to roll into the context is kept at the
                    // top of each word.
                    uint w1 = 0, w2 = 0;

                    if (y >= 2) w1 = data[(y - 2) * wordsPerRow];
                    if (y >= 1)
                    {
                        w2 = data[(y - 1) * wordsPerRow];

                        if (duplicateLineRemoval)
                        {
                            // It's possible that the last row was the same as this row
                            if (RowsEqual(data, y * wordsPerRow, (y - 1) * wordsPerRow, wordsPerRow))
                            {
                                sltp = ltp ^ 1;
                                ltp = 1;
                            }
                            else
                            {
                                sltp = ltp;
                                ltp = 0;
                            }
                        }
                    }

                    if (duplicateLineRemoval)
                    {
                        EncodeBit(context, TpgdContext, sltp);
                        if (ltp != 0) continue;
                    }

                    uint w3 = data[y * wordsPerRow];

                    // The top bits are the start of the context rows
                    uint c1 = w1 >> 29;
                    uint c2 = w2 >> 28;
                    w1 <<= 3;
                    w2 <<= 4;
                    uint c3 = 0;

                    for (int x = 0; x < mx; x++)
                    {
                        int tval = (int)((c1 << 11) | (c2 << 4) | c3);
                        int v = (int)(w3 >> 31);

                        EncodeBit(context, tval, v);

                        c1 <<= 1;
                        c2 <<= 1;
                        c3 <<= 1;
                        c1 |= w1 >> 31;
                        c2 |= w2 >> 31;
                        c3 |= (uint)v;

                        int m = x & 31;
                        if (m == 28 && y >= 2)
                        {
                            // Need to roll in another word from two lines up
                            int wordNo = (x >> 5) + 1;
                            w1 = wordNo >= wordsPerRow ? 0 : data[(y - 2) * wordsPerRow + wordNo];
                        }
                        else
                        {
                            w1 <<= 1;
                        }

                        if (m == 27 && y >= 1)
                        {
                            // Need to roll in another word from the last line
                            int wordNo = (x >> 5) + 1;
                            w2 = wordNo >= wordsPerRow ? 0 : data[(y - 1) * wordsPerRow + wordNo];
                        }
                        else
                        {
                            w2 <<= 1;
                        }

                        if (m == 31)
                        {
                            // Need to roll in another word from this line
                            int wordNo = (x >> 5) + 1;
                            w3 = wordNo >= wordsPerRow ? 0 : data[y * wordsPerRow + wordNo];
                        }
                        else
                        {
                            w3 <<= 1;
                        }

                        c1 &= 31;
                        c2 &= 127;
                        c3 &= 15;
                    }
                }
            }

            private static bool RowsEqual(uint[] data, int a, int b, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (data[a + i] != data[b + i]) return false;
                }
                return true;
            }
        }
    }
}
