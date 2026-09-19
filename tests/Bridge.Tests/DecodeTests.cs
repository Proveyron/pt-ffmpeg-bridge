using Bridge.Decode;
using Bridge.Server;

namespace Bridge.Tests;

public class DecodeTests
{
    const string FlacProbe = """
        {"streams":[{"index":0,"codec_name":"flac","codec_type":"audio","sample_fmt":"s32","sample_rate":"96000","channels":1,
          "bits_per_raw_sample":"24","time_base":"1/96000","duration_ts":192000,"duration":"2.000000"}],
         "format":{"duration":"2.000000"}}
        """;

    const string MovProbe = """
        {"streams":[
          {"index":0,"codec_name":"mpeg4","codec_type":"video","avg_frame_rate":"30000/1001","r_frame_rate":"30000/1001"},
          {"index":1,"codec_name":"aac","codec_type":"audio","sample_rate":"44100","channels":2,"time_base":"1/44100","duration_ts":133120},
          {"index":2,"codec_name":"mjpeg","codec_type":"video","avg_frame_rate":"0/0","disposition":{"attached_pic":1}}],
         "format":{"duration":"3.018"}}
        """;

    [Fact]
    public void Probe_parses_lossless_stream()
    {
        var info = MediaProbe.Parse("x.flac", FlacProbe);
        var a = Assert.Single(info.AudioStreams);
        Assert.Equal(("flac", 1, 96000, 24, 192000L), (a.Codec, a.Channels, a.SampleRate, a.BitsPerSample, a.DurationSamples));
        Assert.True(a.IsLossless);
        Assert.False(a.IsPcm);
        Assert.True(a.ExactDuration);
        Assert.Equal(0, info.VideoFrameRate);
    }

    [Fact]
    public void Bitrate_estimated_durations_are_not_exact()
    {
        // ffprobe output for a raw ADTS .aac: odd time base, duration estimated from bitrate.
        const string adts = """
            {"streams":[{"codec_name":"aac","codec_type":"audio","sample_rate":"44100","channels":2,
              "time_base":"1/28224000","duration_ts":86931418,"duration":"3.080053"}],"format":{"duration":"3.080053"}}
            """;
        var a = MediaProbe.Parse("x.aac", adts).AudioStreams[0];
        Assert.False(a.ExactDuration);
        Assert.Equal(135830, a.DurationSamples);
    }

