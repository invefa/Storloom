using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Storloom.Core.Chats;

public sealed record MessagePart(string Kind, string Text = "", string Title = "", string Detail = "",
    string ReferenceSender = "", string ReferenceText = "", string[]? MediaKeys = null) {
    public string? MediaState { get; init; }
    public string? ReferenceKind { get; init; }
}

/// <summary>Derives a display model without replacing or executing the original payload.</summary>
public static class MessagePresentation {
    public static MessagePart[] Parse(ChatMessage message) {
        if (message.Parts.Length > 0) return message.Parts;
        string body = message.Text.Trim();
        if (body.StartsWith("[attachment] ", StringComparison.Ordinal)) {
            string name = body[13..].Trim(); string extension = Path.GetExtension(name).ToLowerInvariant();
            string kind = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" ? "image" :
                extension is ".mp4" or ".webm" ? "video" : extension is ".wav" or ".mp3" or ".ogg" or ".silk" ? "audio" : "file";
            return [new(kind, Title: Path.GetFileName(name), MediaKeys: [Path.GetFileName(name)])];
        }
        string xml = body;
        int start = xml.IndexOf('<');
        if (start >= 0 && (start == 0 || xml[..start].TrimEnd().EndsWith(':'))) xml = xml[start..];
        if (xml.StartsWith("&lt;", StringComparison.Ordinal)) xml = WebUtility.HtmlDecode(xml);
        if (xml.StartsWith('<')) {
            try {
                using var input = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
                return [ParseXml(XDocument.Load(input), message)];
            } catch (Exception ex) when (ex is XmlException or InvalidOperationException) { return [Fallback(message, "unparsed")]; }
        }
        if (body.StartsWith('{') || body.StartsWith('[') && !body.StartsWith("[attachment]", StringComparison.Ordinal)) {
            try { using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind == JsonValueKind.Object) return [ParseJson(document.RootElement)];
            } catch (JsonException) { }
            if (body.Length > 1000 || body.StartsWith('{')) return [Fallback(message, "unparsed")];
        }
        if (body.StartsWith("[unparsed message", StringComparison.Ordinal)) return [Fallback(message, "unsupported")];
        if (message.Platform == "WeChat") {
            string kind = WeChatKind(message.Type);
            if (kind != "text") return [new(kind, Text: kind == "system" ? body : "", Detail: kind == "system" ? "" : SafeSummary(body))];
        }
        if (message.Platform == "QQ" && message.Type is not (0 or 1 or 2)) return [Fallback(message, "unsupported")];
        return [new("text", body)];
    }
    private static MessagePart ParseXml(XDocument document, ChatMessage message) {
        var root = document.Root!;
        XElement? Find(string name) => root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        string Value(string name) => Find(name)?.Value.Trim() ?? "";
        string Attribute(string name, string attribute) => Find(name)?.Attribute(attribute)?.Value ?? "";
        var app = Find("appmsg");
        long subtype = long.TryParse(app?.Elements().FirstOrDefault(e => e.Name.LocalName == "type")?.Value, out long parsed) ? parsed : message.Type >> 32;
        string kind = app != null ? subtype switch {
            1 => "text", 3 or 76 or 92 => "music", 4 => "video", 5 => "link", 6 or 74 => "file", 8 => "sticker", 17 => "location", 19 or 40 => "forward",
            33 or 36 => "miniapp", 50 => "channel", 51 or 63 => "channel", 57 => "reply", 62 => "system", 2000 => "transfer", 2001 or 2003 => "redpacket", _ => "unsupported"
        } : message.Platform == "QQ" ? QQKind(message.Type) : WeChatKind(message.Type);
        if (root.Attribute("action")?.Value.Contains("viewMultiMsg", StringComparison.OrdinalIgnoreCase) == true) kind = "forward";
        if (Find("img") != null) kind = "image";
        else if (Find("videomsg") != null) kind = "video";
        else if (Find("voicemsg") != null) kind = "audio";
        else if (Find("emoji") != null) kind = "sticker";
        else if (Find("location") != null) kind = "location";
        else if (Find("sysmsg") != null) kind = "system";
        if (root.Name.LocalName == "msg" && root.Attribute("nickname") != null) kind = "contact";
        if (kind == "text" && app == null) kind = "unsupported";
        string title = SafeSummary(Value("title")), detail = SafeSummary(Value("des")), text = "", sender = "", reference = "";
        string? referenceKind = null;
        if (kind == "reply") {
            text = title; title = ""; var refer = Find("refermsg");
            sender = refer?.Elements().FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value ?? "";
            reference = SafeSummary(refer?.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Value ?? "");
            if (long.TryParse(refer?.Elements().FirstOrDefault(e => e.Name.LocalName == "type")?.Value, out long referenceType)) referenceKind = WeChatKind(referenceType);
        } else if (kind == "transfer") {
            title = SafeSummary(Value("feedesc")); detail = SafeSummary(Value("pay_memo"));
            // Do not infer a payment's current status from its historical message.
        } else if (kind == "redpacket") { title = SafeSummary(Value("sendertitle")); detail = SafeSummary(Value("receivertitle")); }
        else if (kind == "location") { title = Attribute("location", "poiname"); detail = Attribute("location", "label"); }
        else if (kind == "contact") { title = root.Attribute("nickname")?.Value ?? ""; detail = root.Attribute("username")?.Value ?? ""; }
        else if (kind == "forward") detail = string.Join("\n", root.Descendants().Where(e => e.Name.LocalName is "datadesc" or "sourcename" or "summary").Take(12).Select(e => SafeSummary(e.Value)).Where(t => t != ""));
        else if (kind == "audio" || kind == "video") {
            string seconds = kind == "audio" ? Attribute("voicemsg", "voicelength") : Attribute("videomsg", "playlength");
            if (double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out double length)) detail = (kind == "audio" ? length / 1000 : length).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";
        } else if (kind == "system") text = SafeSummary(Value("replacemsg") is string replacement && replacement.Length > 0 ? replacement : root.Value);
        else if (kind == "text") text = title;
        if (kind is "link" or "music" or "miniapp" or "channel") detail = string.Join("\n", new[] { detail, SafeSummary(Value("sourcedisplayname")), SafeUrl(Value("url")) }.Where(s => s != ""));
        var keys = root.DescendantsAndSelf().SelectMany(e => e.Attributes().Where(a => a.Name.LocalName.Contains("md5", StringComparison.OrdinalIgnoreCase)).Select(a => a.Value)
            .Concat(e.Name.LocalName is "md5" or "filemd5" ? [e.Value] : Array.Empty<string>()))
            .Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-fA-F0-9]{32}$")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kind == "file" && title != "") keys.Add(Path.GetFileName(title));
        return new(kind, text, title, detail, sender, reference, keys.ToArray()) { ReferenceKind = referenceKind };
    }
    private static MessagePart ParseJson(JsonElement root) {
        string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? SafeSummary(value.GetString() ?? "") : "";
        string app = Read("app"), title = Read("prompt");
        string kind = app.Contains("miniapp", StringComparison.OrdinalIgnoreCase) ? "miniapp" : app.Contains("map", StringComparison.OrdinalIgnoreCase) ? "location" : "link";
        string detail = "";
        if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object) {
            foreach (var entry in meta.EnumerateObject()) {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                string Get(string key) => entry.Value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                title = SafeSummary(Get("title")) is string t && t.Length > 0 ? t : title;
                detail = SafeSummary(Get("desc"));
                string url = SafeUrl(Get("jumpUrl")); if (url != "") detail += "\n" + url;
                break;
            }
        }
        return title == "" && detail == "" ? new("unsupported") : new(kind, Title: title, Detail: detail);
    }
    private static MessagePart Fallback(ChatMessage m, string fallback) {
        string kind = m.Platform == "WeChat" ? WeChatKind(m.Type) : QQKind(m.Type);
        return new(kind is "text" or "unsupported" ? fallback : kind);
    }
    public static string WeChatKind(long type) => (type & 0xffffffff) switch {
        1 => "text", 3 => "image", 34 => "audio", 37 or 42 => "contact", 43 or 62 => "video", 47 => "sticker", 48 => "location", 49 => "unsupported",
        50 or 64 => "call", 10000 or 10002 => "system", _ => "unsupported" };
    public static string QQKind(long type) => type switch {
        0 or 1 or 2 => "text", 3 or 21 or 27 => "file", 4 or 11 => "link", 5 => "system", 6 => "audio", 7 => "video", 8 => "forward", 9 => "reply",
        10 => "wallet", 13 or 17 or 24 => "sticker", 14 or 15 => "gift", 25 => "location", _ => "unsupported" };
    public static string SafeSummary(string value) {
        if (value.Length > 4096) value = value[..4096];
        if (value.Contains('<') || value.Contains("&lt;", StringComparison.Ordinal)) return "";
        return value;
    }
    private static string SafeUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? value : "";
}
