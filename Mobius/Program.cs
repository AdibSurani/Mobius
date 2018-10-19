using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;

namespace Mobius
{
    class Program
    {
        static readonly int maxQueueSize = int.Parse(ConfigurationManager.AppSettings["maxQueueSize"] ?? "256");
        static  string ffmpegPath = ConfigurationManager.AppSettings["ffmpegPath"] ?? @"ffmpeg.exe";
        static readonly string options = ConfigurationManager.AppSettings["options"] ?? "-preset ultrafast -crf 0";
        static readonly string stereoTarget = ConfigurationManager.AppSettings["stereoTarget"] ?? "sbs2l";

        static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            if (!File.Exists(ffmpegPath))
                Console.WriteLine($"ERROR: Unable to find {ffmpegPath}. Please double-check the config file.");
            else if (args.Length == 0)
                Console.WriteLine("ERROR: Please select some .mods or .moflex files to transcode");
            else
            {
                foreach (var path in args)
                {
                    Console.WriteLine($"Transcoding {path}...");
                    try
                    {
                        var stp = Stopwatch.StartNew();
                        Transcode(path, path + ".mp4");
                        stp.Stop();
                        Console.WriteLine($"Completed in {stp.Elapsed:mm\\:ss\\.ff}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Failed to transcode: {e.Message}");
                    }
                }
            }
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        static void Transcode(string inputPath, string outputPath)
        {
            var headers = MobiContainer.GetHeaders(inputPath);
            var headerA = headers.OfType<MoflexAudioFrame>().SingleOrDefault();
            var headerV = headers.OfType<VideoFrame>().Single();

            var decoderA = headerA?.GetDecoder();
            var decoderV = new MobiDecoder(headerV.Width, headerV.Height);
            var pipeName = $"mobius{decoderA?.GetType()?.Name}";

            var stereoFilter = string.IsNullOrEmpty(headerV.Stereo) ? "" : $"-vf stereo3d={headerV.Stereo}:{stereoTarget}";
            var inputArgsA = headerA is null ? "" :
                $@"-thread_queue_size {maxQueueSize} -guess_layout_max 0 -f s16le -ar {headerA.Frequency} -ac {headerA.Channels} -i \\.\pipe\{pipeName}";
            var inputArgsV = $@"-thread_queue_size {maxQueueSize} -f rawvideo -pix_fmt yuv420p -r {headerV.Fps} -s {headerV.Width}x{headerV.Height} -i -";
            var inputArgsO = $@"-y -hide_banner {stereoFilter} {options} -ac {headerA?.Channels ?? 0} ""{ outputPath}""";

            var startInfo = new ProcessStartInfo
            {
                Arguments = $"{inputArgsA} {inputArgsV} {inputArgsO}",
                CreateNoWindow = true,
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.ErrorDataReceived += (s, e) => Console.WriteLine(e.Data);
                process.OutputDataReceived += (s, e) => Console.WriteLine(e.Data);
                process.Start();
                process.BeginErrorReadLine();

                var pipeA = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.WriteThrough, 0, 0);
                var pipeV = process.StandardInput.BaseStream;
                if (headerA != null) pipeA.WaitForConnection();
                var bcA = new BlockingCollection<byte[]>(maxQueueSize);
                var bcV = new BlockingCollection<byte[]>(maxQueueSize);

                void WriteToStream(BlockingCollection<byte[]> bc, Stream stream)
                {
                    foreach (var data in bc.GetConsumingEnumerable())
                        stream.Write(data, 0, data.Length);
                    stream.Flush();
                    stream.Close();
                }
                Task.Run(() => WriteToStream(bcA, pipeA));
                Task.Run(() => WriteToStream(bcV, pipeV));

                foreach (var frame in MobiContainer.Demux(inputPath))
                {
                    if (frame is VideoFrame video)
                        bcV.Add(decoderV.Decode(video.Stream));
                    else if (frame is MoflexAudioFrame audio)
                        bcA.Add(decoderA.Decode(audio.Stream.ToArray()));
                }

                bcA.CompleteAdding();
                bcV.CompleteAdding();
                process.WaitForExit();
            }
        }
    }
}
