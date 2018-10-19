using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mobius
{
    public abstract class AudioCodec
    {
        public int Channels { get; }
        public abstract byte[] Decode(byte[] data);

        protected AudioCodec(int channels) => Channels = channels;

        protected byte[] Interleave(Func<int, IEnumerable<short>> sampleFunc)
        {
            using (var bw = new BinaryWriter(new MemoryStream()))
            {
                var enums = Enumerable.Range(0, Channels).Select(c => sampleFunc(c).GetEnumerator()).ToList();
                while (enums.All(e => e.MoveNext()))
                {
                    foreach (var e in enums)
                        bw.Write(e.Current);
                }
                return ((MemoryStream)bw.BaseStream).ToArray();
            }
        }

        protected static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        protected static short ClampS16(int value) => (short)Clamp(value, short.MinValue, short.MaxValue);
    }

    public class RawPcm16 : AudioCodec
    {
        public RawPcm16(int channels) : base(channels) { }
        public override byte[] Decode(byte[] data) => data;
    }

    public class ImaAdpcm : AudioCodec
    {
        static readonly int[] indexTable = { -1, -1, -1, -1, 2, 4, 6, 8 };
        static readonly int[] stepTable = Enumerable.Range(0, 89).Select(i => (int)(7.464 * Math.Pow(1.1, i) + 0.08)).ToArray();

        public ImaAdpcm(int channels) : base(channels) { }

        static IEnumerable<short> DecodeFrame(IEnumerable<byte> source, short pred, int index)
        {
            foreach (var nibble in source.SelectMany(b => new[] { b & 0xF, b >> 4 }))
            {
                var diff = (2 * (nibble & 7) + 1) * stepTable[index] / 8 * (nibble < 8 ? 1 : -1);
                (pred, index) = (ClampS16(pred + diff), Clamp(index + indexTable[nibble & 7], 0, 88));
                yield return pred;
            }
        }

        public override byte[] Decode(byte[] data)
        {
            // 4 bytes per channel header
            // each frame is 128 bytes --> 256 samples
            return Interleave(i => DecodeFrame(
                from j in Enumerable.Range(0, (data.Length - 4 * Channels) / 256)
                from b in new ArraySegment<byte>(data, 128 * (i + Channels * j) + 4 * Channels, 128)
                select b,
                BitConverter.ToInt16(data, 4 * i + 2), BitConverter.ToInt16(data, 4 * i)));
        }
    }

    public class DspAdpcm : AudioCodec
    {
        public DspAdpcm(int channels) : base(channels) { }

        static IEnumerable<short> DecodeFrame(IEnumerable<byte> source, short[] coefs)
        {
            var (scale, c1, c2, h1, h2) = (0, 0, 0, coefs[17], coefs[18]);
            foreach (var (b, isHeader) in source.Select((x, i) => (x, i % 8 == 0)))
            {
                if (isHeader)
                    (scale, c1, c2) = (1 << (b & 0xF), coefs[b / 16 * 2], coefs[b / 16 * 2 + 1]);
                else
                    foreach (var nibble in new[] { b >> 4, b & 0xF })
                    {
                        var pred = (1024 + c1 * h1 + c2 * h2) >> 11;
                        (h1, h2) = (ClampS16(((nibble ^ 8) - 8) * scale + pred), h1);
                        yield return h1;
                    }
            }
        }

        public override byte[] Decode(byte[] data)
        {
            // 38 bytes per channel header
            // each frame is 128 bytes --> 256 samples
            var size = data.Length / Channels;
            return Interleave(i => DecodeFrame(new ArraySegment<byte>(data, size * i + 38, size - 38),
                Enumerable.Range(0, 19).Select(j => BitConverter.ToInt16(data, size * i + 2 * j)).ToArray()).Skip(32));
        }
    }

    public class FastAudio : AudioCodec
    {
        class FrameDecoder
        {
            double[] f = new double[8];
            double last;

            public IEnumerable<short> DecodeFrame(byte[] source, int offset)
            {
                var lst = Enumerable.Range(0, 10).Select(i => BitConverter.ToInt32(source, 4 * i + offset)).ToList();
                int pos = 0;
                int Read(int bits)
                {
                    pos += bits;
                    var r = lst[(pos - 1) / 32] >> (32 - pos % 32);
                    return r & ((1 << (bits % 32)) - 1);
                }

                var m = tbls.Zip(new[] { 6, 6, 5, 5, 4, 0, 3, 3 }, (t, i) => t[Read(i)]).Reverse().ToList();
                var inds = Enumerable.Range(0, 4).Select(_ => Read(6)).Reverse().ToList();
                var pads = Enumerable.Range(0, 4).Select(_ => Read(2)).Reverse().ToList();
                var result = new double[256];
                for (int i = 0, index5 = 0; i < 4; i++)
                {
                    var tblVal = BitConverter.ToSingle(BitConverter.GetBytes((inds[i] + 1) << 20), 0) * Math.Pow(2, 116);
                    void SetSample(int j, int v) => result[i * 64 + pads[i] + j * 3] = tblVal * (2 * v - 7);

                    for (int j = 0, tmp = 0; j < 21; j++)
                    {
                        SetSample(j, j == 20 ? tmp / 2 : Read(3));
                        if (j % 10 == 9) tmp = 4 * tmp + Read(2);
                        if (j == 20) index5 = 2 * index5 + tmp % 2;
                    }
                    m[2] = tbls[5][index5];
                }

                // We already finished reading all the stuff
                // Now we can process the stuff we need
                foreach (var sample in result)
                {
                    var x = sample; // make a local copy
                    for (int j = 0; j < 8; j++)
                    {
                        x -= m[j] * f[j];
                        f[j] += m[j] * x;
                    }
                    f = f.Skip(1).Concat(new[] { x }).ToArray();
                    last = x + last * 0.86;
                    yield return ClampS16((int)(last * 65536));
                }
            }

            // filter coefficients
            static readonly double[] tbl0 = Enumerable.Range(0, 8).Select(i => (i - 159.5) / 160)
                .Concat(Enumerable.Range(0, 11).Select(i => (i - 37.5) / 40))
                .Concat(Enumerable.Range(0, 27).Select(i => (i - 13.0) / 20))
                .Concat(Enumerable.Range(0, 11).Select(i => (i + 27.5) / 40))
                .Concat(Enumerable.Range(0, 7).Select(i => (i + 152.5) / 160))
                .ToArray();
            static readonly double[] tbl2 = Enumerable.Range(0, 7).Select(i => (i - 33.5) / 40)
                .Concat(Enumerable.Range(0, 25).Select(i => (i - 13.0) / 20))
                .ToArray();
            static readonly double[][] tbls = new[] {
                tbl0, tbl0, tbl2, tbl2.Select(x => -x).Reverse().ToArray(),
                Enumerable.Range(0, 16).Select(i => i * 0.22 / 3 - 0.6).ToArray(),
                Enumerable.Range(0, 16).Select(i => i * 0.20 / 3 - 0.3).ToArray(),
                Enumerable.Range(0, 8).Select(i => i * 0.36 / 3 - 0.4).ToArray(),
                Enumerable.Range(0, 8).Select(i => i * 0.34 / 3 - 0.2).ToArray()
            };
        }

        readonly FrameDecoder[] decoders;

        public FastAudio(int channels) : base(channels) => 
            decoders = Enumerable.Range(0, channels).Select(_ => new FrameDecoder()).ToArray();

        public override byte[] Decode(byte[] data)
        {
            // 0 bytes per channel header
            // each frame is 40 bytes --> 256 samples
            return Interleave(i => from j in Enumerable.Range(0, data.Length / 40 / Channels)
                                     from s in decoders[i].DecodeFrame(data, 40 * (i + Channels * j))
                                     select s);
        }
    }
}
