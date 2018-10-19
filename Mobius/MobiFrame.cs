using System;
using System.IO;

namespace Mobius
{
    public class Frame
    {
        protected byte[] _data;
        public MemoryStream Stream { get; set; } = new MemoryStream();
    }

    public class VideoFrame : Frame
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public string Stereo { get; set; }
    }

    public class MoflexVideoFrame : VideoFrame
    {
        static string[] stereoTypes = { "al", "ar", "abl", "abr", "sbsl", "sbsr", null };

        public MoflexVideoFrame(byte[] data)
        {
            _data = data;
            Fps = (double)((_data[2] << 8) + _data[3]) / ((_data[4] << 8) + _data[5]);
            Width = (_data[6] << 8) + _data[7];
            Height = (_data[8] << 8) + _data[9];
            if (data.Length > 12) Stereo = stereoTypes[_data[12] & 0xF]; // with layout
        }
    }

    public class MoflexTimelineFrame : Frame
    {
        public MoflexTimelineFrame(byte[] data) => _data = data;
    }

    public class MoflexAudioFrame : Frame
    {
        public int CodecId { get; set; }
        public int Frequency { get; set; }
        public int Channels { get; set; }

        public AudioCodec GetDecoder()
        {
            switch (CodecId)
            {
                case 0: return new FastAudio(Channels);
                case 1: return new ImaAdpcm(Channels);
                case 2: return new RawPcm16(Channels);
                case 4: return new DspAdpcm(Channels);
                default: throw new NotSupportedException($"Unknown audio codec ID {CodecId}");
            }
        }

        public MoflexAudioFrame(byte[] data)
        {
            _data = data;
            CodecId = _data[1];
            Frequency = ((_data[2] << 16) | (_data[3] << 8) | _data[4]) + 1;
            Channels = _data[5] + 1;
        }
    }
}
