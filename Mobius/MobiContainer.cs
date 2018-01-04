using System;
using System.Collections.Generic;
using System.IO;

namespace Mobius
{
    class MobiContainer
    {
        public class Frame
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public double Fps { get; set; }
            public MemoryStream Stream { get; set; } = new MemoryStream();
            public string Stereo { get; set; }
        }

        public class MoflexFrame : Frame
        {
            byte _type;
            byte[] _data;
            public bool IsVideo => _type % 2 == 1;
            static string[] stereoTypes = { "al", "ar", "abl", "abr", "sbsl", "sbsr", null };

            public MoflexFrame(byte type, byte[] data)
            {
                _type = type;
                _data = data;

                if (!IsVideo) return;
                Fps = (double)((_data[2] << 8) + _data[3]) / ((_data[4] << 8) + _data[5]);
                Width = (_data[6] << 8) + _data[7];
                Height = (_data[8] << 8) + _data[9];
                if (data.Length > 12) Stereo = stereoTypes[_data[12] & 0xF];
            }
        }

        static IEnumerable<Frame> DemuxMoflex(Stream stream)
        {
            int packetSize = 0;
            var chunks = new List<MoflexFrame>();

            using (var br = new BitReader(stream, true))
                while (br.BaseStream.Position < br.BaseStream.Length)
                {
                    var startOffset = br.BaseStream.Position;

                    if (br.ReadInt16() == 0x4C32) // L2
                    {
                        var unknown = br.ReadBytes(10);
                        packetSize = br.ReadInt16() + 1;
                        chunks.Clear();

                        while (true)
                        {
                            var type = br.ReadByte();
                            if (type > 4) throw new NotImplementedException("Unknown chunk type");
                            var bytes = br.ReadBytes(br.ReadByte());
                            if (type == 0) break;
                            chunks.Add(new MoflexFrame(type, bytes));
                        }
                    }
                    else
                        br.BaseStream.Position -= 2;

                    var flags = br.ReadByte();
                    if ((flags & 2) != 0) throw new NotImplementedException("Unknown flag mask");
                    while (br.BaseStream.Position < startOffset + packetSize && br.ReadByte() != 0)
                    {
                        br.BaseStream.Position--;
                        br.ResetBits();

                        var frame = chunks[br.PopInt(br.PopLength())];
                        var last = br.Pop();
                        if (last)
                            br.PopInt(br.PopInt(br.PopLength() + 1) * 0 + br.PopLength() * 2 + 26);
                        var bytes = br.ReadBytes(br.PopInt(13) + 1);
                        frame.Stream.Write(bytes, 0, bytes.Length);
                        if (last)
                        {
                            frame.Stream.Position = 0;
                            if (frame.IsVideo)
                                yield return frame; // we have a complete frame -- let's do stuff to it
                            frame.Stream = new MemoryStream();
                        }
                    }

                    if (flags % 2 == 0)
                        br.BaseStream.Position = startOffset + packetSize;
                }
        }

        static IEnumerable<Frame> DemuxMods(Stream stream)
        {
            using (var br = new BinaryReader(stream))
            {
                br.ReadBytes(8);
                var frameCount = br.ReadInt32();
                var frame = new Frame
                {
                    Width = br.ReadInt32(),
                    Height = br.ReadInt32(),
                    Fps = br.ReadInt32() / (double)0x1000000
                };
                br.ReadBytes(16);
                stream.Position = br.ReadInt32() + 4;
                stream.Position = br.ReadInt32();
                for (int i = 0; i < frameCount; i++)
                {
                    frame.Stream = new MemoryStream(br.ReadBytes(br.ReadInt32() >> 14));
                    yield return frame;
                }
            }
        }

        public static IEnumerable<Frame> Demux(string path)
        {
            var stream = File.OpenRead(path);
            var b = stream.ReadByte();
            stream.Position--;
            switch (b)
            {
                case (byte)'L': return DemuxMoflex(stream);
                case (byte)'M': return DemuxMods(stream);
                default: throw new NotSupportedException("Unknown filetype");
            }
        }
    }
}
