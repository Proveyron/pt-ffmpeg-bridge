using Bridge.Decode;

namespace Bridge.Server;

/// <summary>
/// State behind one remote interface id: the opened file and, once Pro Tools starts pulling
/// audio, a decode cache. QuickTime "tracks" are 1-based and we expose only audio streams.
/// </summary>
public sealed class MovieSession : IDisposable
{
    readonly Dictionary<int, PcmFormat> _formats = new();
    PcmCache? _cache;
    (int Track, PcmFormat Format)? _cacheKey;

    public long Id { get; }
    public MediaInfo? Media { get; private set; }

    public MovieSession(long id) => Id = id;

    public void Open(MediaInfo media)
    {
        DisposeCache();
        _formats.Clear();
        Media = media;
    }

    public int TrackCount => Media?.AudioStreams.Count ?? 0;

    public AudioStream Track(long track)
    {
        if (Media == null)
            throw new InvalidOperationException("no movie open");
        if (track < 1 || track > Media.AudioStreams.Count)
            throw new ArgumentOutOfRangeException(nameof(track), $"track {track} of {Media.AudioStreams.Count}");
        return Media.AudioStreams[(int)track - 1];
    }

    /// <summary>Records the output format for a track. Called for every file clicked in the import
    /// browser, so this must stay cheap: decoding starts on the first extraction.</summary>
    public void SetFormat(int track, PcmFormat format) => _formats[track] = format;

    public PcmFormat FormatFor(int track) =>
        _formats.TryGetValue(track, out var f) ? f : throw new InvalidOperationException($"SetParams was not called for track {track}");

    public PcmCache CacheFor(int track, FfmpegTools tools, string tempDir)
    {
        var format = FormatFor(track);
        if (_cache != null && _cacheKey == (track, format))
            return _cache;
        DisposeCache();
        _cache = new PcmCache(tools, Media!.Path, Track(track), format, tempDir);
        _cacheKey = (track, format);
        Log.Info($"iface {Id:x}: decoding track {track} -> {format.FfmpegFormat} {format.SampleRate} Hz x{format.Channels}");
        return _cache;
    }

    void DisposeCache()
    {
        _cache?.Dispose();
        _cache = null;
        _cacheKey = null;
    }

    public void Dispose() => DisposeCache();
}
