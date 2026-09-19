using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Bridge.Decode;

/// <summary>One audio stream as Pro Tools will see it (a QuickTime "track").</summary>
public sealed record AudioStream(
    int StreamIndex,        // index among audio streams (ffmpeg -map 0:a:N)
    string Codec,
    int Channels,
    int SampleRate,
    int BitsPerSample,      // source precision if known, else 0
    long DurationSamples,   // at SampleRate
    bool IsPcm,
    bool IsLossless,
    string ChannelLayout = "",
    bool ExactDuration = false); // container states the length in samples (not estimated from bitrate)

public sealed record MediaInfo(string Path, IReadOnlyList<AudioStream> AudioStreams, double DurationSeconds, double VideoFrameRate);

public static class MediaProbe
{
    static readonly HashSet<string> Lossless = ["flac", "alac", "wavpack", "ape", "tta", "mlp", "truehd", "tak", "shorten", "als", "dsd_lsbf", "dsd_msbf", "dsd_lsbf_planar", "dsd_msbf_planar"];

    public static MediaInfo Probe(FfmpegTools tools, string path, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(tools.Ffprobe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            p.Kill(true);
            throw new TimeoutException($"ffprobe timed out on {path}");
        }
        if (p.ExitCode != 0)
            throw new InvalidDataException($"ffprobe failed ({p.ExitCode}): {stderr.Result.Trim()}");
        return Parse(path, stdout.Result);
    }

    public static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double formatDuration = root.TryGetProperty("format", out var fmt) ? Num(fmt, "duration") : 0;
        var audio = new List<AudioStream>();
        double videoRate = 0;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = Str(s, "codec_type");
                if (type == "video" && videoRate == 0 && !IsAttachedPicture(s))
                    videoRate = Rational(Str(s, "avg_frame_rate")) is > 0 and var r ? r : Rational(Str(s, "r_frame_rate"));
                if (type != "audio")
                    continue;
                string codec = Str(s, "codec_name");
                int rate = (int)Num(s, "sample_rate");
                int channels = (int)Num(s, "channels");
                if (rate <= 0 || channels <= 0)
                    continue;
                int bits = (int)Num(s, "bits_per_raw_sample");
                if (bits == 0)
                    bits = (int)Num(s, "bits_per_sample");
                audio.Add(new AudioStream(audio.Count, codec, channels, rate, bits,
                    DurationSamples(s, rate, formatDuration), codec.StartsWith("pcm_"), codec.StartsWith("pcm_") || Lossless.Contains(codec),
                    Str(s, "channel_layout"), IsExact(s, rate)));
            }
        }
        return new MediaInfo(path, audio, formatDuration, videoRate);
    }

    static long DurationSamples(JsonElement s, int rate, double formatDuration)
    {
        // duration_ts in the stream time base is exact for FLAC/WAV/ALAC; fall back to seconds.
        long ts = (long)Num(s, "duration_ts");
        double tb = Rational(Str(s, "time_base"));
        if (ts > 0 && tb > 0)
            return (long)Math.Round(ts * tb * rate);
        double seconds = Num(s, "duration");
        if (seconds <= 0)
            seconds = formatDuration;
        return (long)Math.Round(seconds * rate);
    }

    /// <summary>
    /// A duration counted in a 1/sample-rate time base comes from the container's sample tables
    /// (MP4/MOV, FLAC, Ogg, WavPack, WAV…). Raw ADTS or header-less MP3 use other time bases and
    /// their duration is only estimated from the bitrate.
    /// </summary>
    static bool IsExact(JsonElement s, int rate) =>
        (long)Num(s, "duration_ts") > 0 && Math.Abs(Rational(Str(s, "time_base")) * rate - 1) < 1e-9;

    static bool IsAttachedPicture(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    static double Rational(string r)
    {
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
            return n / d;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }
}