    [Fact]
    public void Probe_uses_video_rate_and_skips_cover_art()
    {
        var info = MediaProbe.Parse("x.mov", MovProbe);
        Assert.Equal(30000.0 / 1001, info.VideoFrameRate, 6);
        var a = Assert.Single(info.AudioStreams);
        Assert.Equal(0, a.StreamIndex);
        Assert.Equal(133120, a.DurationSamples);
    }

    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(30000.0 / 1001, 30000, 1001)]
    [InlineData(24000.0 / 1001, 24000, 1001)]
    [InlineData(60, 60, 1)]
    public void Edit_rate_matches_quicktime_convention(double video, int num, int den) =>
        Assert.Equal((num, den), QtServer.EditRate(video));

    [Fact]
    public void FourCCs_match_what_quicktime_reported_for_aac_and_alac()
    {
        Assert.Equal((1633772320u, 1836069985u), QtServer.FourCCs("aac"));   // 'aac ', 'mp4a'
        Assert.Equal(1634492771u, QtServer.FourCCs("alac").FormatId);         // 'alac'
    }

    [Fact]
    public void Reports_16_bits_only_for_16_bit_lossless()
    {
        var flac24 = MediaProbe.Parse("x.flac", FlacProbe).AudioStreams[0];
        Assert.Equal(24, QtServer.ReportedBits(flac24));
        Assert.Equal(16, QtServer.ReportedBits(flac24 with { BitsPerSample = 16 }));
        Assert.Equal(24, QtServer.ReportedBits(flac24 with { Codec = "aac", IsLossless = false, BitsPerSample = 0 }));
        Assert.Equal(24, QtServer.ReportedBits(flac24 with { BitsPerSample = 0 })); // unknown depth: don't truncate
    }

    [Fact]
    public void Channel_select_pulls_one_channel()
    {
        // 3 frames, 2 channels, 16-bit: L = 01 02 / 05 06 / 09 0a, R = 03 04 / 07 08 / 0b 0c
        byte[] interleaved = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var right = new byte[6];
        ChannelSelect.Extract(interleaved, right, channels: 2, bytesPerSample: 2, channel: 1);
        Assert.Equal(new byte[] { 3, 4, 7, 8, 11, 12 }, right);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelSelect.Extract(interleaved, right, 2, 2, 2));
    }

    [Fact]
    public void Ffmpeg_arguments_resample_then_dither_in_one_filter()
    {
        var stream = MediaProbe.Parse("x.flac", FlacProbe).AudioStreams[0];
        var args = PcmCache.BuildArguments("x.flac", stream, new PcmFormat(2, false, 48000, 1));
        int af = args.ToList().IndexOf("-af");
        Assert.Equal("aresample=osr=48000:resampler=soxr:precision=28:osf=s16:dither_method=triangular", args[af + 1]);
        Assert.Contains("s16be", args);
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void Ffmpeg_arguments_are_bit_exact_for_24_bit_at_native_rate()
    {
        var stream = MediaProbe.Parse("x.flac", FlacProbe).AudioStreams[0];
        var args = PcmCache.BuildArguments("x.flac", stream, new PcmFormat(3, true, 96000, 1));
        Assert.DoesNotContain("-af", args);
        Assert.Contains("pcm_s24le", args);
    }

    [Theory]
    [InlineData(2, false, "s16be")]
    [InlineData(2, true, "s16le")]
    [InlineData(3, false, "s24be")]
    [InlineData(4, true, "s32le")]
    [InlineData(1, true, "u8")]
    public void Pcm_format_names(int bytes, bool le, string name) =>
        Assert.Equal(name, new PcmFormat(bytes, le, 44100, 2).FfmpegFormat);
}

public class ChannelOrderTests
{
    [Theory]
    [InlineData("5.1", 6, new[] { 0, 2, 1, 4, 5, 3 })]
    [InlineData("5.1(side)", 6, new[] { 0, 2, 1, 4, 5, 3 })]
    [InlineData("", 6, new[] { 0, 2, 1, 4, 5, 3 })]
    [InlineData("7.1", 8, new[] { 0, 2, 1, 6, 7, 4, 5, 3 })]
    [InlineData("5.0", 5, new[] { 0, 2, 1, 3, 4 })]
    public void Reorders_to_pro_tools_film_order(string layout, int channels, int[] expected) =>
        Assert.Equal(expected, Bridge.Decode.ProToolsChannelOrder.Map(layout, channels));

    [Theory]
    [InlineData("stereo", 2)]
    [InlineData("mono", 1)]
    [InlineData("quad", 4)]
    [InlineData("2.1", 3)]
    [InlineData("", 3)]
    public void Leaves_unambiguous_layouts_alone(string layout, int channels) =>
        Assert.Null(Bridge.Decode.ProToolsChannelOrder.Map(layout, channels));

    [Fact]
    public void Surround_decode_reorders_before_resampling()
    {
        var stream = new Bridge.Decode.AudioStream(0, "flac", 6, 48000, 16, 96000, false, true, "5.1");
        var args = Bridge.Decode.PcmCache.BuildArguments("x.flac", stream, new Bridge.Decode.PcmFormat(3, false, 44100, 6));
        Assert.Equal("channelmap=map=0|2|1|4|5|3,aresample=osr=44100:resampler=soxr:precision=28", args[args.ToList().IndexOf("-af") + 1]);
        Assert.DoesNotContain("-ac", args);
    }
}
