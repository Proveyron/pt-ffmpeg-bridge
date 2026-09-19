using System.Text.Json;

namespace Bridge.Server;

/// <summary>Optional bridge.json next to the exe. Every setting has a working default.</summary>
public sealed class BridgeConfig
{
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public string TempDir { get; set; } = Path.Combine(Path.GetTempPath(), "pt-ffmpeg-bridge");
    public string LogDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pt-ffmpeg-bridge", "logs");
    public bool DebugLog { get; set; }

    /// <summary>
    /// Report tracks whose container gives an exact sample count as non-VBR, so Pro Tools imports them
    /// at their true length. False = call everything VBR like QuickTime did (Pro Tools then appends
    /// ~0.4% of silence to every import).
    /// </summary>
    public bool ExactLengths { get; set; } = true;

    /// <summary>QuickTime version we claim to Pro Tools (Gestalt format; 0x07798000 = 7.7.9 release).</summary>
    public int ReportedQuickTimeVersion { get; set; } = 0x07798000;

    public static BridgeConfig Load(string path)
    {
        if (!File.Exists(path))
            return new BridgeConfig();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), options) ?? new BridgeConfig();
    }
}
