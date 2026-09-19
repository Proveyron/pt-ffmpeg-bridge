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

## Install

1. Quit Pro Tools.
2. Download `pt-ffmpeg-bridge-setup-<version>.exe` from
   [Releases](https://github.com/Proveyron/pt-ffmpeg-bridge/releases) and run it (it asks for admin rights).
   The installer is not code-signed, so SmartScreen may warn: choose *More info → Run anyway*.
3. Start Pro Tools and import as usual (**File → Import → Audio**). FLAC, OGG, Opus, … now convert.

Setup installs the bridge and a bundled FFmpeg (unmodified LGPL build from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)) into `C:\Program Files\pt-ffmpeg-bridge`,
backs up Avid's helper as `QuickTimeServer\ProToolsQuickTimeServer.orig.exe` and puts the bridge in its
place. Uninstalling from **Apps & Features** restores Avid's original. QuickTime 7 is no longer needed
and can be uninstalled.

Silent install: `pt-ffmpeg-bridge-setup-<version>.exe /VERYSILENT`, plus `/PTDIR="D:\Avid\Pro Tools"` for a
non-default Pro Tools folder.

Requirements: Windows 10/11 x64 and a Pro Tools version that ships
`QuickTimeServer\ProToolsQuickTimeServer.exe` (Pro Tools 12.x for Windows).

### Configuration

The installer writes `QuickTimeServer\bridge.json`:

```json
{ "ffmpegPath": "C:\\...\\ffmpeg.exe", "ffprobePath": "C:\\...\\ffprobe.exe", "exactLengths": true, "debugLog": false }
```

- `exactLengths: false` reports every track as VBR like QuickTime did (imports get a silent 0.4% tail).
- `debugLog: true` logs every request and reply.

Logs: `%LOCALAPPDATA%\pt-ffmpeg-bridge\logs`.

## Build

Needs Pro Tools installed (the protocol schema is read from its helper), .NET 8 SDK, Python 3 with
`protobuf`, and Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
.\build-installer.ps1 -Version 0.1.0   # tests, publish, fetch pinned FFmpeg (SHA-256 checked), build build\out\*-setup-*.exe
```

The schema extracted to `proto/QuickTimeWrapper.proto` comes from Avid's binary, so it is generated on the
build machine and never committed. Development loop without the installer:

```powershell
python tools\extract_proto.py
dotnet test
dotnet publish src\Bridge.Server -c Release -o dist
.\install.ps1      # installs dist\ using FFmpeg from PATH; .\uninstall.ps1 restores Avid's helper
```

The test tools need `pip install protobuf pywin32 numpy` (and `frida` for `trace_server.py`).

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
installer/            Inno Setup script (build-installer.ps1 builds the single setup exe)
tests/Bridge.Tests    unit tests (framing checked against captured Pro Tools traffic)
tools/                schema extractor, DIPC test client, end-to-end verifier, Frida traffic tracer
```

Not affiliated with or endorsed by Avid Technology or Apple. "Pro Tools" and "QuickTime" are trademarks
of their respective owners; this project exists for interoperability.
