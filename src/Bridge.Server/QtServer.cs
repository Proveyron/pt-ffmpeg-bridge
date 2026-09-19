using Bridge.Decode;
using Bridge.Protocol;
using Bridge.Protocol.Messages;
using Google.Protobuf;

namespace Bridge.Server;

/// <summary>
/// Implements the QuickTime-wrapper methods Pro Tools calls when it opens and converts a file it
/// has no native reader for. Everything is answered from ffprobe/ffmpeg; QuickTime is never used.
/// </summary>
public sealed class QtServer : IDisposable
{
    const long InterfaceIdBase = 0x0200000000000000; // same shape as the ids QuickTime's server hands out
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(5);

    readonly FfmpegTools _tools;
    readonly BridgeConfig _config;
    readonly Dictionary<long, MovieSession> _sessions = new();
    readonly Dictionary<uint, Func<ReadOnlyMemory<byte>, IMessage>> _handlers = new();
    readonly HashSet<uint> _oneWay = [];
    SharedMemoryPool? _shm;
    long _nextInterface = 1;

    public QtServer(FfmpegTools tools, BridgeConfig config)
    {
        _tools = tools;
        _config = config;

        // Lifecycle
        On<CS_RemoteInterfaceID>("CreateQuickTimeWrapperInterface", _ => new SC_CreateQuickTimeWrapperInterface { RemoteInterfaceId = NewSession() });
        On<CS_RemoteInterfaceID>("CreateQuickTimePropertyInterface", _ => new SC_CreateQuickTimePropertyInterface { RemoteInterfaceId = NewSession() });
        On<CS_RemoteInterfaceID>("DestroyQuickTimeInterface", r => { EndSession(r.RemoteInterfaceId); return new SC_Error { Error = Replies.Ok(), AcfResult = Replies.AcfOk }; });
        On<CS_RemoteInterfaceID>("QTWGetQuickTimeInfo", _ => new SC_QTWGetQuickTimeInfo { Error = Replies.Ok(), AcfResult = Replies.AcfOk, QTPresent = true, QTVersion = _config.ReportedQuickTimeVersion });
        OnSuccess("InitQT", "InitQT2", "ReleaseQT");
        OneWay("SH_RemoteFreeMem"); // blocks are recycled by the pool; nothing to do

        // Opening a file and describing it
        On<CS_NewMovieFromFilePath>("NewMovieFromFilePath", NewMovieFromFilePath);
        On<CS_RemoteInterfaceID>("QTWGetMovieTrackCount", r => new SC_QTWGetMovieTrackCount { Error = Replies.Ok(), MovieTrackCount = (uint)S(r.RemoteInterfaceId).TrackCount });
        On<CS_RemoteInterfaceID>("GetNumberOfAudioAndVideoTracks", r => new SC_GetNumberOfAudioAndVideoTracks { Error = Replies.Ok(), MovieTrackCount = (uint)S(r.RemoteInterfaceId).TrackCount });
        On<CS_RemoteInterfaceID>("GetAudioTrackCount", r => new SC_GetAudioTrackCount { Error = Replies.Ok(), AudioTrackCount = (uint)S(r.RemoteInterfaceId).TrackCount });
        On<CS_DetermineMovieEditRateAndLength>("DetermineMovieEditRateAndLength", DetermineMovieEditRateAndLength);
        On<CS_QTWGetTrackID>("QTWGetTrackID", r => { S(r.RemoteInterfaceId).Track(r.TrackNum); return new SC_QTWGetTrackID { Error = Replies.Ok(), AcfResult = Replies.AcfOk, TrackID = r.TrackNum }; });
        On<CS_QTWGetTrackEnabled>("QTWGetTrackEnabled", r => new SC_QTWGetTrackEnabled { Error = Replies.Ok(), AcfResult = Replies.AcfOk, Enabled = true });
        On<CS_QTWGetTrackOffset>("QTWGetTrackOffset", r => new SC_QTWGetTrackOffset { Error = Replies.Ok(), AcfResult = Replies.AcfOk, Offset = 0 });
        On<CS_IsTrackAnAudioTrack>("IsTrackAnAudioTrack", r => new SC_IsTrackAnAudioTrack { Error = Replies.Ok(), TrackIsAudio = HasTrack(r.RemoteInterfaceId, r.Track) });
        On<CS_IsTrackAVisualTrack>("IsTrackAVisualTrack", _ => new SC_IsTrackAVisualTrack { Error = Replies.Ok(), TrackIsVisual = false });
        On<CS_IsTrackATimeCodeTrack>("IsTrackATimeCodeTrack", _ => new SC_IsTrackATimeCodeTrack { Error = Replies.Ok(), TrackIsTimecode = false });
        On<CS_GetAudioInfoForTrack>("GetAudioInfoForTrack", GetAudioInfoForTrack);
        // Pro Tools reads 0.4% past the reported duration of VBR tracks (captured with QuickTime, which
        // called everything VBR) and keeps that padding in the imported file. When the container gives
        // the exact sample count there is nothing to be unsure about, so only estimates are "VBR".
        On<CS_IsAudioTrackVBR>("IsAudioTrackVBR", r => new SC_IsAudioTrackVBR { Error = Replies.Ok(), AcfResult = Replies.AcfOk, IsVBR = !(_config.ExactLengths && S(r.RemoteInterfaceId).Track(r.TrackNum).ExactDuration) });
        On<CS_GetNumAudioChannels>("GetNumAudioChannels", r => new SC_GetNumAudioChannels { Error = Replies.Ok(), NumChannels = (uint)S(r.RemoteInterfaceId).Track(r.Track).Channels });
        On<CS_QTWGetMediaDuration>("QTWGetMediaDuration", r => new SC_QTWGetMediaDuration { Error = Replies.Ok(), Duration = Clamp32(S(r.RemoteInterfaceId).Track(r.Track).DurationSamples) });
        On<CS_QTWGetMediaTimeScale>("QTWGetMediaTimeScale", r => new SC_QTWGetMediaTimeScale { Error = Replies.Ok(), Scale = (uint)S(r.RemoteInterfaceId).Track(r.Track).SampleRate });
        On<CS_RemoteInterfaceID>("QTWGetMovieTimeScale", r => new SC_QTWGetMovieTimeScale { Error = Replies.Ok(), AcfResult = Replies.AcfOk, TimeScale = (uint)MovieTimeScale(S(r.RemoteInterfaceId)) });
        On<CS_RemoteInterfaceID>("QTWGetMovieDuration", r => new SC_QTWGetMovieDuration { Error = Replies.Ok(), MovieDuration = Clamp32(MovieDuration(S(r.RemoteInterfaceId))) });
        On<CS_QTWrGetTrackDuration>("QTWrGetTrackDuration", r => new SC_QTWrGetTrackDuration { Error = Replies.Ok(), Duration = Clamp32(TrackDurationInMovieScale(S(r.RemoteInterfaceId), r.Track)) });
        On<CS_QTWGetTrackDuration2>("QTWGetTrackDuration2", r => new SC_QTWGetTrackDuration2 { Error = Replies.Ok(), AcfResult = Replies.AcfOk, Duration = (int)Math.Min(int.MaxValue, TrackDurationInMovieScale(S(r.RemoteInterfaceId), r.TrackNum)) });
        // Non-VBR tracks: Pro Tools asks for the length in samples instead of padding a VBR estimate.
        // QuickTime media sample numbers are 1-based and each audio sample is one frame long.
        On<CS_QTWGetMediaSampleCount>("QTWGetMediaSampleCount", r => new SC_QTWGetMediaSampleCount { Error = Replies.Ok(), MedDurInSamples = (int)Math.Min(int.MaxValue, S(r.RemoteInterfaceId).Track(r.Track).DurationSamples) });
        On<CS_QTWGetMediaSampleDescriptionCount>("QTWGetMediaSampleDescriptionCount", r => { S(r.RemoteInterfaceId).Track(r.Track); return new SC_QTWGetMediaSampleDescriptionCount { Error = Replies.Ok(), SampleDescriptionCount = 1 }; });
        On<CS_QTWSampleNumToMediaDecodeTime>("QTWSampleNumToMediaDecodeTime", r => new SC_QTWSampleNumToMediaDecodeTime { Error = Replies.Ok(), AcfResult = Replies.AcfOk, SampleDecodeTime = Math.Max(0, r.LogicalSampleNum - 1), SampleDecodeDuration = 1 });
        On<CS_GetDecodeDuration>("GetDecodeDuration", _ => new SC_GetDecodeDuration { Error = Replies.Ok(), DecodeDuration = 1 });
        On<CS_QTWTrackTimeToMediaTime>("QTWTrackTimeToMediaTime", r =>
        {
            var s = S(r.RemoteInterfaceId);
            long media = (long)Math.Round((double)r.TrackTime * s.Track(r.TrackNum).SampleRate / MovieTimeScale(s));
            return new SC_QTWTrackTimeToMediaTime { Error = Replies.Ok(), AcfResult = Replies.AcfOk, MediaTime = (int)Math.Min(int.MaxValue, media) };
        });
        On<CS_QTWGetDuration>("QTWGetDuration", r => new SC_QTWGetDuration { Error = Replies.Ok(), AcfResult = Replies.AcfOk, Duration = (int)Math.Min(int.MaxValue, TrackDurationInMovieScale(S(r.RemoteInterfaceId), r.TrackId)) });
        On<CS_QTWGetTrackEditRate>("QTWGetTrackEditRate", _ => new SC_QTWGetTrackEditRate { Error = Replies.Ok(), AcfResult = Replies.AcfOk, EditRate = 0x10000 }); // Fixed 1.0: no speed changes
        On<CS_MovieTrackDurationIssues>("MovieTrackDurationIssues", _ => new SC_MovieTrackDurationIssues { Error = Replies.Ok() });
        On<CS_RemoteInterfaceID>("IsMovieSelfContained", _ => new SC_IsMovieSelfContained { Error = Replies.Ok(), IsMovieSelfContained = 1, AllResolved = true });

        // Pulling audio
        On<CS_GetQTAudioReader>("GetQTAudioReader", r => { S(r.RemoteInterfaceId); return new SC_Error { Error = Replies.Ok(), AcfResult = Replies.AcfOk }; });
        On<CS_SetParamsForProToolsImport>("SetParamsForProToolsImport", r =>
        {
            var (channels, acf) = SetFormat(r.RemoteInterfaceId, (int)r.Track, r.BytesPerSample, r.IsLittleEndian, r.SampleRate);
            return new SC_SetParamsForProToolsImport { Error = Replies.Ok(), NumChannels = channels, AcfResult = acf };
        });
        On<CS_SetAudioReaderParams>("SetAudioReaderParams", r =>
        {
            var (channels, acf) = SetFormat(r.RemoteInterfaceId, (int)r.Track, r.BytesPerSample, r.IsLittleEndian, r.SampleRate);
            return new SC_SetAudioReaderParams { Error = Replies.Ok(), NumChannels = channels, AcfResult = acf };
        });
        On<CS_SetAudioTrackPropertiesForImport>("SetAudioTrackPropertiesForImport", r =>
            new SC_SetAudioTrackPropertiesForImport { Error = Replies.Ok(), InStereoMode = S(r.RemoteInterfaceId).Track(r.Track).Channels == 2, Acfresult = Replies.AcfOk });
        On<CS_ExtractAudio>("ExtractAudio", ExtractAudio);
        On<CS_CachedExtractAudio>("CachedExtractAudio", CachedExtractAudio);
    }

