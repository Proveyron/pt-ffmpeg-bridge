# pt-ffmpeg-bridge

An open-source, FFmpeg-backed drop-in replacement for the QuickTime helper that Pro Tools uses to
import audio it can't read natively. It removes the dependency on QuickTime 7 for Windows (abandoned
in 2016, with unpatched vulnerabilities) and lets the Import Audio dialog accept anything FFmpeg
decodes: FLAC, ALAC, AAC/M4A, MP4/MOV audio, OGG Vorbis, Opus, WavPack, APE, WMA, AC-3/E-AC-3,
DTS, multichannel, 24-bit/high-sample-rate, …

Tested live with Pro Tools 12.5 on Windows 10: FLAC, ALAC, AAC/M4A, MP4/MOV audio, OGG, Opus, WavPack
and MP3 import through the normal Import Audio dialog. A 31-minute 24-bit FLAC imports bit-exact at its
exact length, and 5.1 files land in the right channels.

## How it works

Pro Tools never calls QuickTime directly. It launches `QuickTimeServer\ProToolsQuickTimeServer.exe`
and talks to it over named pipes using a protobuf protocol; decoded PCM comes back through shared
memory. Any file Pro Tools' own readers don't recognize is sent to that helper. This project
re-implements the helper's audio side with `ffprobe`/`ffmpeg`, so no QuickTime code runs at all.
The protocol is documented in [docs/PROTOCOL.md](docs/PROTOCOL.md).

Decoding details:

- Sample-accurate: the requested stream is decoded once to a temporary PCM cache and served by
  sample range. Encoder delay is handled by FFmpeg from the file's edit list (QuickTime blindly
  dropped 2112 samples from AAC).
- Output is exactly what Pro Tools asks for (bit depth, endianness, sample rate). Resampling uses
  SoX (`soxr`, precision 28); reducing to 16-bit applies TPDF dither; lossless → same-or-higher bit
  depth is bit-exact.
- Pro Tools requests whatever sample size the server reports, so everything except 16-bit lossless
  sources is reported as 24-bit (QuickTime reported AAC as 16-bit, capping lossy imports at 16 bits).
- Files whose container states an exact length are imported at exactly that length. QuickTime
  called every track VBR, and Pro Tools pads VBR imports by 0.4% (7.5 s of silence on a 31-minute file).
- Multichannel audio is reordered to Pro Tools' film order (L C R Ls Rs LFE).

## Requirements

- Pro Tools for Windows (x64) that ships `QuickTimeServer\ProToolsQuickTimeServer.exe` (12.x)
- FFmpeg with `ffmpeg.exe` and `ffprobe.exe` — e.g. `winget install Gyan.FFmpeg`
- To build: .NET 8 SDK, Python 3 with `protobuf` (only to extract the schema)
- For the test tools: `pip install protobuf pywin32 numpy` (and `frida` for `trace_server.py`)

## Build

```powershell
python tools\extract_proto.py            # reads the schema out of your installed ProToolsQuickTimeServer.exe
dotnet test
dotnet publish src\Bridge.Server -c Release -o dist
```

The extracted `proto/QuickTimeWrapper.proto` is derived from Avid's binary, so it is generated on
your machine and never committed.

## Install / uninstall

Close Pro Tools, then:

```powershell
.\install.ps1      # backs up Avid's exe as ProToolsQuickTimeServer.orig.exe, installs the bridge
.\uninstall.ps1    # restores the original
```

After installing you can uninstall QuickTime 7 from Windows' Apps & Features.

Optional `bridge.json` next to the installed exe (install.ps1 writes one):

```json
{ "ffmpegPath": "C:\\...\\ffmpeg.exe", "ffprobePath": "C:\\...\\ffprobe.exe", "exactLengths": true, "debugLog": false }
```

- `exactLengths: false` reports every track as VBR like QuickTime did (imports get a silent 0.4% tail).
- `debugLog: true` logs every request and reply (`install.ps1 -DebugLog` sets it).

Logs: `%LOCALAPPDATA%\pt-ffmpeg-bridge\logs`.

## Verify without Pro Tools

`tools/dipc_client.py` plays Pro Tools' role (same handshake and call sequence) against any server exe;
`tools/verify_server.py` runs it over a set of files and compares the PCM with a direct FFmpeg decode:

```powershell
python tools\verify_server.py dist\ProToolsQuickTimeServer.exe testmedia\*
```

## Limitations

- Audio only. Video decoding calls (used by the Video Engine for QuickTime movies) are answered with
  errors, so importing *video* from .mov files needs another path once QuickTime is removed. Importing
  the *audio* of a video file works.
- QuickTime export (Bounce to QuickTime) is not implemented.
- Files without a stated length (raw ADTS `.aac`, MP3 without a Xing/LAME header) keep a short silent
  tail, because their duration is only an estimate. Pro Tools reads WAV, AIFF and MP3 natively anyway.

## Project layout

```
src/Bridge.Protocol   DIPC framing, named pipes, shared memory, protobuf messages (generated)
src/Bridge.Decode     ffprobe probing, ffmpeg PCM cache, channel selection
src/Bridge.Server     ProToolsQuickTimeServer.exe: method handlers
tests/Bridge.Tests    unit tests (framing checked against captured Pro Tools traffic)
tools/                schema extractor, DIPC test client, end-to-end verifier, Frida traffic tracer
```

Not affiliated with or endorsed by Avid Technology or Apple. "Pro Tools" and "QuickTime" are trademarks
of their respective owners; this project exists for interoperability.
