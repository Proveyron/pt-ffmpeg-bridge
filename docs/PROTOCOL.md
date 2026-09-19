# Pro Tools ⇄ QuickTime server protocol (DIPC)

Observed on Pro Tools 12.5 (Windows, x64) with `ProToolsQuickTimeServer.exe` 1.0.0.85 and QuickTime 7.7.9.
Everything here was recovered from the installed binaries and from pipe traffic captured with Frida while
importing files; no Avid code or headers are included in this repository.

## Who talks to whom

```
ProTools.exe (x64)
 └─ FFmt.dll                 file-format layer; native readers: WAVE, AIFF, MP3, REX, UAF, …
    │                        any other file → FF_QuickTimeAudioReader
    └─ avx2_plug-ins\ProTools_QuickTime_OPClient.avx   ("out-of-process client")
          │  CreateProcessW  QuickTimeServer\ProToolsQuickTimeServer.exe <ProToolsPID>
          │  named pipes     \\.\pipe\DIPC_active_<ProToolsPID>, \\.\pipe\DIPC_passive_<serverPID>
          │  shared memory   Local\DIPC_Shm_%08X
          ▼
    ProToolsQuickTimeServer.exe (x86) → QuickTime_Wrapper.avx → QuickTime.qts (MovieAudioExtraction*)
```

**Routing:** Pro Tools sends *every* file its native readers do not recognize to the server. FLAC,
OGG, Opus, WavPack, … all reach `NewMovieFromFilePath`; with QuickTime they fail there
(`acfResult = 0x80000008`) and the import dialog says "unreadable by Pro Tools". A server that can
decode them makes them import natively.

The server is started once when Pro Tools launches and lives until Pro Tools exits.

## Process start and handshake

1. Pro Tools creates `\\.\pipe\DIPC_active_<ptPid>` (inbound, message mode) and launches
   `ProToolsQuickTimeServer.exe <ptPid>`. The argument must be 1–7 decimal digits.
2. The server opens a SYNCHRONIZE handle on the parent (it exits with it), creates its own inbound pipe
   `\\.\pipe\DIPC_passive_<serverPid>` and opens the parent's pipe for writing.
3. Pro Tools connects to the passive pipe. Each side only ever *reads* its own pipe and *writes* the peer's.

Pipes: `PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED`, `PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE |
PIPE_REJECT_REMOTE_CLIENTS`, 64 KB in-buffer. One `WriteFile` = one frame.

## Frame format

All integers little-endian.

| offset | size | field |
|---|---|---|
| 0 | 4 | tag: `djb2(ASCII method name)`; every reply uses `djb2("GenericFutureCompletion") = 0xe1009bf7` |
| 4 | 4 | sender timestamp (µs tick; informational) |
| 8 | 4 | request id; the reply echoes it; `0` for one-way messages |
| 12 | 4 | flags: `0x01000000` = expects a reply |
| 16 | 4 | part count, `1` |
| 20 | 4 | payload length |
| 24 | n | protobuf payload (proto2, package `QTWrapperProto`) |

Parameterless requests (`CreateQuickTimeWrapperInterface`) are 20 bytes: offset 16 holds the constant
`0x7644CC87` and there is no payload.

`djb2`: `h = 5381; for c in name: h = h * 33 + c (mod 2^32)`.

The server may also send unsolicited `ConsoleMessage{level, msg}` frames with request id 0 (QuickTime
error text). Clients should ignore them.

## Schema

`ProToolsQuickTimeServer.exe` embeds its serialized `FileDescriptorProto` (`QuickTimeWrapper.proto`,
252 messages). `tools/extract_proto.py` turns it back into `.proto` source. Request types are
`CS_<Method>` (or `CS_RemoteInterfaceID` when the only argument is the interface), replies are
`SC_<Method>` (or `SC_Error`). Integers declared `sint32/sint64` are zigzag-encoded on the wire, so
e.g. `bytes_per_sample = 2` appears as the byte `04`.

## Audio import call sequence (captured)

