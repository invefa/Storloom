using System.Text;

namespace Storloom.Core.Chats;

/// <summary>NTQQ database wire fields. Unknown segments remain inspectable in the original payload.</summary>
public static class QQMessageParser {
    private sealed record Field(int Number, ulong Integer, byte[]? Bytes);
    public static MessagePart[] Parse(byte[] bytes, long outerType) => Parse(bytes, outerType, 0);
    private static MessagePart[] Parse(byte[] bytes, long outerType, int depth) {
        if (depth > 6) return [new("unsupported")];
        try {
            var fields = Fields(bytes); var segments = fields.Where(f => f.Number == 40800 && f.Bytes != null).ToArray();
            if (segments.Length == 0) return [Segment(fields, outerType, depth)];
            return segments.Select(f => Segment(Fields(f.Bytes!), outerType, depth)).ToArray();
        } catch (Exception ex) when (ex is InvalidDataException or OverflowException or DecoderFallbackException) { return [new("unparsed")]; }
    }
    private static MessagePart Segment(List<Field> fields, long outerType, int depth) {
        string Text(int number) {
            byte[]? data = fields.FirstOrDefault(f => f.Number == number)?.Bytes;
            return data == null ? "" : new UTF8Encoding(false, true).GetString(data);
        }
        long Number(int number) => checked((long)(fields.FirstOrDefault(f => f.Number == number)?.Integer ?? 0));
        long type = Number(45002);
        string kind = type switch { 1 => "text", 2 => "image", 3 => "file", 4 => "audio", 5 => "video", 6 or 11 => "sticker", 7 => "reply",
            8 => "system", 9 => "redpacket", 10 => "link", 14 => "markdown", 16 => "forward", 17 => "buttons", 21 => "call", _ => MessagePresentation.QQKind(outerType) };
        string body = Text(45101), name = Text(45402), detail = "", reference = Text(47413), sender = Text(47421);
        string? referenceKind = null;
        var keys = new List<string>(); if (name != "") keys.Add(Path.GetFileName(name.Replace('\\', '/')));
        foreach (int number in new[] { 45406, 45424, 45862 }) {
            var data = fields.FirstOrDefault(f => f.Number == number)?.Bytes;
            if (data is { Length: 16 }) keys.Add(Convert.ToHexString(data).ToLowerInvariant());
            else if (data != null && data.Length == 32) keys.Add(Encoding.ASCII.GetString(data));
        }
        foreach (int number in new[] { 45403, 45812, 45814 }) { string path = Text(number); if (path != "") keys.Add(Path.GetFileName(path.Replace('\\', '/'))); }
        if (fields.FirstOrDefault(f => f.Number == 45526)?.Bytes is byte[] mediaRef) {
            var hash = Fields(mediaRef).FirstOrDefault(f => f.Number == 45530)?.Bytes;
            if (hash != null) keys.Add(Encoding.ASCII.GetString(hash));
        }
        if (kind == "sticker") { name = Text(47602); if (name == "") name = Text(45815); if (name == "" && Number(47601) > 0) name = "#" + Number(47601); }
        if (kind == "video" && Number(45410) > 0) detail = Number(45410) + " s";
        if (kind == "reply" && reference == "") {
            var quoted = fields.FirstOrDefault(f => f.Number is 47423 or 47710)?.Bytes;
            if (quoted != null) { var quotedParts = Parse(quoted, 2, depth + 1);
                reference = string.Join("\n", quotedParts.Where(p => p.Kind == "text").Select(p => p.Text)); referenceKind = quotedParts.FirstOrDefault()?.Kind; }
        }
        if (kind == "call") { detail = MessagePresentation.SafeSummary(Text(48153)); if (Number(48152) > 0) detail += " " + Number(48152) + " s"; }
        if (kind == "system" && body == "") body = MessagePresentation.SafeSummary(Text(80900));
        if (kind == "system" && body.TrimStart().StartsWith('<')) {
            var notice = MessagePresentation.Parse(new("QQ", "", "", 0, 5, body)).FirstOrDefault();
            if (notice?.Kind == "system") return notice;
        }
        if (kind == "text" && body == "") {
            // Compatibility with legacy nested wrappers; never recursively reinterpret a text field as protobuf.
            var children = fields.Where(f => f.Bytes != null && f.Number < 40000).ToArray();
            body = string.Join("\n", children.Select(f => ChatExporter.DecodeProto(f.Bytes!)).Where(t => !t.StartsWith("[unparsed message", StringComparison.Ordinal)));
        }
        if (kind is "link" or "forward") {
            foreach (var field in fields.Where(f => f.Bytes != null && f.Number is 47901 or 48403 or 48602)) {
                try { string structured = new UTF8Encoding(false, true).GetString(field.Bytes!);
                    var parsed = MessagePresentation.Parse(new("QQ", "", "", 0, outerType, structured));
                    if (parsed.Any(p => p.Kind != "unsupported")) return parsed[0];
                } catch (DecoderFallbackException) { }
            }
        }
        if (kind == "text" && body == "") kind = "unsupported";
        if (kind == "text" && (body.TrimStart().StartsWith('<') || body.TrimStart().StartsWith('{')))
            return MessagePresentation.Parse(new("QQ", "", "", 0, outerType, body))[0];
        return new(kind, kind is "text" or "reply" or "system" or "markdown" ? body : "", MessagePresentation.SafeSummary(name), detail,
            MessagePresentation.SafeSummary(sender), MessagePresentation.SafeSummary(reference), keys.ToArray()) { ReferenceKind = referenceKind,
                ReferenceId = kind == "reply" && Number(47401) != 0 ? Number(47401).ToString(System.Globalization.CultureInfo.InvariantCulture) : null };
    }
    public static ForwardedMessage[] ParseForwardCache(byte[] bytes) {
        try {
            return Fields(bytes).Where(f => f.Number == 40900 && f.Bytes != null).Take(500).Select(f => {
                var fields = Fields(f.Bytes!);
                string Read(int n) => fields.FirstOrDefault(v => v.Number == n)?.Bytes is byte[] b ? new UTF8Encoding(false, true).GetString(b) : "";
                long type = checked((long)(fields.FirstOrDefault(v => v.Number == 40011)?.Integer ?? 0));
                var parts = fields.Where(v => v.Number == 40800 && v.Bytes != null).SelectMany(v => Parse(v.Bytes!, type, 1)).ToArray();
                string sender = Read(40093); if (sender == "") sender = (fields.FirstOrDefault(v => v.Number == 40033)?.Integer ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                long timestamp = checked((long)(fields.FirstOrDefault(v => v.Number == 40050)?.Integer ?? 0));
                string time = timestamp is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("u") : "";
                return new ForwardedMessage(sender, time, parts);
            }).Where(f => f.Parts.Length > 0).ToArray();
        } catch (Exception ex) when (ex is InvalidDataException or OverflowException or DecoderFallbackException) { return []; }
    }
    private static List<Field> Fields(byte[] bytes) {
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Message exceeds parsing limit.");
        var fields = new List<Field>(); int offset = 0;
        ulong Varint() {
            ulong value = 0;
            for (int shift = 0; shift < 64 && offset < bytes.Length; shift += 7) {
                byte b = bytes[offset++]; if (shift == 63 && b > 1) throw new InvalidDataException("Invalid protobuf integer.");
                value |= (ulong)(b & 127) << shift; if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("Truncated protobuf field.");
        }
        while (offset < bytes.Length) {
            if (fields.Count > 10000) throw new InvalidDataException("Too many message fields.");
            ulong tag = Varint(); int number = checked((int)(tag >> 3)), wire = (int)(tag & 7);
            if (number == 0) throw new InvalidDataException("Invalid protobuf field.");
            if (wire == 0) fields.Add(new(number, Varint(), null));
            else if (wire == 2) { int length = checked((int)Varint()); if (length > bytes.Length - offset) throw new InvalidDataException("Truncated protobuf data.");
                fields.Add(new(number, 0, bytes.AsSpan(offset, length).ToArray())); offset += length; }
            else if (wire is 1 or 5) { offset = checked(offset + (wire == 1 ? 8 : 4)); if (offset > bytes.Length) throw new InvalidDataException("Truncated protobuf data."); }
            else throw new InvalidDataException("Unsupported protobuf wire type.");
        }
        return fields;
    }
}
