namespace Bridge.Decode;

public static class ChannelSelect
{
    /// <summary>Copies one channel out of interleaved PCM (used when ExtractAudio has stereo=false).</summary>
    public static void Extract(ReadOnlySpan<byte> interleaved, Span<byte> mono, int channels, int bytesPerSample, int channel)
    {
        if ((uint)channel >= (uint)channels)
            throw new ArgumentOutOfRangeException(nameof(channel), $"channel {channel} of {channels}");
        int frameBytes = channels * bytesPerSample;
        int frames = interleaved.Length / frameBytes;
        if (mono.Length < frames * bytesPerSample)
            throw new ArgumentException("destination too small", nameof(mono));
        for (int f = 0; f < frames; f++)
            interleaved.Slice(f * frameBytes + channel * bytesPerSample, bytesPerSample).CopyTo(mono.Slice(f * bytesPerSample, bytesPerSample));
    }
}
