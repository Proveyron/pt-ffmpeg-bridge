using Bridge.Protocol;
using Bridge.Protocol.Messages;
using Google.Protobuf;

namespace Bridge.Tests;

public class ProtocolTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Theory]
    [InlineData("NewMovieFromFilePath", 0xf2cff4d0u)]
    [InlineData("ExtractAudio", 0x1f0ba4d2u)]
    [InlineData("GenericFutureCompletion", 0xe1009bf7u)]
    [InlineData("SH_RemoteFreeMem", 0xfd874c6cu)]
    [InlineData("CreateQuickTimeWrapperInterface", 0xc0930b97u)]
    public void Djb2_matches_tags_captured_from_pro_tools(string method, uint tag) =>
        Assert.Equal(tag, Djb2.Hash(method));

    [Fact]
    public void Parses_captured_ExtractAudio_request()
    {
        // Pro Tools 12.5 -> QuickTime server, first ExtractAudio of t01_aac.m4a
        var frame = DipcFrame.Parse(Hex("d2a40b1ffc43398a b6000000 00000001 01000000 19000000 08a680808080808080041001180020807e2880f80330013800"));
        Assert.Equal(Djb2.Hash("ExtractAudio"), frame.Tag);
        Assert.Equal(0xb6u, frame.RequestId);
        Assert.True(frame.WantsReply);
        var req = CS_ExtractAudio.Parser.ParseFrom(frame.Payload.Span);
        Assert.Equal(0x0200000000000013L, req.RemoteInterfaceId);
        Assert.Equal(1u, req.Track);
        Assert.Equal(0ul, req.StartSample);
        Assert.Equal(16128ul, req.SamplesToExtract);
        Assert.Equal(64512u, req.BufferSize);
        Assert.True(req.Stereo);
    }

    [Fact]
    public void Parses_parameterless_20_byte_frame()
    {
        var frame = DipcFrame.Parse(Hex("970b93c0 60e7368a a9000000 00000001 87cc4476"));
        Assert.Equal(Djb2.Hash("CreateQuickTimeWrapperInterface"), frame.Tag);
        Assert.Equal(0xa9u, frame.RequestId);
        Assert.True(frame.Payload.IsEmpty);
    }

    [Fact]
    public void Reply_serializes_like_quicktime_server()
    {
        // Captured SC_ExtractAudio reply body (after the 24-byte header).
        var captured = Hex("0a0c08001208080010001801220010807e1a0d08d89495e607100018808080402000");
        var reply = new SC_ExtractAudio
        {
            Error = Replies.Ok(),
            SamplesExtracted = 16128,
            ShMem = new MemoryDescriptor { Id = 0x7CC54A58, Address = 0, Size = 128 * 1024 * 1024 },
            Acfresult = 0,
        };
        Assert.Equal(captured, reply.ToByteArray());

        var frame = DipcFrame.Reply(0xb6, reply.ToByteArray()).Serialize();
        Assert.Equal(Hex("f79b00e1"), frame[..4]);
        Assert.Equal(Hex("b6000000 00000001 01000000 22000000"), frame[8..24]);
    }

    [Fact]
    public void Frame_round_trips()
    {
        var original = new DipcFrame(0x12345678, 42, 7, DipcFrame.FlagWantsReply, new byte[] { 1, 2, 3 });
        var parsed = DipcFrame.Parse(original.Serialize());
        Assert.Equal(original.Tag, parsed.Tag);
        Assert.Equal(original.RequestId, parsed.RequestId);
        Assert.Equal(original.Payload.ToArray(), parsed.Payload.ToArray());
    }

    [Fact]
    public void Rejects_truncated_frames()
    {
        Assert.Throws<InvalidDataException>(() => DipcFrame.Parse(new byte[12]));
        Assert.Throws<InvalidDataException>(() => DipcFrame.Parse(Hex("00000000 00000000 01000000 00000001 01000000 ff000000 00")));
    }

    [Fact]
    public void Decodes_utf16_paths()
    {
        var path = @"D:\Sessões\Violino ção.flac"; // non-ASCII on purpose: paths arrive as UTF-16
        var bytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        var sc = new StringContainer { Type = 2, NumCodeunits = (uint)path.Length + 1, StringSize = (uint)bytes.Length, Msg = ByteString.CopyFrom(bytes) };
        Assert.Equal(path, Replies.DecodeString(sc));
    }

    [Theory]
    [InlineData("ExtractAudio")]
    [InlineData("GetFrame")]
    [InlineData("QTWGetMovieBox")]
    [InlineData("SomethingThatDoesNotExist")]
    public void Not_implemented_replies_are_complete_messages(string method)
    {
        var reply = Replies.NotImplemented(Djb2.Hash(method), "nope");
        Assert.True(reply is IMessage);
        // proto2 required fields: serializing and parsing back must succeed.
        var bytes = reply.ToByteArray();
        var roundTrip = reply.Descriptor.Parser.ParseFrom(bytes);
        Assert.Equal(reply, roundTrip);
    }

    [Fact]
    public void Success_reply_for_quicktime_info_style_methods()
    {
        var reply = (SC_InitQT)Replies.Success(Djb2.Hash("InitQT"));
        Assert.Equal((int)QTErrorCodes.QtNoError, reply.Error.ErrorType);
    }

    [Fact]
    public void Shared_memory_pool_hands_out_distinct_readable_blocks()
    {
        using var pool = new SharedMemoryPool(1 << 20);
        var a = pool.Write(new byte[] { 1, 2, 3 });
        var b = pool.Write(new byte[] { 4, 5 });
        Assert.Equal(pool.Id, a.Id);
        Assert.NotEqual(a.Address, b.Address);
        using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting($"Local\\DIPC_Shm_{pool.Id:X8}");
        using var view = mmf.CreateViewAccessor();
        Assert.Equal(4, view.ReadByte(b.Address));
        Assert.Equal(3, view.ReadByte(a.Address + 2));
    }

    [Fact]
    public void Shared_memory_pool_wraps_when_full()
    {
        using var pool = new SharedMemoryPool(1024);
        pool.Write(new byte[700]);
        var wrapped = pool.Write(new byte[700]);
        Assert.Equal(0u, wrapped.Address);
    }
}
