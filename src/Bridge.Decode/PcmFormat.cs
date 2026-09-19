namespace Bridge.Decode;

/// <summary>Integer PCM layout Pro Tools asks for in SetParamsForProToolsImport.</summary>
public sealed record PcmFormat(int BytesPerSample, bool LittleEndian, int SampleRate, int Channels)
{
    public int FrameBytes => BytesPerSample * Channels;

    /// <summary>ffmpeg raw muxer/codec name, e.g. s16be.</summary>
    public string FfmpegFormat => BytesPerSample switch
    {
        1 => "u8",
        2 => LittleEndian ? "s16le" : "s16be",
        3 => LittleEndian ? "s24le" : "s24be",
        4 => LittleEndian ? "s32le" : "s32be",
        _ => throw new NotSupportedException($"{BytesPerSample} bytes per sample"),
    };

    public string FfmpegCodec => "pcm_" + FfmpegFormat;

    public static bool IsSupported(int bytesPerSample) => bytesPerSample is >= 1 and <= 4;
}
