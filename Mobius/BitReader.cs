using System.Collections.Generic;
using System.IO;

namespace Mobius
{
    class BitReader : BinaryReader
    {
        public BitReader(Stream stream, bool bigEndian) : base(stream)
        {
            _bigEndian = bigEndian;
        }

        int pos, last;
        readonly bool _bigEndian;

        public override short ReadInt16() => _bigEndian ? (short)((ReadByte() << 8) | ReadByte()) : base.ReadInt16();

        public void ResetBits() => pos = 0;

        public bool Pop()
        {
            if (_bigEndian && pos++ % 8 == 0)
                last = ReadByte() << 24;
            else if (!_bigEndian && pos++ % 16 == 0)
                last = ReadInt16() << 16;
            else
                last <<= 1;
            return last < 0;
        }

        public int PopInt(int n)
        {
            var value = 0;
            for (int i = 0; i < n; i++)
                value = 2 * value + (Pop() ? 1 : 0);
            return value;
        }

        public int PopSignedInt(int n)
        {
            var sign = Pop() ? -1 : 0;
            return (sign << (n - 1)) + PopInt(n - 1);
        }

        public int PopExpGolomb()
        {
            int n = PopLength() - 1;
            return PopInt(n) + (1 << n) - 1;
        }

        public int PopSignedExpGolomb()
        {
            var n = PopExpGolomb();
            return -((n >> 1) ^ (-(n & 1)));
        }

        public int PopLength()
        {
            int n = 1;
            while (!Pop()) n++;
            return n;
        }

        public int PopVLC(string s)
        {
            for (var i = 1; ; i = 2 * i + PopInt(1))
                if (s[i] != '.')
                    return s[i] - '0';
        }

        public T PopVLC<T>(Dictionary<int, T> dic)
        {
            for (var i = 1; ; i = 2 * i + PopInt(1))
                if (dic.TryGetValue(i, out var n))
                    return n;
        }
    }
}