```
CreateQuickTimeWrapperInterface()                      → remote_interface_id (e.g. 0x0200000000000013)
NewMovieFromFilePath(id, path: StringContainer{type=2, UTF-16LE, NUL-terminated})
                                                       → acfResult 0, or 0x80000008 = unreadable
QTWGetMovieTrackCount(id)                              → track count (QuickTime counted video tracks too)
DetermineMovieEditRateAndLength(id, 0, 0, 0)           → 50/1 for audio-only (video fps otherwise),
                                                         movieLength, trackLengthInFramesVector = [0, len per track…]
QTWGetTrackID(id, 1)                                   → 1
GetQTAudioReader(id, use_cache=false)                  → SC_Error ok
QTWGetMovieTrackCount / IsTrackAnAudioTrack(id, t)     → per track
GetAudioInfoForTrack(id, 1)                            → channels, sampleRate, formatID ('aac '), dataFormat ('mp4a'),
                                                         sampleSize 16, …
IsAudioTrackVBR(id, 1)                                 → true: QuickTime's answer for every track
QTWGetMediaSampleCount(id, 1)                          (only when not VBR; see below)
QTWGetMediaDuration / QTWGetMediaTimeScale (1-based track)
SetParamsForProToolsImport(id, track 1, bytes_per_sample 2, is_little_endian false, sample_rate 44100)
                                                       → num_channels
   — everything above also happens for each file merely *clicked* in the import browser —
ExtractAudio(id, track, start_sample, 16128, buffer_size, stereo=true, channel 0)   (repeated)
   → samples_extracted, sh_mem MemoryDescriptor{id, address, size}
SH_RemoteFreeMem(MemoryDescriptor)                     one-way, after Pro Tools has copied the block
DestroyQuickTimeInterface(id)                          → SC_Error ok
```

Audio arrives as interleaved integer PCM in the requested width/endianness, big-endian. Pro Tools asks
for the sample size reported by `GetAudioInfoForTrack.sampleSize`: 16 → 2 bytes, 24 → 3 bytes (QuickTime
reported 16 for AAC, which capped lossy imports at 16 bits).

### Lessons from live testing (Pro Tools 12.5)

- **VBR tracks are over-read by 0.4%.** When `IsAudioTrackVBR` is true, Pro Tools extracts exactly
  `1.004 × QTWGetMediaDuration` samples and keeps all of them in the imported file. QuickTime called every
  track VBR, so every QuickTime import carried a silent tail (7.5 s on a 31-minute file).
- **Non-VBR tracks take a different path:** after `IsAudioTrackVBR = false`, Pro Tools calls
  `QTWGetMediaSampleCount(track) → medDurInSamples` and extracts exactly that many samples. This path never
  appears in a QuickTime capture; answering it wrongly makes the file "unreadable".
- **`samples_extracted` is ignored.** Pro Tools always copies `samples_to_extract × frame size` bytes
  from the shared-memory block. A short final read therefore imports whatever stale data sits in shared
  memory; the server must always fill the whole buffer (zeros past the end).
- **Channel order is film order.** Pro Tools labels multichannel audio L C R Ls Rs LFE (5.1), so
  SMPTE/WAV-ordered audio (L R C LFE Ls Rs) must be reordered before extraction.
- On startup Pro Tools calls `CreateQuickTimeWrapperInterface`, `QTWGetQuickTimeInfo`,
  `DestroyQuickTimeInterface`; `QTPresent = true` with a 7.x version keeps QuickTime features enabled.

## Shared memory

On the first extraction the server creates a 128 MB pagefile-backed mapping named
`Local\DIPC_Shm_%08X` with a random id. `MemoryDescriptor.id` is that id, `address` is the byte offset of
the block inside the mapping and `size` is the mapping size. The client opens the mapping by name,
copies `samples_extracted × channels × bytes_per_sample` bytes from `address`, then sends
`SH_RemoteFreeMem` with the same descriptor.

## Reproducing the capture

`tools/dipc_client.py` implements the client side above and can drive either Avid's server or the
replacement. Pipe traffic of a live Pro Tools session was recorded by attaching Frida to the helper
process only (never to `ProTools.exe`, which is copy-protected) and hooking `ReadFile`, `WriteFile`,
`GetOverlappedResult`, `CreateFileMappingW` and `MapViewOfFile` in `kernel32`.
