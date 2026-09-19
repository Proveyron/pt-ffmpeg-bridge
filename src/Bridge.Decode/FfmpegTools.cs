namespace Bridge.Decode;

/// <summary>Locations of ffmpeg.exe / ffprobe.exe. They run as separate processes, never linked in.</summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    /// <summary>Explicit paths win; otherwise look next to our exe, then on PATH.</summary>
    public static FfmpegTools Locate(string? ffmpeg = null, string? ffprobe = null) =>
        new(Resolve(ffmpeg, "ffmpeg.exe"), Resolve(ffprobe, "ffprobe.exe"));

    static string Resolve(string? configured, string exe)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return configured;
            throw new FileNotFoundException($"configured {exe} not found", configured);
        }
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local))
            return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            var candidate = Path.Combine(dir.Trim('"'), exe);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"{exe} not found next to the server or on PATH; set it in bridge.json");
    }
}
