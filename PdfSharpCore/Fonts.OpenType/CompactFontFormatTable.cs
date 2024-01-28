using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfSharpCore.Fonts.OpenType
{
    internal class CompactFontFormatTable : OpenTypeFontTable
    {
        public const string Tag = TableTagNames.Cff;

        private const int OpCharset = 15;
        private const int OpEncoding = 16;
        private const int OpCharStrings = 17;
        private const int OpPrivate = 18;
        private const int OpSubrs = 19;
        private const int OpFdArray = 1236;
        private const int OpFdSelect = 1237;
        
        private byte _majorVersion;
        private byte _minorVersion;
        private byte _hdrSize;
        private byte _offSize;
        
        private byte[][] _nameIndex;
        private byte[][] _topDictIndex;
        private byte[][] _stringIndex;
        private byte[][] _globalSubrIndex;
        private Dictionary<int, object> _topDict;
        private byte[][] _charStringsIndex;
        private byte[][] _fontDictIndex;
        private Dictionary<int,object> _privateDict;
        private byte[][] _localSubrIndex;
        private ushort[] _charsets; // maps glyph id to string id in _charStringsIndex
        private byte[] _fdSelect;

        public CompactFontFormatTable()
            : base(null, Tag)
        {
            this.DirectoryEntry.Tag = Tag;
        }

        public CompactFontFormatTable(OpenTypeFontface fontData)
            : base(fontData, Tag)
        {
            this.DirectoryEntry.Tag = Tag;
            Read();
        }

        public void Read()
        {
            try
            {
                int basePos = _fontData.Position;
                _majorVersion = _fontData.ReadByte();
                _minorVersion = _fontData.ReadByte();
                _hdrSize = _fontData.ReadByte();
                _offSize = _fontData.ReadByte();
                _fontData.Position = basePos + _hdrSize;
                
                _nameIndex = ReadIndex();
                _topDictIndex = ReadIndex();
                _stringIndex = ReadIndex();
                _globalSubrIndex = ReadIndex();

                if (_nameIndex.Length != 1 || _topDictIndex.Length != 1)
                {
                    throw new InvalidOperationException("CFF table should have exactly one font");
                }

                _topDict = ParseDict(_topDictIndex[0], 0, _topDictIndex[0].Length);
                
                if (_topDict.ContainsKey(OpEncoding))
                {
                    _fontData.Position = basePos + (int) _topDict[OpEncoding];
                    ReadEncodings();
                }
                if (_topDict.ContainsKey(OpCharStrings))
                {
                    _fontData.Position = basePos + (int) _topDict[OpCharStrings];
                    _charStringsIndex = ReadIndex();
                }
                if (_topDict.ContainsKey(OpCharset))
                {
                    int operand = (int) _topDict[OpCharset];
                    if (operand == 0 || operand == 1 || operand == 2)
                    {
                        // Predefined charsets, not to be used with CID fonts
                        throw new NotImplementedException();
                    }
                    _fontData.Position = basePos + operand;
                    ReadCharsets();
                }
                if (_topDict.ContainsKey(OpFdSelect))
                {
                    _fontData.Position = basePos + (int) _topDict[OpFdSelect];
                    ReadFdSelect();
                }
                if (_topDict.ContainsKey(OpFdArray))
                {
                    _fontData.Position = basePos + (int) _topDict[OpFdArray];
                    _fontDictIndex = ReadIndex();
                }
                if (_topDict.ContainsKey(OpPrivate))
                {
                    object[] operands = (object[]) _topDict[OpPrivate];
                    _fontData.Position = basePos + (int) operands[1];
                    _privateDict = ParseDict(_fontData.FontSource.Bytes, _fontData.Position, (int) operands[0]);
                }
                if (_topDict.ContainsKey(OpSubrs))
                {
                    _fontData.Position = basePos + (int) _topDict[OpSubrs];
                    _localSubrIndex = ReadIndex();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PSSR.ErrorReadingFontData, ex);
            }
        }

        private void ReadFdSelect()
        {
            int format = _fontData.ReadByte();
            if (format == 0)
            {
                throw new NotImplementedException();
            }
            else if (format == 3)
            {
                int rangeCount = _fontData.ReadUShort();
                _fdSelect = new byte[65536];
                int rangeStart = _fontData.ReadUShort();
                while (rangeStart < _charStringsIndex.Length)
                {
                    byte fd = _fontData.ReadByte();
                    int nextRangeStart = _fontData.ReadUShort();
                    for (int i = rangeStart; i < nextRangeStart; i++)
                    {
                        _fdSelect[i] = fd;
                    }
                    rangeStart = nextRangeStart;
                }
            }
            else
            {
                throw new InvalidOperationException("Unexpected format for fdselect");
            }
        }
        
        private byte[] SerializeFdSelect()
        {
            MemoryStream stream = new MemoryStream();
            
            stream.WriteByte(3);
            
            // Allocate space for range count
            ushort rangeCount = 0;
            stream.WriteByte(0); 
            stream.WriteByte(0);
            
            // First range start (always 0)
            stream.WriteByte(0);
            stream.WriteByte(0);
            for (int i = 0; i < _charsets.Length; i++)
            {
                while (i + 1 < _charsets.Length && _fdSelect[i + 1] == _fdSelect[i])
                {
                    i++;
                }
                stream.WriteByte(_fdSelect[i]);
                int nextRangeStart = i + 1;
                stream.WriteByte((byte) ((nextRangeStart >> 8) & 0xFF));
                stream.WriteByte((byte) (nextRangeStart & 0xFF));
                rangeCount++;
            }
            
            // Write range count
            stream.Position = 1;
            stream.WriteByte((byte) ((rangeCount >> 8) & 0xFF));
            stream.WriteByte((byte) (rangeCount & 0xFF));
            
            return stream.ToArray();
        }

        private void ReadCharsets()
        {
            int format = _fontData.ReadByte();
            if (format == 0)
            {
                throw new NotImplementedException();
            }
            else if (format == 1)
            {
                throw new NotImplementedException();
            }
            else if (format == 2)
            {
                int glyphsCount = _charStringsIndex.Length;
                _charsets = new ushort[glyphsCount];
                ushort glyphsFilled = 1;
                while (glyphsFilled < glyphsCount)
                {
                    int first = _fontData.ReadUShort();
                    int leftInRange = _fontData.ReadUShort();
                    for (int i = first; i <= first + leftInRange; i++)
                    {
                        _charsets[i] = glyphsFilled++;
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Unexpected format for charsets");
            }
        }

        private byte[] SerializeCharsets()
        {
            MemoryStream stream = new MemoryStream();

            ushort[] reverseMap = new ushort[_charsets.Length];
            for (ushort i = 0; i < _charsets.Length; i++)
            {
                reverseMap[_charsets[i]] = i;
            }
            
            stream.WriteByte(2);
            for (int i = 1; i < reverseMap.Length; i++)
            {
                int rangeStart = i;
                while (i + 1 < reverseMap.Length && reverseMap[i + 1] == reverseMap[i] + 1)
                {
                    i++;
                }
                int leftInRange = i - rangeStart;
                stream.WriteByte((byte) ((rangeStart >> 8) & 0xFF));
                stream.WriteByte((byte) (rangeStart & 0xFF));
                stream.WriteByte((byte) ((leftInRange >> 8) & 0xFF));
                stream.WriteByte((byte) (leftInRange & 0xFF));
            }

            return stream.ToArray();
        }

        private void ReadEncodings()
        {
            throw new NotImplementedException();
            // int format = _fontData.ReadByte();
            // if (format == 0)
            // {
            //     int codeCount = _fontData.ReadByte();
            //     byte[] codes = new byte[codeCount];
            //     for (int i = 0; i < codeCount; i++)
            //     {
            //         codes[i] = _fontData.ReadByte();
            //     }
            // }
            // else if (format == 1)
            // {
            //     int rangeCount = _fontData.ReadByte();
            //     
            // }
            // else
            // {
            //     throw new InvalidOperationException("Unexpected format for encodings");
            // }
        }

        private Dictionary<int,object> ParseDict(byte[] bytes, int start, int count)
        {
            Dictionary<int, object> dict = new Dictionary<int, object>();
            List<object> operands = new List<object>();
            for (int i = start; i < start + count; i++)
            {
                if (bytes[i] <= 21) // Operator
                {
                    int op = bytes[i];
                    if (op == 12)
                    {
                        i++;
                        op = op * 100 + bytes[i];
                    }
                    if (operands.Count == 0)
                    {
                        dict[op] = null;
                    }
                    if (operands.Count == 1)
                    {
                        dict[op] = operands[0];
                    }
                    if (operands.Count == 2)
                    {
                        dict[op] = operands.ToArray();
                    }
                    operands.Clear();
                }
                else if ((bytes[i] >= 32 && bytes[i] <= 254) || bytes[i] == 28 || bytes[i] == 29) // Integer
                {
                    operands.Add(ParseVarInt(bytes, ref i));
                }
                else if (bytes[i] == 30) // Real
                {
                    operands.Add(ParseRealNumber(bytes, ref i));
                }
            }
            return dict;
        }

        private byte[][] ReadIndex()
        {
            int count = _fontData.ReadUShort();
            if (count == 0)
            {
                return new byte[0][];
            }
            byte offSize = _fontData.ReadByte();
            int[] offsets = new int[count + 1];
            for (int i = 0; i < count + 1; i++)
            {
                offsets[i] = ReadOffset(offSize);
            }
            int startPos = _fontData.Position - 1;
            byte[][] data = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                _fontData.Position = startPos + offsets[i];
                data[i] = _fontData.ReadBytes(offsets[i + 1] - offsets[i]);
            }
            return data;
        }

        private int ReadOffset(byte offSize)
        {
            int offset = 0;
            for (int i = 0; i < offSize; i++)
            {
                offset = (offset << 8) | _fontData.ReadByte();
            }
            return offset;
        }

        private int ParseVarInt(byte[] bytes, ref int i)
        {
            byte b0 = bytes[i];
            if (b0 >= 32 && b0 <= 246)
            {
                return b0 - 139;
            }
            if (b0 >= 247 && b0 <= 250)
            {
                byte b1 = bytes[++i];
                return (b0 - 247) * 256 + b1 + 108;
            }
            if (b0 >= 251 && b0 <= 254)
            {
                byte b1 = bytes[++i];
                return -(b0 - 251) * 256 - b1 - 108;
            }
            if (b0 == 28)
            {
                byte b1 = bytes[++i];
                byte b2 = bytes[++i];
                // Cast to short to make sure if the top bit is set it's interpreted as a negative sign
                return (short) (b1 << 8) | b2;
            }
            if (b0 == 29)
            {
                byte b1 = bytes[++i];
                byte b2 = bytes[++i];
                byte b3 = bytes[++i];
                byte b4 = bytes[++i];
                return (b1 << 24) | (b2 << 16) | (b3 << 8) | b4;
            }

            throw new InvalidOperationException("Could not parse varint");
        }

        private double ParseRealNumber(byte[] bytes, ref int i)
        {
            byte b0 = bytes[i];
            if (b0 == 30)
            {
                StringBuilder real = new StringBuilder();
                int n1, n2;
                do
                {
                    byte b = bytes[++i];
                    n1 = (b >> 4) & 0xF;
                    n2 = b & 0xF;
                    if (n1 <= 9) real.Append(n1);
                    if (n1 == 0xa) real.Append('.');
                    if (n1 == 0xb) real.Append('E');
                    if (n1 == 0xc) real.Append("E-");
                    if (n1 == 0xe) real.Append("-");
                    if (n2 <= 9) real.Append(n2);
                    if (n2 == 0xa) real.Append('.');
                    if (n2 == 0xb) real.Append('E');
                    if (n2 == 0xc) real.Append("E-");
                    if (n2 == 0xe) real.Append("-");
                } while (n1 != 0xF && n2 != 0xF);
                return double.Parse(real.ToString(), CultureInfo.InvariantCulture);
            }
            
            throw new InvalidOperationException("Could not parse real number");
        }

        /// <summary>
        /// Converts the font into its binary representation.
        /// </summary>
        public override void Write(OpenTypeFontWriter writer)
        {
            writer.WriteByte(_majorVersion);
            writer.WriteByte(_minorVersion);
            writer.WriteByte(4);
            writer.WriteByte(_offSize);
            
            byte[] nameIndexBytes = SerializeIndex(_nameIndex);
            byte[] stringIndexBytes = SerializeIndex(_stringIndex);
            byte[] globalSubrBytes = SerializeIndex(_globalSubrIndex);

            byte[] charStringBytes = _charStringsIndex != null ? SerializeIndex(_charStringsIndex) : null;
            byte[] charsetsBytes = _charsets != null ? SerializeCharsets() : null;
            byte[] fdSelectBytes = _fdSelect != null ? SerializeFdSelect() : null;
            byte[] fdArrayBytes = _fontDictIndex != null ? SerializeIndex(_fontDictIndex) : null;
            byte[] privateBytes = _privateDict != null ? SerializeDict(_privateDict) : null;
            byte[] subrsBytes = _localSubrIndex != null ? SerializeIndex(_localSubrIndex) : null;

            // As the TopDict size depends on the values of the offsets due to variable-length encoding, the amount of
            // space we actually need is indeterminable, so we just allocate more bytes than we need.
            // Yes, this is a completely insane way for a file format to be encoded, but alas that's not in our control.
            int topDictSpace = SerializeDict(_topDict).Length + 64;
            int startOffset = 4 + nameIndexBytes.Length + stringIndexBytes.Length + globalSubrBytes.Length + topDictSpace;
            int offset = startOffset;

            var newTopDict = new Dictionary<int, object>(_topDict);
            if (charsetsBytes != null)
            {
                newTopDict[OpCharset] = offset;
                offset += charsetsBytes.Length;
            }
            if (fdSelectBytes != null)
            {
                newTopDict[OpFdSelect] = offset;
                offset += fdSelectBytes.Length;
            }
            if (charStringBytes != null)
            {
                newTopDict[OpCharStrings] = offset;
                offset += charStringBytes.Length;
            }
            if (fdArrayBytes != null)
            {
                newTopDict[OpFdArray] = offset;
                offset += fdArrayBytes.Length;
            }
            if (privateBytes != null)
            {
                newTopDict[OpPrivate] = offset;
                offset += privateBytes.Length;
            }
            if (subrsBytes != null)
            {
                newTopDict[OpSubrs] = offset;
                offset += subrsBytes.Length;
            }

            var topDictBytes = SerializeDict(newTopDict);
            
            writer.Write(nameIndexBytes);
            writer.Write(topDictBytes);
            writer.Write(stringIndexBytes);
            writer.Write(globalSubrBytes);
            
            writer.Write(new byte[topDictSpace - topDictBytes.Length]);
            
            if (charsetsBytes != null) writer.Write(charsetsBytes);
            if (fdSelectBytes != null) writer.Write(fdSelectBytes);
            if (charStringBytes != null) writer.Write(charStringBytes);
            if (fdArrayBytes != null) writer.Write(fdArrayBytes);
            if (privateBytes != null) writer.Write(privateBytes);
            if (subrsBytes != null) writer.Write(subrsBytes);
        }

        private byte[] SerializeDict(Dictionary<int,object> dict)
        {
            MemoryStream stream = new MemoryStream();

            foreach (var kvp in dict)
            {
                WriteDictValue(stream, kvp.Value);
                if (kvp.Key >= 1200)
                {
                    stream.WriteByte(12);
                    stream.WriteByte((byte) (kvp.Key - 1200));
                }
                else
                {
                    stream.WriteByte((byte) kvp.Key);
                }
            }
            
            return stream.ToArray();
        }

        private void WriteDictValue(MemoryStream stream, object value)
        {
            if (value is int)
            {
                WriteVarInt(stream, (int) value);
            }
            else if (value is double)
            {
                WriteReal(stream, (double) value);
            }
            else if (value is object[])
            {
                foreach (var element in (object[]) value)
                {
                    WriteDictValue(stream, element);
                }
            }
            else
            {
                throw new InvalidOperationException("Unexpected dict type");
            }
        }

        private void WriteVarInt(MemoryStream stream, int value)
        {
            if (value >= -107 && value <= 107)
            {
                stream.WriteByte((byte)(value + 139));
            } // TODO: Test these, but not using them until guaranteed working
            // else if (value >= 108 && value <= 1131)
            // {
            //     stream.WriteByte((byte) ((value - 108) / 256 + 247));
            //     stream.WriteByte((byte) ((value - 108) % 256));
            // }
            // else if (value >= -1131 && value <= -108)
            // {
            //     stream.WriteByte((byte) ((108 - value) / 256 + 251));
            //     stream.WriteByte((byte) ((value - 108) % 256));
            // }
            else if (value >= -32768 && value <= 32767)
            {
                stream.WriteByte(28);
                stream.WriteByte((byte) (value >> 8));
                stream.WriteByte((byte) (value & 0xFF));
            }
            else
            {
                stream.WriteByte(29);
                stream.WriteByte((byte) ((value >> 24) & 0xFF));
                stream.WriteByte((byte) ((value >> 16) & 0xFF));
                stream.WriteByte((byte) ((value >> 8) & 0xFF));
                stream.WriteByte((byte) (value & 0xFF));
            }
        }

        private void WriteReal(MemoryStream stream, double value)
        {
            string str = value.ToString(CultureInfo.InvariantCulture);
            stream.WriteByte(30);
            for (int i = 0; i < str.Length - 1; i += 2)
            {
                byte b = (byte) ((GetNibble(str[i]) << 4) | GetNibble(str[i + 1]));
                stream.WriteByte(b);
            }
            if (str.Length % 2 == 1)
            {
                stream.WriteByte((byte) ((GetNibble(str[str.Length - 1]) << 4) | 0xF));
            }
            else
            {
                stream.WriteByte(0xFF);
            }
        }

        private byte GetNibble(char c)
        {
            // TODO: Handle "E-" = 0xc case?
            if (c >= '0' && c <= '9') return (byte) (c - '0');
            if (c == '.') return 0xa;
            if (c == 'e' || c == 'E') return 0xb;
            if (c == '-') return 0xe;
            throw new InvalidOperationException();
        }

        private byte[] SerializeIndex(byte[][] index)
        {
            if (index.Length == 0)
            {
                return new byte[2];
            }
            
            int totalBytes = index.Sum(entry => entry.Length);
            byte offSize = (byte) (totalBytes > 0xFFFFFF ? 4 : totalBytes > 0xFFFF ? 3 : totalBytes > 0xFF ? 2 : 1);

            int valuesStart = 3 + (index.Length + 1) * offSize;
            byte[] data = new byte[valuesStart + totalBytes];
            data[0] = (byte) ((index.Length >> 8) & 0xFF);
            data[1] = (byte) (index.Length & 0xFF);
            data[2] = offSize;
            int offset = 0;
            for (int i = 0; i < index.Length; i++)
            {
                WriteOffset(data, 3 + offSize * i, offSize, offset + 1);
                Array.Copy(index[i], 0, data, valuesStart + offset, index[i].Length);
                offset += index[i].Length;
            }
            return data;
        }

        private void WriteOffset(byte[] data, int start, byte offSize, int offset)
        {
            for (int i = 0; i < offSize; i++)
            {
                byte b = (byte) (offset >> ((offSize - i - 1) * 8) & 0xFF);
                data[start + i] = b;
            }
        }
    }
}
