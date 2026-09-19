using System.Buffers.Binary;

namespace Bridge.Protocol;

/// <summary>
/// One DIPC message as it travels over a message-mode named pipe.
///
/// Layout (little-endian u32s):
///   0  tag        djb2(method name); replies use djb2("GenericFutureCompletion")
///   4  stamp      sender timestamp (microsecond tick, informational)
///   8  requestId  echoed by the reply; 0 for one-way messages
///   12 flags      0x01000000 = sender expects a reply
///   16 partCount  always 1 when a payload follows
///   20 length     protobuf payload length
///   24 payload
/// Parameterless requests (e.g. CreateQuickTimeWrapperInterface) are 20 bytes: the part/length
/// words are replaced by a single constant word and there is no payload.
/// </summary>
public sealed record DipcFrame(uint Tag, uint Stamp, uint RequestId, uint Flags, ReadOnlyMemory<byte> Payload)
{
    public const uint FlagWantsReply = 0x01000000;
    public const int HeaderSize = 24;
    public const int ShortFrameSize = 20;
    public const uint NoPayloadMarker = 0x7644CC87;

    public static readonly uint ReplyTag = Djb2.Hash("GenericFutureCompletion");

    public bool WantsReply => (Flags & FlagWantsReply) != 0;

    public static DipcFrame Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < ShortFrameSize)
            throw new InvalidDataException($"DIPC frame too short ({data.Length} bytes)");
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (data.Length < HeaderSize)
            return new DipcFrame(tag, stamp, id, flags, ReadOnlyMemory<byte>.Empty);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (length < 0 || HeaderSize + length > data.Length)
            throw new InvalidDataException($"DIPC payload length {length} exceeds frame size {data.Length}");
        return new DipcFrame(tag, stamp, id, flags, data.Slice(HeaderSize, length).ToArray());
    }

    public byte[] Serialize()
    {
        var buf = new byte[HeaderSize + Payload.Length];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Stamp);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)Payload.Length);
        Payload.Span.CopyTo(span[HeaderSize..]);
        return buf;
    }

    public static DipcFrame Reply(uint requestId, byte[] payload) =>
        new(ReplyTag, Timestamp(), requestId, FlagWantsReply, payload);

    public static uint Timestamp() =>
        unchecked((uint)(System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency));
}
