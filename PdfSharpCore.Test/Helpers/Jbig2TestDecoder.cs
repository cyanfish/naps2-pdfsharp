using System;

namespace PdfSharpCore.Test.Helpers
{
    /// <summary>
    /// A minimal JBIG2 decoder written from ITU-T T.88, used as a test oracle. It only supports what the encoder
    /// produces: an embedded stream with a page information segment and one immediate generic region segment using
    /// arithmetic coding and template 0 (optionally with TPGD).
    /// </summary>
    public static class Jbig2TestDecoder
    {
        /// <summary>
        /// Decodes the stream to one bool per pixel (row-major), with true = black (JBIG2 polarity).
        /// </summary>
        public static bool[] Decode(byte[] data, out int width, out int height)
        {
            int pos = 0;
            var pageInfo = ReadSegmentHeader(data, ref pos);
            if (pageInfo.Type != 48) throw new FormatException("Expected page information segment");
            int pageWidth = (int)ReadUInt32(data, pos);
            int pageHeight = (int)ReadUInt32(data, pos + 4);
            pos += (int)pageInfo.DataLength;

            var region = ReadSegmentHeader(data, ref pos);
            if (region.Type != 38) throw new FormatException("Expected immediate generic region segment");
            int regionEnd = pos + (int)region.DataLength;
            if (regionEnd != data.Length) throw new FormatException("Unexpected data after generic region");
            width = (int)ReadUInt32(data, pos);
            height = (int)ReadUInt32(data, pos + 4);
            int x0 = (int)ReadUInt32(data, pos + 8);
            int y0 = (int)ReadUInt32(data, pos + 12);
            if (width != pageWidth || height != pageHeight || x0 != 0 || y0 != 0)
                throw new FormatException("Region doesn't cover the page");
            pos += 17;
            byte flags = data[pos++];
            if ((flags & 0x01) != 0) throw new FormatException("MMR not supported");
            if (((flags >> 1) & 0x03) != 0) throw new FormatException("Only template 0 supported");
            bool tpgdon = (flags & 0x08) != 0;
            var at = new sbyte[8];
            for (int i = 0; i < 8; i++) at[i] = (sbyte)data[pos++];

            var decoder = new MqDecoder(data, pos, regionEnd);
            var cx = new MqContexts(65536);
            var pixels = new bool[width * height];
            int w = width, h = height;
            bool Get(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && pixels[y * w + x];

            bool ltp = false;
            for (int y = 0; y < height; y++)
            {
                if (tpgdon)
                {
                    ltp ^= decoder.Decode(cx, 0x9b25) == 1;
                    if (ltp)
                    {
                        for (int x = 0; x < width; x++) pixels[y * width + x] = Get(x, y - 1);
                        continue;
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    // Template 0 (T.88 Figure 3)
                    int context =
                        (Get(x - 1, y) ? 1 << 0 : 0) |
                        (Get(x - 2, y) ? 1 << 1 : 0) |
                        (Get(x - 3, y) ? 1 << 2 : 0) |
                        (Get(x - 4, y) ? 1 << 3 : 0) |
                        (Get(x + at[0], y + at[1]) ? 1 << 4 : 0) |
                        (Get(x + 2, y - 1) ? 1 << 5 : 0) |
                        (Get(x + 1, y - 1) ? 1 << 6 : 0) |
                        (Get(x, y - 1) ? 1 << 7 : 0) |
                        (Get(x - 1, y - 1) ? 1 << 8 : 0) |
                        (Get(x - 2, y - 1) ? 1 << 9 : 0) |
                        (Get(x + at[2], y + at[3]) ? 1 << 10 : 0) |
                        (Get(x + at[4], y + at[5]) ? 1 << 11 : 0) |
                        (Get(x + 1, y - 2) ? 1 << 12 : 0) |
                        (Get(x, y - 2) ? 1 << 13 : 0) |
                        (Get(x - 1, y - 2) ? 1 << 14 : 0) |
                        (Get(x + at[6], y + at[7]) ? 1 << 15 : 0);
                    pixels[y * width + x] = decoder.Decode(cx, context) == 1;
                }
            }
            return pixels;
        }

        private struct SegmentHeader
        {
            public int Type;
            public uint DataLength;
        }

        private static SegmentHeader ReadSegmentHeader(byte[] data, ref int pos)
        {
            uint number = ReadUInt32(data, pos);
            byte flags = data[pos + 4];
            byte referred = data[pos + 5];
            if (referred >> 5 != 0) throw new FormatException("Referred-to segments not supported");
            pos += 6;
            bool longPage = (flags & 0x40) != 0;
            pos += longPage ? 4 : 1;
            var header = new SegmentHeader { Type = flags & 0x3f, DataLength = ReadUInt32(data, pos) };
            pos += 4;
            if (number > 1) throw new FormatException("Unexpected segment number");
            return header;
        }

        private static uint ReadUInt32(byte[] data, int pos) =>
            (uint)(data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3]);

        private class MqContexts
        {
            public readonly byte[] Index;
            public readonly byte[] Mps;

            public MqContexts(int size)
            {
                Index = new byte[size];
                Mps = new byte[size];
            }
        }

        /// <summary>
        /// The MQ arithmetic decoder. This uses the (equivalent) formulation from T.800 Annex C.3, where the code
        /// register isn't inverted.
        /// </summary>
        private class MqDecoder
        {
            // T.88 Table E.1
            private static readonly ushort[] QeTable =
            {
                0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
                0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
                0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
                0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601
            };

            private static readonly byte[] NmpsTable =
            {
                1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
                28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46
            };

            private static readonly byte[] NlpsTable =
            {
                1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23,
                24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46
            };

            private readonly byte[] _data;
            private readonly int _end;
            private int _bp;
            private uint _c;
            private uint _a;
            private int _ct;

            public MqDecoder(byte[] data, int start, int end)
            {
                _data = data;
                _end = end;
                // INITDEC
                _bp = start;
                _c = (uint)CurrentByte << 16;
                ByteIn();
                _c <<= 7;
                _ct -= 7;
                _a = 0x8000;
            }

            private byte CurrentByte => _bp < _end ? _data[_bp] : (byte)0xff;
            private byte NextByte => _bp + 1 < _end ? _data[_bp + 1] : (byte)0xff;

            private void ByteIn()
            {
                if (CurrentByte == 0xff)
                {
                    if (NextByte > 0x8f)
                    {
                        // Marker: feed 1 bits
                        _c += 0xff00;
                        _ct = 8;
                    }
                    else
                    {
                        _bp++;
                        _c += (uint)CurrentByte << 9;
                        _ct = 7;
                    }
                }
                else
                {
                    _bp++;
                    _c += (uint)CurrentByte << 8;
                    _ct = 8;
                }
            }

            private void RenormD()
            {
                do
                {
                    if (_ct == 0) ByteIn();
                    _a <<= 1;
                    _c <<= 1;
                    _ct--;
                } while ((_a & 0x8000) == 0);
            }

            public int Decode(MqContexts cx, int index)
            {
                int i = cx.Index[index];
                int mps = cx.Mps[index];
                uint qe = QeTable[i];
                int d;
                _a -= qe;
                if ((_c >> 16) >= qe)
                {
                    // Upper sub-interval
                    _c -= qe << 16;
                    if ((_a & 0x8000) != 0) return mps;
                    // MPS_EXCHANGE
                    if (_a < qe)
                    {
                        d = 1 - mps;
                        if (i == 0 || i == 6 || i == 14) cx.Mps[index] = (byte)(1 - mps);
                        cx.Index[index] = NlpsTable[i];
                    }
                    else
                    {
                        d = mps;
                        cx.Index[index] = NmpsTable[i];
                    }
                }
                else
                {
                    // Lower sub-interval: LPS_EXCHANGE
                    if (_a < qe)
                    {
                        _a = qe;
                        d = mps;
                        cx.Index[index] = NmpsTable[i];
                    }
                    else
                    {
                        _a = qe;
                        d = 1 - mps;
                        if (i == 0 || i == 6 || i == 14) cx.Mps[index] = (byte)(1 - mps);
                        cx.Index[index] = NlpsTable[i];
                    }
                }
                RenormD();
                return d;
            }
        }
    }
}
