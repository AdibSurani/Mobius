# Mobius
Mobius is a program for transcoding Mobiclip videos (`.moflex` or `.mods`) to `.mp4`.
The source is based on Gericom's [MobiclipDecoder](https://github.com/Gericom/MobiclipDecoder).
Mobius also uses ffmpeg for the output — a static binary for Windows can be obtained from the [FFmpeg builds](https://ffmpeg.zeranoe.com/builds/) page.

## Initial setup
The `Mobius.exe.config` file needs to be set up before first use. The most important one of these is:
- `ffmpegPath`: This is the path to `ffmpeg.exe`, such as `C:\ffmpeg\ffmpeg.exe`.

But there are also three other options that can be tweaked: 
- `options`: Roughly speaking, this controls the quality/filesize of the output. The default is `-preset ultrafast -crf 0`, which creates a lossless output very quickly, but at the cost of a larger filesize. See the [x264 encoding options](https://trac.ffmpeg.org/wiki/Encode/H.264#LosslessH.264) page for more options and information.
- `stereoTarget`: For 3D videos, this option automatically converts these videos to use side-by-side stereo, with the left eye on the left (`sbs2l`). This is the recommended option, but other options are available — see [stereo3d filter](https://trac.ffmpeg.org/wiki/Stereoscopic) page for more information.
- `maxQueueSize`: Sets the FIFO size (in frames) for video/audio synchronisation. Defaults to 256, and should not need changing.

## How to use
Once the `Mobius.exe.config` file is setup, simply drag any number of Mobiclip videos (.moflex or .mods) onto `Mobius.exe` and it will convert them to `.mp4`.

Alternatively, the command line can also be used, e.g.:
```
Mobius.exe input1.moflex input2.mods
```
This will create `input1.moflex.mp4` and `input2.mods.mp4`

## Caveats
- `.mods` audio will not be converted.
- Very limited testing has been done, so expect plenty of bugs.