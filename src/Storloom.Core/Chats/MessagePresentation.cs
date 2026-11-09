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
    public string? ReferenceId { get; init; }
    public bool Inline { get; init; }
    public string? EmojiId { get; init; }
    public ForwardedMessage[] Forwarded { get; init; } = [];
}
public sealed record ForwardedMessage(string Sender, string Time, MessagePart[] Parts);

/// <summary>Derives a display model without replacing or executing the original payload.</summary>
public static class MessagePresentation {
    public static ChatMessage ResolveSenderPrefix(ChatMessage message) {
        if (message.Platform != "WeChat") return message;
        var match = System.Text.RegularExpressions.Regex.Match(message.Text, "^(?<sender>wxid_[A-Za-z0-9_-]+|[A-Za-z0-9_.-]+@openim):\\r?\\n");
        if (!match.Success) return message;
        string sender = match.Groups["sender"].Value;
        if (!message.Conversation.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) && sender != message.Sender) return message;
        return message with { Sender = sender, SenderName = sender == message.Sender ? message.SenderName : null, DisplayText = message.Text[match.Length..] };
    }
    public static MessagePart[] Parse(ChatMessage message) {
        if (message.Parts.Length > 0) return message.Parts;
        // Database text may include terminal C-string padding. Keep Text lossless; normalize only this derived view.
        string body = (ResolveSenderPrefix(message).DisplayText ?? message.Text).Trim().TrimEnd('\0').Trim();
        if(message.Platform=="WeChat"&&message.Type%4294967296==1)return [new("text",body)];
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
        if (message.Platform == "QQ" && message.Type == 5) return [new("system", PlainSystemText(body))];
        if (message.Platform == "QQ" && message.Type is not (0 or 1 or 2)) return [Fallback(message, "unsupported")];
        return [new("text", body)];
    }
    private static MessagePart ParseXml(XDocument document, ChatMessage message, int depth = 0) {
        var root = document.Root!;
        if (root.Name.LocalName == "gtip") {
            string notice = string.Concat(root.Descendants().Where(e => e.Name.LocalName is "qq" or "nor").Select(e =>
                e.Name.LocalName == "qq" ? e.Attribute("nm")?.Value ?? e.Attribute("jp")?.Value ?? e.Attribute("uin")?.Value ?? e.Value : e.Attribute("txt")?.Value ?? e.Value));
            return new("system", notice);
        }
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
        if (app == null) {
            if (Find("img") != null) kind = "image";
            else if (Find("videomsg") != null) kind = "video";
            else if (Find("voicemsg") != null) kind = "audio";
            else if (Find("emoji") != null) kind = "sticker";
            else if (Find("location") != null) kind = "location";
            else if (Find("sysmsg") != null) kind = "system";
        }
        if (root.Name.LocalName == "msg" && root.Attribute("nickname") != null) kind = "contact";
        if (kind == "text" && app == null) kind = "unsupported";
        string title = SafeSummary(Value("title")), detail = SafeSummary(Value("des")), text = "", sender = "", reference = "";
        string? referenceKind = null, referenceId = null;
        ForwardedMessage[] forwarded = [];
        if (kind == "reply") {
            text = title; title = ""; var refer = Find("refermsg");
            sender = refer?.Elements().FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value ?? "";
            reference = SafeSummary(refer?.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Value ?? "");
            referenceId = refer?.Elements().FirstOrDefault(e => e.Name.LocalName == "svrid")?.Value;
            if (referenceId is "" or "0") referenceId = null;
            if (long.TryParse(refer?.Elements().FirstOrDefault(e => e.Name.LocalName == "type")?.Value, out long referenceType)) referenceKind = WeChatKind(referenceType);
        } else if (kind == "transfer") {
            title = SafeSummary(Value("feedesc")); detail = SafeSummary(Value("pay_memo"));
            // Do not infer a payment's current status from its historical message.
        } else if (kind == "redpacket") { title = SafeSummary(Value("sendertitle")); detail = SafeSummary(Value("receivertitle")); }
        else if (kind == "location") { title = Attribute("location", "poiname"); detail = Attribute("location", "label"); }
        else if (kind == "contact") { title = root.Attribute("nickname")?.Value ?? ""; detail = root.Attribute("username")?.Value ?? ""; }
        else if (kind == "forward") {
            forwarded = ReadForward(root, message, depth);
            detail = forwarded.Length > 0 ? "" : string.Join("\n", root.Descendants().Where(e => e.Name.LocalName is "datadesc" or "sourcename" or "summary").Take(12).Select(e => SafeSummary(e.Value)).Where(t => t != ""));
        }
        else if (kind == "audio" || kind == "video") {
            string seconds = kind == "audio" ? Attribute("voicemsg", "voicelength") : Attribute("videomsg", "playlength");
            if (double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out double length)) detail = (kind == "audio" ? length / 1000 : length).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";
        } else if (kind == "system") {
            text = Value("replacemsg");
            if (text.Length == 0) {
                text = Value("template");
                foreach (var link in root.Descendants().Where(e => e.Name.LocalName == "link")) {
                    string key = link.Attribute("name")?.Value ?? "";
                    string names = string.Join(", ", link.Descendants().Where(e => e.Name.LocalName == "nickname").Select(e => e.Value));
                    if (key.Length > 0 && names.Length > 0) text = text.Replace("$" + key + "$", names, StringComparison.Ordinal);
                }
                if (text.Length == 0) text = Value("content");
                if (text.Length == 0) text = root.Value;
            }
            foreach (var member in root.Descendants().Where(e => e.Name.LocalName is "fromusername" or "pattedusername")) {
                string key = member.Name.LocalName;
                text = text.Replace("${" + key + "}", member.Value, StringComparison.Ordinal)
                    .Replace("${" + key + "@textstatusicon}", member.Value, StringComparison.Ordinal);
            }
            text = PlainSystemText(text);
        }
        else if (kind == "text") text = title;
        if (kind is "link" or "music" or "miniapp" or "channel") detail = string.Join("\n", new[] { detail, SafeSummary(Value("sourcedisplayname")), SafeUrl(Value("url")) }.Where(s => s != ""));
        var keys = root.DescendantsAndSelf().SelectMany(e => e.Attributes().Where(a => a.Name.LocalName.Contains("md5", StringComparison.OrdinalIgnoreCase)).Select(a => a.Value)
            .Concat(e.Name.LocalName is "md5" or "filemd5" ? [e.Value] : Array.Empty<string>()))
            .Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-fA-F0-9]{32}$")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kind == "file" && title != "") keys.Add(Path.GetFileName(title));
        keys.AddRange(forwarded.SelectMany(f => f.Parts).SelectMany(p => p.MediaKeys ?? []));
        return new(kind, text, title, detail, sender, reference, keys.ToArray()) { ReferenceKind = referenceKind, ReferenceId = referenceId, Forwarded = forwarded };
    }
    private static ForwardedMessage[] ReadForward(XElement root, ChatMessage message, int depth) {
        if (depth >= 4) return [];
        var record = root.Descendants().FirstOrDefault(e => e.Name.LocalName is "recorditem" or "recordinfo");
        if (record == null) return [];
        if (!record.HasElements) {
            try {
                using var input = XmlReader.Create(new StringReader(record.Value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                record = XElement.Load(input);
            } catch (XmlException) { return []; }
        }
        return record.Descendants().Where(e => e.Name.LocalName == "dataitem").Take(500).Select(item => {
            string Read(string name) => item.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
            string type = item.Attribute("datatype")?.Value ?? "1";
            string kind = type switch { "1" => "text", "2" => "image", "3" => "audio", "4" => "video", "8" => "file", "17" => "forward", _ => "unsupported" };
            var keys = item.Descendants().Where(e => e.Name.LocalName.Contains("md5", StringComparison.OrdinalIgnoreCase)).Select(e => e.Value)
                .Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-fA-F0-9]{32}$")).ToArray();
            MessagePart part = new(kind, kind == "text" ? SafeSummary(Read("datadesc")) : "", Title: SafeSummary(Read("datatitle")), MediaKeys: keys);
            if (kind == "forward") part = part with { Forwarded = ReadForward(item, message, depth + 1) };
            return new ForwardedMessage(SafeSummary(Read("sourcename")), SafeSummary(Read("sourcetime")), [part]);
        }).ToArray();
    }
    private static string PlainSystemText(string value) {
        value = System.Text.RegularExpressions.Regex.Replace(value, "<[^>]*>", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        return SafeSummary(WebUtility.HtmlDecode(value));
    }
    private static MessagePart ParseJson(JsonElement root) {
        string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? SafeSummary(value.GetString() ?? "") : "";
        string app = Read("app"), title = Read("prompt");
        string kind = app.Contains("multimsg", StringComparison.OrdinalIgnoreCase) ? "forward" : app.Contains("miniapp", StringComparison.OrdinalIgnoreCase) ? "miniapp" : app.Contains("map", StringComparison.OrdinalIgnoreCase) ? "location" : "link";
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
