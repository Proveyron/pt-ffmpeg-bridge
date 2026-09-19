"""Regenerate QuickTimeWrapper.proto from the user's installed ProToolsQuickTimeServer.exe.

The server embeds its protobuf FileDescriptorProto. We decode it and print proto2 source so
the bridge can speak the same wire protocol. The output is derived from Avid's binary and is
therefore never committed (see .gitignore); run this on a machine with Pro Tools installed.

usage: python tools/extract_proto.py [path-to-ProToolsQuickTimeServer.exe] [out.proto]
"""
import sys
from pathlib import Path

from google.protobuf import descriptor_pb2 as dp

_SERVER_DIR = r"C:\Program Files\Avid\Pro Tools\QuickTimeServer"
# After install.ps1 the original lives on as ProToolsQuickTimeServer.orig.exe.
DEFAULT_EXE = next((p for p in (_SERVER_DIR + r"\ProToolsQuickTimeServer.orig.exe", _SERVER_DIR + r"\ProToolsQuickTimeServer.exe")
                    if Path(p).exists()), _SERVER_DIR + r"\ProToolsQuickTimeServer.exe")
MARKER = b"\x0a\x16QuickTimeWrapper.proto"
FILE_FIELDS = set(range(1, 13))  # field numbers valid in FileDescriptorProto

F = dp.FieldDescriptorProto
TYPE_NAMES = {
    F.TYPE_DOUBLE: "double", F.TYPE_FLOAT: "float", F.TYPE_INT64: "int64", F.TYPE_UINT64: "uint64",
    F.TYPE_INT32: "int32", F.TYPE_FIXED64: "fixed64", F.TYPE_FIXED32: "fixed32", F.TYPE_BOOL: "bool",
    F.TYPE_STRING: "string", F.TYPE_BYTES: "bytes", F.TYPE_UINT32: "uint32", F.TYPE_SFIXED32: "sfixed32",
    F.TYPE_SFIXED64: "sfixed64", F.TYPE_SINT32: "sint32", F.TYPE_SINT64: "sint64",
}
LABELS = {F.LABEL_OPTIONAL: "optional", F.LABEL_REQUIRED: "required", F.LABEL_REPEATED: "repeated"}


def read_varint(buf, pos):
    result = shift = 0
    while True:
        b = buf[pos]
        result |= (b & 0x7F) << shift
        pos += 1
        shift += 7
        if b < 0x80:
            return result, pos


def find_descriptor(data):
    start = data.find(MARKER)
    if start < 0:
        raise SystemExit("embedded QuickTimeWrapper.proto descriptor not found")
    pos = start
    # Walk top-level fields until we hit bytes that can't belong to a FileDescriptorProto.
    while pos < len(data):
        tag, nxt = read_varint(data, pos)
        field, wire = tag >> 3, tag & 7
        if field not in FILE_FIELDS or wire not in (0, 2):
            break
        if wire == 2:
            length, nxt = read_varint(data, nxt)
            nxt += length
        else:
            _, nxt = read_varint(data, nxt)
        pos = nxt
    fd = dp.FileDescriptorProto()
    fd.ParseFromString(data[start:pos])
    return fd


def fmt_default(field):
    if not field.HasField("default_value"):
        return ""
    v = field.default_value
    if field.type in (F.TYPE_STRING, F.TYPE_BYTES):
        v = '"' + v.replace("\\", "\\\\").replace('"', '\\"') + '"'
    return f" [default = {v}]"


def emit_enum(e, indent, out):
    out.append(f"{indent}enum {e.name} {{")
    for v in e.value:
        out.append(f"{indent}  {v.name} = {v.number};")
    out.append(f"{indent}}}")


def emit_message(m, indent, out):
    out.append(f"{indent}message {m.name} {{")
    for e in m.enum_type:
        emit_enum(e, indent + "  ", out)
    for n in m.nested_type:
        emit_message(n, indent + "  ", out)
    for f in m.field:
        typ = f.type_name.lstrip(".") if f.type_name else TYPE_NAMES[f.type]
        packed = " [packed = true]" if f.options.packed else ""
        out.append(f"{indent}  {LABELS[f.label]} {typ} {f.name} = {f.number}{fmt_default(f)}{packed};")
    out.append(f"{indent}}}")


def to_proto_source(fd):
    out = ['syntax = "proto2";', "", f"package {fd.package};", ""]
    out.append("option csharp_namespace = \"Bridge.Protocol.Messages\";")
    out.append("")
    for e in fd.enum_type:
        emit_enum(e, "", out)
        out.append("")
    for m in fd.message_type:
        emit_message(m, "", out)
        out.append("")
    return "\n".join(out)


def main():
    exe = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(DEFAULT_EXE)
    dest = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / "proto" / "QuickTimeWrapper.proto"
    fd = find_descriptor(exe.read_bytes())
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(to_proto_source(fd), encoding="utf-8")
    print(f"{dest}: {len(fd.message_type)} messages, {len(fd.enum_type)} enums")


if __name__ == "__main__":
    main()