    // ---- dispatch -------------------------------------------------------------------------

    void On<T>(string method, Func<T, IMessage> handler) where T : IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());
        _handlers[Djb2.Hash(method)] = payload => handler(parser.ParseFrom(payload.Span));
    }

    void OnSuccess(params string[] methods)
    {
        foreach (var m in methods)
        {
            uint tag = Djb2.Hash(m);
            _handlers[tag] = _ => Replies.Success(tag);
        }
    }

    void OneWay(string method)
    {
        uint tag = Djb2.Hash(method);
        _oneWay.Add(tag);
        _handlers[tag] = _ => new SC_Error();
    }

    /// <summary>Handles one request; returns the reply frame, or null for one-way messages.</summary>
    public DipcFrame? Handle(DipcFrame request)
    {
        string name = Replies.NameOf(request.Tag);
        IMessage reply;
        if (_handlers.TryGetValue(request.Tag, out var handler))
        {
            try
            {
                reply = handler(request.Payload);
                if (_oneWay.Contains(request.Tag))
                    return null;
                Log.Verbose($"#{request.RequestId} {name} -> {reply}");
            }
            catch (Exception e)
            {
                Log.Warn($"#{request.RequestId} {name} failed: {e.Message}");
                reply = Replies.NotImplemented(request.Tag, e.Message);
            }
        }
        else
        {
            Log.Warn($"#{request.RequestId} unhandled method {name} ({request.Payload.Length} byte payload)");
            reply = Replies.NotImplemented(request.Tag, $"{name} is not supported by pt-ffmpeg-bridge");
        }
        return request.WantsReply ? DipcFrame.Reply(request.RequestId, reply.ToByteArray()) : null;
    }

    // ---- sessions ---------------------------------------------------------------------------

    long NewSession()
    {
        long id = InterfaceIdBase + _nextInterface++;
        _sessions[id] = new MovieSession(id);
        return id;
    }

    void EndSession(long id)
    {
        if (_sessions.Remove(id, out var s))
            s.Dispose();
    }

    MovieSession S(long id) =>
        _sessions.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"unknown interface {id:x}");

    bool HasTrack(long iface, long track) => track >= 1 && track <= S(iface).TrackCount;

    // ---- handlers ---------------------------------------------------------------------------

    IMessage NewMovieFromFilePath(CS_NewMovieFromFilePath r)
    {
        var session = S(r.RemoteInterfaceId);
        string path = Replies.DecodeString(r.TheMoviePathUniCharPtr);
        try
        {
            var media = MediaProbe.Probe(_tools, path, ProbeTimeout);
            if (media.AudioStreams.Count == 0)
            {
                Log.Info($"open {path}: no audio streams");
                return new SC_NewMovieFromFilePath { Error = Replies.Ok(), AcfResult = Replies.AcfCannotOpen };
            }
            session.Open(media);
            Log.Info($"open {path}: " + string.Join("; ", media.AudioStreams.Select(a => $"{a.Codec} {a.Channels}ch {a.SampleRate} Hz {a.BitsPerSample}-bit {a.DurationSamples} samples")));
            return new SC_NewMovieFromFilePath { Error = Replies.Ok(), AcfResult = Replies.AcfOk };
        }
        catch (Exception e)
        {
            Log.Info($"open {path}: unreadable ({e.Message})");
            return new SC_NewMovieFromFilePath { Error = Replies.Ok(), AcfResult = Replies.AcfCannotOpen };
        }
    }

    IMessage DetermineMovieEditRateAndLength(CS_DetermineMovieEditRateAndLength r)
    {
        var s = S(r.RemoteInterfaceId);
        var (num, den) = EditRate(s.Media!.VideoFrameRate);
        var reply = new SC_DetermineMovieEditRateAndLength { Error = Replies.Ok(), MERNumerator = num, MERDenominator = den, AcfResult = Replies.AcfOk };
        // QuickTime answers with a leading 0 followed by one length per track (in edit-rate frames).
        reply.TrackLengthInFramesVector.Add(0);
        long longest = 0;
        foreach (var a in s.Media.AudioStreams)
        {
            long frames = (long)Math.Floor((double)a.DurationSamples / a.SampleRate * num / den);
            reply.TrackLengthInFramesVector.Add(frames);
            longest = Math.Max(longest, frames);
        }
        reply.MovieLength = longest;
        return reply;
    }

    /// <summary>Audio-only files use 50/1 like QuickTime did; files with video use the video rate.</summary>
    public static (int Num, int Den) EditRate(double videoRate)
    {
        if (videoRate <= 0 || double.IsNaN(videoRate))
            return (50, 1);
        double ntsc = videoRate * 1001 / 1000;
        if (Math.Abs(videoRate - Math.Round(videoRate)) > 0.001 && Math.Abs(ntsc - Math.Round(ntsc)) < 0.001)
            return ((int)Math.Round(ntsc) * 1000, 1001);
        return ((int)Math.Round(videoRate), 1);
    }

    IMessage GetAudioInfoForTrack(CS_GetAudioInfoForTrack r)
    {
        var a = S(r.RemoteInterfaceId).Track(r.Track);
        var (formatId, dataFormat) = FourCCs(a.Codec);
        int bits = ReportedBits(a);
        return new SC_GetAudioInfoForTrack
        {
            Error = Replies.Ok(),
            ChannelsPerFrame = (uint)a.Channels,
            SampleRate = a.SampleRate,
            FormatID = formatId,
            BytesPerFrame = a.IsPcm ? (uint)(a.Channels * bits / 8) : 0,
            BytesPerPacket = a.IsPcm ? (uint)(a.Channels * bits / 8) : 0,
            BitsPerChannel = a.IsPcm ? (uint)bits : 0,
            IsBigEndian = false,
            DataFormat = (int)dataFormat,
            SampleSize = bits,
            AcfResult = Replies.AcfOk,
        };
    }

    /// <summary>
    /// Pro Tools requests the sample size we report (verified: 24 → s24 extraction). Only genuinely
    /// 16-bit-or-less lossless sources report 16 (bit-exact); everything else, including lossy decoders
    /// whose output is floating point, reports 24 so nothing is truncated or dithered down to 16 bits.
    /// QuickTime reported 16 for lossy codecs, which capped AAC imports at 16-bit resolution.
    /// </summary>
    public static int ReportedBits(AudioStream a) =>
        a.IsLossless && a.BitsPerSample is > 0 and <= 16 ? 16 : 24;

    public static (uint FormatId, uint DataFormat) FourCCs(string codec)
    {
        string id = codec switch
        {
            "aac" => "aac ",
            "alac" => "alac",
            "mp3" or "mp3float" => ".mp3",
            "mp2" => ".mp2",
            "flac" => "fLaC",
            "opus" => "opus",
            "vorbis" => "vorb",
            "ac3" => "ac-3",
            "eac3" => "ec-3",
            "wmav1" or "wmav2" or "wmapro" or "wmalossless" => "wma ",
            _ when codec.StartsWith("pcm_") => "lpcm",
            _ => "ffmp",
        };
        uint formatId = FourCC(id);
        return (formatId, codec == "aac" ? FourCC("mp4a") : formatId);
    }

    static uint FourCC(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    (uint Channels, int Acf) SetFormat(long iface, int track, int bytesPerSample, bool littleEndian, double sampleRate)
    {
        var s = S(iface);
        var a = s.Track(track);
        if (!PcmFormat.IsSupported(bytesPerSample) || sampleRate < 1000 || sampleRate > 768000)
        {
            Log.Warn($"iface {iface:x}: unsupported output format {bytesPerSample} bytes @ {sampleRate} Hz");
            return ((uint)a.Channels, Replies.AcfFail);
        }
        s.SetFormat(track, new PcmFormat(bytesPerSample, littleEndian, (int)Math.Round(sampleRate), a.Channels));
        return ((uint)a.Channels, Replies.AcfOk);
    }

    IMessage ExtractAudio(CS_ExtractAudio r)
    {
        var s = S(r.RemoteInterfaceId);
        int track = (int)r.Track;
        var fmt = s.FormatFor(track);
        int outChannels = r.Stereo ? fmt.Channels : 1;
        int channel = (int)r.Channel;
        if (!r.Stereo && channel >= fmt.Channels)
            throw new ArgumentOutOfRangeException(nameof(r.Channel), $"channel {channel} of {fmt.Channels}");

        long frames = (long)r.SamplesToExtract;
        int outFrameBytes = fmt.BytesPerSample * outChannels;
        if (r.BufferSize > 0)
            frames = Math.Min(frames, r.BufferSize / outFrameBytes);
        if (frames * fmt.FrameBytes > int.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(r.SamplesToExtract), $"{frames} frames per call is too many");

        var interleaved = new byte[frames * fmt.FrameBytes];
        // Pro Tools ignores samples_extracted and always copies the whole requested buffer, so the
        // buffer must be complete: past the end of the audio it is zero-filled (QuickTime did the same).
        long real = s.CacheFor(track, _tools, _config.TempDir).Read((long)r.StartSample, interleaved, DecodeTimeout);
        if (real < frames)
            Log.Verbose($"iface {s.Id:x}: frames {(long)r.StartSample + real}..{(long)r.StartSample + frames} are past the end (silence)");
        byte[] output = interleaved;
        if (!r.Stereo && fmt.Channels > 1)
        {
            output = new byte[frames * fmt.BytesPerSample];
            ChannelSelect.Extract(interleaved, output, fmt.Channels, fmt.BytesPerSample, channel);
        }

        _shm ??= new SharedMemoryPool();
        var desc = _shm.Write(output);
        Log.Verbose($"extract track {track} [{r.StartSample}+{frames}] stereo={r.Stereo} ch={channel} -> {output.Length} bytes @ {desc.Address}");
        return new SC_ExtractAudio { Error = Replies.Ok(), SamplesExtracted = (ulong)frames, ShMem = desc, Acfresult = Replies.AcfOk };
    }

    /// <summary>Block-addressed variant (never observed from Pro Tools 12.5): block N covers bytes [N*size, (N+1)*size).</summary>
    IMessage CachedExtractAudio(CS_CachedExtractAudio r)
    {
        var s = S(r.RemoteInterfaceId);
        int track = (int)r.Track;
        var fmt = s.FormatFor(track);
        long framesPerBlock = Math.Max(1, r.BufferSize / fmt.FrameBytes);
        var data = new byte[framesPerBlock * fmt.FrameBytes];
        s.CacheFor(track, _tools, _config.TempDir).Read(r.AudioBlockNo * framesPerBlock, data, DecodeTimeout);
        _shm ??= new SharedMemoryPool();
        Log.Info($"CachedExtractAudio track {track} block {r.AudioBlockNo} ({data.Length} bytes)");
        return new SC_CachedExtractAudio { Error = Replies.Ok(), ShMem = _shm.Write(data), BufferSize = (uint)data.Length, Acfresult = Replies.AcfOk };
    }

    // Movie-level time is expressed in the first track's sample rate.
    static int MovieTimeScale(MovieSession s) => s.TrackCount > 0 ? s.Track(1).SampleRate : 600;

    static long MovieDuration(MovieSession s)
    {
        int scale = MovieTimeScale(s);
        return s.Media!.AudioStreams.Select(a => (long)Math.Round((double)a.DurationSamples * scale / a.SampleRate)).DefaultIfEmpty(0).Max();
    }

    static long TrackDurationInMovieScale(MovieSession s, long track)
    {
        var a = s.Track(track);
        return (long)Math.Round((double)a.DurationSamples * MovieTimeScale(s) / a.SampleRate);
    }

    static uint Clamp32(long v) => (uint)Math.Clamp(v, 0, uint.MaxValue);

    public void Dispose()
    {
        foreach (var s in _sessions.Values)
            s.Dispose();
        _sessions.Clear();
        _shm?.Dispose();
    }
}
