using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mobius
{
    class MobiContainer
    {
        static IEnumerable<Frame> GetChunks(BitReader br)
        {
            while (true)
            {
                var type = br.ReadByte();
                var bytes = br.ReadBytes(br.ReadByte());
                switch (type)
                {
                    case 0:
                        yield break;
                    case 1:
                    case 3:
                        yield return new MoflexVideoFrame(bytes);
                        break;
                    case 2:
                        yield return new MoflexAudioFrame(bytes);
                        break;
                    case 4:
                        yield return new MoflexTimelineFrame(bytes);
                        break;
                    default:
                        throw new NotImplementedException($"Unknown chunk type {type}");
                }
            }
        }

        static IEnumerable<Frame> DemuxMoflex(Stream stream)
        {
            int packetSize = 0;
            var chunks = new List<Frame>();

            using (var br = new BitReader(stream, true))
                while (br.BaseStream.Position < br.BaseStream.Length)
                {
                    var startOffset = br.BaseStream.Position;

                    if (br.ReadInt16() == 0x4C32) // L2
                    {
                        var unknown = br.ReadBytes(10);
                        packetSize = br.ReadInt16() + 1;
                        chunks = GetChunks(br).ToList();
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
                            yield return frame;
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
                var frame = new VideoFrame
                {
                    Width = br.ReadInt32(),
                    Height = br.ReadInt32(),
                    Fps = br.ReadInt32() / (double)0x1000000
                };
                br.ReadBytes(16);
                stream.Position = br.ReadInt32() + 4;
                stream.Position = br.ReadInt32();
                Console.WriteLine(frameCount);
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

        public static List<Frame> GetHeaders(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var b = stream.ReadByte();
                stream.Position--;
                if (b == 'L')
                {
                    using (var br = new BitReader(stream, true))
                    {
                        br.ReadBytes(14);
                        return GetChunks(br).ToList();
                    }
                }
                else if (b == 'M')
                {
                    using (var br = new BinaryReader(stream))
                    {
                        br.ReadBytes(12);
                        return new List<Frame>
                        {
                            new VideoFrame
                            {
                                Width = br.ReadInt32(),
                                Height = br.ReadInt32(),
                                Fps = br.ReadInt32() / (double)0x1000000
                            }
                        };
                    }
                }
                else
                {
                    throw new NotSupportedException("Unknown filetype");
                }
            }
        }
    }
}
