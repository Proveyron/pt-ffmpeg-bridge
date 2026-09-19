namespace Bridge.Decode;

/// <summary>
/// Pro Tools names the channels it receives in film order (5.1 = L C R Ls Rs LFE; verified by
/// importing a 5.1 file with a distinct tone per channel). FFmpeg decodes in SMPTE/WAV order
/// (5.1 = FL FR FC LFE BL BR), so multichannel audio is reordered before it is handed over.
/// </summary>
public static class ProToolsChannelOrder
{
    /// <summary>
    /// For each output channel (Pro Tools order), the index of the FFmpeg input channel to use;
    /// null when the order already matches. Only unambiguous layouts are remapped.
    /// </summary>
    public static int[]? Map(string layout, int channels)
    {
        if (string.IsNullOrEmpty(layout))
            layout = channels switch { 6 => "5.1", 8 => "7.1", _ => "" }; // FFmpeg's defaults for untagged audio
        return (layout, channels) switch
        {
            ("3.0", 3) => [0, 2, 1],                             // L C R
            ("5.0" or "5.0(side)", 5) => [0, 2, 1, 3, 4],        // L C R Ls Rs
            ("5.1" or "5.1(side)", 6) => [0, 2, 1, 4, 5, 3],     // L C R Ls Rs LFE
            ("7.1", 8) => [0, 2, 1, 6, 7, 4, 5, 3],              // L C R Lss Rss Lsr Rsr LFE
            _ => null,
        };
    }

    /// <summary>FFmpeg channelmap filter for the reorder, or null when none is needed.</summary>
    public static string? Filter(string layout, int channels) =>
        Map(layout, channels) is { } map ? "channelmap=map=" + string.Join("|", map) : null;
}
