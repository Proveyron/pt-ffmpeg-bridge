using System.Text;
using Bridge.Protocol.Messages;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Bridge.Protocol;

public static class Replies
{
    /// <summary>ACF result codes seen on the wire.</summary>
    public const int AcfOk = 0;
    public const int AcfFail = unchecked((int)0x80004005);
    /// <summary>What QuickTime returned for files it could not open.</summary>
    public const int AcfCannotOpen = unchecked((int)0x80000008);

    /// <summary>The "no error" block QuickTime puts in every reply.</summary>
    public static Error Ok() => new()
    {
        ErrorType = (int)QTErrorCodes.QtNoError,
        ErrorMsg = new StringContainer { Type = 0, NumCodeunits = 0, StringSize = 1, Msg = ByteString.Empty },
    };

    public static Error Failure(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return new Error
        {
            ErrorType = (int)QTErrorCodes.QtError,
            ErrorMsg = new StringContainer { Type = 0, NumCodeunits = (uint)bytes.Length, StringSize = (uint)bytes.Length + 1, Msg = ByteString.CopyFrom(bytes) },
        };
    }

    /// <summary>Decodes a StringContainer; type 2 (what Pro Tools sends for paths) is UTF-16LE.</summary>
    public static string DecodeString(StringContainer s)
    {
        var bytes = s.Msg.Span;
        string text = s.Type switch
        {
            2 => Encoding.Unicode.GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes),
        };
        return text.TrimEnd('\0');
    }

    static readonly Dictionary<uint, MessageDescriptor> ReplyTypes = QuickTimeWrapperReflection.Descriptor.MessageTypes
        .Where(m => m.Name.StartsWith("SC_"))
        .ToDictionary(m => Djb2.Hash(m.Name[3..]));

    static readonly Dictionary<uint, string> MethodNames = QuickTimeWrapperReflection.Descriptor.MessageTypes
        .Where(m => m.Name.StartsWith("SC_") || m.Name.StartsWith("CS_"))
        .Select(m => m.Name[3..])
        .Concat(["DestroyQuickTimeInterface", "SH_RemoteFreeMem"])
        .Distinct()
        .ToDictionary(Djb2.Hash);

    public static string NameOf(uint tag) => MethodNames.TryGetValue(tag, out var n) ? n : $"0x{tag:x8}";

    /// <summary>
    /// A well-formed failure reply for a method we don't implement: the method's own SC_ type with
    /// every required field present, error set, and any acfResult field set to E_FAIL. Falls back to
    /// SC_Error when the method has no dedicated reply type.
    /// </summary>
    public static IMessage NotImplemented(uint tag, string why) => Default(tag, Failure(why), AcfFail);

    /// <summary>A success reply with every other field zeroed, for methods where "nothing to report" is right.</summary>
    public static IMessage Success(uint tag) => Default(tag, Ok(), AcfOk);

    static IMessage Default(uint tag, Error error, int acf)
    {
        if (!ReplyTypes.TryGetValue(tag, out var desc))
            return new SC_Error { Error = error, AcfResult = acf };
        var msg = desc.Parser.ParseFrom(ByteString.Empty);
        foreach (var field in desc.Fields.InFieldNumberOrder())
        {
            if (field.IsRepeated)
                continue;
            if (field.FieldType == FieldType.Message)
                field.Accessor.SetValue(msg, field.MessageType.ClrType == typeof(Error) ? error.Clone() : Empty(field.MessageType));
            else if (field.Name.Equals("acfResult", StringComparison.OrdinalIgnoreCase))
                field.Accessor.SetValue(msg, acf);
            else
                field.Accessor.SetValue(msg, DefaultFor(field));
        }
        return msg;
    }

    /// <summary>A nested message with its own required fields filled so it serializes.</summary>
    static IMessage Empty(MessageDescriptor desc)
    {
        var msg = desc.Parser.ParseFrom(ByteString.Empty);
        foreach (var field in desc.Fields.InFieldNumberOrder())
        {
            if (field.IsRepeated)
                continue;
            field.Accessor.SetValue(msg, field.FieldType == FieldType.Message ? Empty(field.MessageType) : DefaultFor(field));
        }
        return msg;
    }

    static object DefaultFor(FieldDescriptor f) => f.FieldType switch
    {
        FieldType.Bool => false,
        FieldType.Double => 0.0,
        FieldType.Float => 0f,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => 0L,
        FieldType.UInt64 or FieldType.Fixed64 => 0UL,
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => 0,
        FieldType.UInt32 or FieldType.Fixed32 => 0u,
        FieldType.Enum => 0,
        FieldType.String => "",
        FieldType.Bytes => ByteString.Empty,
        _ => throw new NotSupportedException(f.FieldType.ToString()),
    };
}
