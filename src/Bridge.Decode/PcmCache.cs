using System.Diagnostics;
using System.Globalization;

namespace Bridge.Decode;

/// <summary>
/// Decodes one audio stream with ffmpeg into a temp file of interleaved PCM in the requested
/// format, so ExtractAudio can serve any sample range. Decoding runs in the background; reads
/// block only until the bytes they need exist. The part of a read past the end of the audio is
/// zero-filled and the read reports how many real frames it returned.
/// </summary>
public sealed class PcmCache : IDisposable
{
    readonly Process _ffmpeg;
    readonly FileStream _file;
    readonly Task _pump;
    readonly object _lock = new();
    long _written;
    bool _done;
    Exception? _failure;
    readonly System.Text.StringBuilder _stderr = new();

    public PcmFormat Format { get; }
    public string TempPath { get; }

    /// <summary>Frames decoded so far (final count once <see cref="IsComplete"/>).</summary>
    public long FramesAvailable { get { lock (_lock) return _written / Format.FrameBytes; } }
    public bool IsComplete { get { lock (_lock) return _done; } }

    public PcmCache(FfmpegTools tools, string mediaPath, AudioStream stream, PcmFormat format, string tempDir)
    {
        Format = format;
        Directory.CreateDirectory(tempDir);
        TempPath = Path.Combine(tempDir, $"{Environment.ProcessId}-{Guid.NewGuid():N}.pcm");
        _file = new FileStream(TempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.DeleteOnClose);

        var psi = new ProcessStartInfo(tools.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArguments(mediaPath, stream, format))
            psi.ArgumentList.Add(a);
        _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        _ffmpeg.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) { if (_stderr.Length < 8192) _stderr.AppendLine(e.Data); } };
        _ffmpeg.BeginErrorReadLine();
        _pump = Task.Run(Pump);
    }

    public static IReadOnlyList<string> BuildArguments(string mediaPath, AudioStream stream, PcmFormat format)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-i", mediaPath, "-map", $"0:a:{stream.StreamIndex}", "-vn", "-sn", "-dn" };
        var filters = new List<string>();
        if (ProToolsChannelOrder.Filter(stream.ChannelLayout, stream.Channels) is { } reorder)
            filters.Add(reorder);
        // One aresample does both jobs, in the right order: resample at full precision with SoX,
        // then (only when reducing to 16/8 bits) convert with TPDF dither instead of truncating.
        // Separate filters would let ffmpeg auto-insert its default resampler after the dither.
        var options = new List<string>();
        if (format.SampleRate != stream.SampleRate)
            options.AddRange([$"osr={format.SampleRate.ToString(CultureInfo.InvariantCulture)}", "resampler=soxr", "precision=28"]);
        bool reducing = format.BytesPerSample <= 2 && (stream.BitsPerSample == 0 || stream.BitsPerSample > format.BytesPerSample * 8 || !stream.IsPcm && !stream.IsLossless);
        if (reducing)
            options.AddRange([format.BytesPerSample == 1 ? "osf=u8" : "osf=s16", "dither_method=triangular"]);
        if (options.Count > 0)
            filters.Add("aresample=" + string.Join(":", options));
        if (filters.Count > 0)
            args.AddRange(["-af", string.Join(",", filters)]);
        // No -ac: the channel count never changes, and forcing it could make ffmpeg remix a layout it
        // considers different (e.g. the relabelled output of channelmap).
        args.AddRange(["-ar", format.SampleRate.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-c:a", format.FfmpegCodec, "-f", format.FfmpegFormat, "pipe:1"]);
        return args;
    }

    void Pump()
    {
        var buf = new byte[1 << 16];
        try
        {
            var src = _ffmpeg.StandardOutput.BaseStream;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                lock (_lock)
                {
                    _file.Position = _written;
                    _file.Write(buf, 0, n);
                    _file.Flush();
                    _written += n;
                    Monitor.PulseAll(_lock);
                }
            }
            _ffmpeg.WaitForExit();
            if (_ffmpeg.ExitCode != 0)
                lock (_stderr) _failure = new InvalidDataException($"ffmpeg exited {_ffmpeg.ExitCode}: {_stderr.ToString().Trim()}");
        }
        catch (Exception e)
        {
            _failure = e;
        }
        finally
        {
            lock (_lock)
            {
                _done = true;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="dest"/> (frames * FrameBytes) starting at <paramref name="startFrame"/>
    /// and returns how many of those frames are real audio; the rest (past the end) is zeroed.
    /// Throws if ffmpeg failed before producing anything.
    /// </summary>
    public long Read(long startFrame, Span<byte> dest, TimeSpan timeout)
    {
        long start = startFrame * Format.FrameBytes;
        long end = start + dest.Length;
        var deadline = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (_written < end && !_done)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                    throw new TimeoutException($"decode stalled at {_written} bytes, needed {end}");
                Monitor.Wait(_lock, left);
            }
            // ffmpeg failed before producing anything: surface the error instead of returning silence.
            if (_failure != null && _written == 0)
                throw _failure;
            dest.Clear();
            long available = Math.Min(end, _written) - start;
            if (available <= 0)
                return 0;
            _file.Position = start;
            _file.ReadExactly(dest[..(int)available]);
            return available / Format.FrameBytes;
        }
    }

    public string? Error { get { lock (_lock) return _failure?.Message; } }

    public void Dispose()
    {
        try
        {
            if (!_ffmpeg.HasExited)
                _ffmpeg.Kill(true);
        }
        catch (InvalidOperationException) { }
        _pump.Wait(TimeSpan.FromSeconds(5));
        _ffmpeg.Dispose();
        _file.Dispose();
    }
}
