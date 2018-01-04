using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;

namespace Mobius
{
    class Program
    {
        static string ffmpegPath = ConfigurationManager.AppSettings["ffmpegPath"] ?? @"ffmpeg.exe";
        static string options = ConfigurationManager.AppSettings["options"] ?? "-preset ultrafast -crf 0";
        static string stereoTarget = ConfigurationManager.AppSettings["stereoTarget"] ?? "sbs2l";

        static void Main(string[] args)
        {
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
            var startInfo = new ProcessStartInfo
            {
                CreateNoWindow = true,
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };

            using (var proc = new Process { StartInfo = startInfo })
            {
                MobiDecoder decoder = null;
                foreach (var video in MobiContainer.Demux(inputPath))
                {
                    if (decoder == null)
                    {
                        var stereoFilter = string.IsNullOrEmpty(video.Stereo) ? "" : $"-vf stereo3d={video.Stereo}:{stereoTarget}";
                        startInfo.Arguments = $"-f rawvideo -r {video.Fps} -pix_fmt yuv420p -s {video.Width}x{video.Height}"
                                            + $" -i pipe:0 {stereoFilter} -hide_banner {options} -y \"{outputPath}\"";
                        proc.Start();
                        proc.ErrorDataReceived += (s, e) => Console.WriteLine(e.Data);
                        proc.BeginErrorReadLine();
                        decoder = new MobiDecoder(video.Width, video.Height);
                    }
                    foreach (var bytes in decoder.Parse(video.Stream))
                        proc.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                }
                proc.StandardInput.BaseStream.Close();
                proc.WaitForExit();
            }
        }
    }
}
