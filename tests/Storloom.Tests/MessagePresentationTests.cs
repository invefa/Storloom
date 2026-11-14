using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class MessagePresentationTests {
    [Theory][InlineData(47402)][InlineData(47419)]
    public void QQRepliesReadEmbeddedSequenceSenderAndTimeWithoutLegacyReferenceFields(int sequenceField){
        byte[] raw=B(40800,[..I(45002,7),..I(47402,sequenceField==47402?42UL:17UL),..(sequenceField==47419?I(47419,42):[]),..I(47403,12345678),..I(47404,1700000000),..B(47423,[..I(45002,1),..S(45101,"quoted fixture")])]);
        var reply=Assert.Single(QQMessageParser.Parse(raw,2));Assert.Equal("reply",reply.Kind);Assert.Equal("seq:42",reply.ReferenceId);Assert.Equal("12345678",reply.ReferenceSender);Assert.Equal(1700000000L,reply.ReferenceTimestamp);Assert.Equal("quoted fixture",reply.ReferenceText);
        var empty=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,7),..I(47402,0),..I(47403,0),..I(47404,0)]),2));Assert.Null(empty.ReferenceId);Assert.Empty(empty.ReferenceSender);Assert.Null(empty.ReferenceTimestamp);
    }
    [Fact]
    public void QQEmojiReactionsAreAttachedCountsAndNeverConfusedWithRecallRoles() {
        byte[] raw = [..B(40062,[..S(48301,"75"),..I(48302,1),..I(48303,3),..I(48304,1)]),
            ..B(40062,[..S(48301,"128077"),..I(48303,2)]),..B(40062,[..S(48301,"1"),..I(48303,0)])];
        var reactions=QQMessageParser.ParseReactions(raw);Assert.Equal(2,reactions.Length);
        Assert.Equal("75",reactions[0].EmojiId);Assert.Equal(3,reactions[0].Count);Assert.True(reactions[0].IsSelf);
        Assert.Equal("128077",reactions[1].EmojiId);Assert.False(reactions[1].IsSelf);
        Assert.Empty(QQMessageParser.ParseReactions([255,255]));
        foreach(int role in new[]{0,1,2,99}) {
            var part=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,8),..I(47702,(ulong)role),..S(47705,"Member"),..S(47714,"Operator"),..S(47713,"Fixture reason")]),5));
            Assert.Equal("recall",part.Kind);Assert.Equal(role,part.RecallRole);Assert.Equal("Fixture reason",part.Detail);
        }
    }
    [Theory]
    [InlineData("wxid_fixture_member", "wxid_fixture_member:\nGoing to make tea")]
    [InlineData("123456789@openim", "123456789@openim:\r\nGoing to make tea")]
    public void WeChatGroupSenderPrefixesBecomeMetadataWithoutChangingTheOriginal(string sender,string payload) {
        var original=new ChatMessage("WeChat","fixture@chatroom","fixture@chatroom",0,1,payload);
        var normalized=MessagePresentation.ResolveSenderPrefix(original);
        Assert.Equal(sender,normalized.Sender);Assert.Equal("Going to make tea",normalized.DisplayText);Assert.Equal(payload,normalized.Text);
        Assert.Equal("Going to make tea",MessagePresentation.Parse(normalized)[0].Text);
        Assert.Equal(payload,MessagePresentation.ResolveSenderPrefix(original with{Platform="QQ"}).Text);
        Assert.Null(MessagePresentation.ResolveSenderPrefix(original with{Conversation="wxid_other",Sender="wxid_other"}).DisplayText);
    }
    [Fact]
    public void EmbeddedForwardsReferencesAndSystemTemplatesPreserveTheirMeaning() {
        var reply = MessagePresentation.Parse(new("WeChat", "fixture", "friend", 0, 49,
            "<msg><appmsg><type>57</type><title>Reply</title><refermsg><createtime>1700000000</createtime><svrid>9223372036854775806</svrid><type>3</type><content><msg><img md5=\"0123456789abcdef0123456789abcdef\"/></msg></content></refermsg></appmsg></msg>"))[0];
        Assert.Equal("reply", reply.Kind); Assert.Equal("9223372036854775806", reply.ReferenceId); Assert.Equal("image", reply.ReferenceKind);Assert.Equal(1700000000L,reply.ReferenceTimestamp);
        var system = MessagePresentation.Parse(new("WeChat", "fixture", "", 0, 10002,
            "<sysmsg><sysmsgtemplate><content_template><template>$actor$ invited $member$</template><link_list><link name=\"actor\"><memberlist><member><nickname>Alex</nickname></member></memberlist></link><link name=\"member\"><memberlist><member><nickname>Sam</nickname></member></memberlist></link></link_list></content_template></sysmsgtemplate></sysmsg>"))[0];
        Assert.Equal("system", system.Kind); Assert.Equal("Alex invited Sam", system.Text);
        var forward = MessagePresentation.Parse(new("WeChat", "fixture", "friend", 0, 49,
            "<msg><appmsg><type>19</type><title>Forward</title><recorditem><![CDATA[<recordinfo><datalist><dataitem datatype=\"1\"><sourcename>Alex</sourcename><sourcetime>12:00</sourcetime><datadesc>Forward text</datadesc></dataitem><dataitem datatype=\"2\"><sourcename>Sam</sourcename><fullmd5>0123456789abcdef0123456789abcdef</fullmd5></dataitem></datalist></recordinfo>]]></recorditem></appmsg></msg>"))[0];
        Assert.Equal("forward", forward.Kind); Assert.Equal(2, forward.Forwarded.Length);
        Assert.Equal("Forward text", forward.Forwarded[0].Parts[0].Text); Assert.Equal("image", forward.Forwarded[1].Parts[0].Kind);
        Assert.Contains("0123456789abcdef0123456789abcdef", forward.MediaKeys!);
    }
    [Theory]
    [InlineData(3,"<msg><img md5=\"0123456789abcdef0123456789abcdef\"/></msg>","image")]
    [InlineData(34,"<msg><voicemsg voicelength=\"2300\"/></msg>","audio")]
    [InlineData(43,"<msg><videomsg playlength=\"6\"/></msg>","video")]
    [InlineData(47,"<msg><emoji md5=\"0123456789abcdef0123456789abcdef\"/></msg>","sticker")]
    [InlineData(48,"<msg><location label=\"Fixture location\" poiname=\"Fixture place\"/></msg>","location")]
    [InlineData(42,"<msg nickname=\"Fixture contact\" username=\"fixture\"/>","contact")]
    [InlineData(50,"Call fixture","call")]
    [InlineData(10002,"<sysmsg><replacemsg>Message recalled</replacemsg></sysmsg>","system")]
    [InlineData(49,"<msg><appmsg><type>5</type><title>Fixture link</title><url>https://example.invalid</url></appmsg></msg>","link")]
    [InlineData(49,"<msg><appmsg><type>6</type><title>fixture.pdf</title></appmsg></msg>","file")]
    [InlineData(49,"<msg><appmsg><type>19</type><title>Forwarded chat</title></appmsg></msg>","forward")]
    [InlineData(49,"<msg><appmsg><type>33</type><title>Fixture app</title></appmsg></msg>","miniapp")]
    [InlineData(49,"<msg><appmsg><type>51</type><title>Fixture video</title></appmsg></msg>","channel")]
    [InlineData(49,"<msg><appmsg><type>2000</type><wcpayinfo><feedesc>12.34</feedesc></wcpayinfo></appmsg></msg>","transfer")]
    [InlineData(49,"<msg><appmsg><type>2001</type><sendertitle>Fixture gift</sendertitle></appmsg></msg>","redpacket")]
    [InlineData(49,"<msg><appmsg><type>92</type><title>Fixture song</title></appmsg></msg>","music")]
    [InlineData(49,"<msg><appmsg><type>99999</type></appmsg></msg>","unsupported")]
    [InlineData(49,"<msg><broken>","unparsed")]
    public void WeChatTypesHaveExplicitDisplayKinds(long type,string body,string kind) {
        var original=new ChatMessage("WeChat","fixture","sender",1700000000,type,body);
        var part=Assert.Single(MessagePresentation.Parse(original));Assert.Equal(kind,part.Kind);Assert.Equal(body,original.Text);
    }
    [Fact]
    public void RepliesRetainReadableContextWithoutExpandingEmbeddedMarkup() {
        var message=new ChatMessage("WeChat","","",0,49,"<msg><appmsg><type>57</type><title>My reply</title><refermsg><displayname>Fixture friend</displayname><content>Quoted text</content></refermsg></appmsg></msg>");
        var part=Assert.Single(MessagePresentation.Parse(message));Assert.Equal("reply",part.Kind);Assert.Equal("My reply",part.Text);
        Assert.Equal("Fixture friend",part.ReferenceSender);Assert.Equal("Quoted text",part.ReferenceText);
        part=Assert.Single(MessagePresentation.Parse(message with{Text=message.Text.Replace("Quoted text","&lt;img src='https://example.invalid'&gt;")}));
        Assert.Equal("",part.ReferenceText);
    }
    [Fact]
    public void DtdAndUnknownJsonCannotExecuteOrLeakMarkupIntoTheDisplay() {
        var message=new ChatMessage("WeChat","","",0,49,"<!DOCTYPE msg [<!ENTITY x SYSTEM 'file:///not-a-real-fixture'>]><msg>&x;</msg>");
        Assert.Equal("unparsed",Assert.Single(MessagePresentation.Parse(message)).Kind);
        var unknown=Assert.Single(MessagePresentation.Parse(message with{Text="{\"secret_markup\":\"<script>unsafe()</script>\"}"}));
        Assert.Equal("unsupported",unknown.Kind);Assert.Empty(unknown.Text);
        var card=Assert.Single(MessagePresentation.Parse(message with{Platform="QQ",Text="{\"app\":\"com.tencent.miniapp\",\"prompt\":\"Fixture app\"}"}));
        Assert.Equal("miniapp",card.Kind);Assert.Equal("Fixture app",card.Title);
    }
    private static byte[] V(ulong value) {
        var result=new List<byte>();do{byte next=(byte)(value&127);value>>=7;result.Add((byte)(next|(value>0?128:0)));}while(value>0);return result.ToArray();
    }
    private static byte[] I(int field,ulong value)=>[..V((ulong)(field<<3)),..V(value)];
    private static byte[] B(int field,byte[] value)=>[..V((ulong)(field<<3|2)),..V((ulong)value.Length),..value];
    private static byte[] S(int field,string value)=>B(field,Encoding.UTF8.GetBytes(value));
    [Fact]
    public void QQFacesKeepZeroIdsAndNoticesReadAttributedTextWithoutExecutingMarkup() {
        var face=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,6),..I(47601,0)]),2));
        Assert.True(face.Inline);Assert.Equal("0",face.EmojiId);
        var named=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,6),..S(47602,"/smile")]),2));Assert.Null(named.EmojiId);
        var notice=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,8),..S(48214,"<gtip><qq jp='Alex'/><nor txt=' invited '/><qq jp='Sam'/><nor txt=' to the group'/></gtip>")]),5));
        Assert.Equal("system",notice.Kind);Assert.Equal("Alex invited Sam to the group",notice.Text);
        var nested=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,8),..B(48210,S(48214,"<gtip><nor txt='Fixture notice'/></gtip>"))]),5));Assert.Equal("Fixture notice",nested.Text);
        var reply=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,7),..I(47702,1),..S(45101,"Fixture reply")]),9));Assert.Equal("reply",reply.Kind);Assert.Equal("Fixture reply",reply.Text);
        var poke=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,7),..B(47401,S(47413,"Alex poked Sam"))]),9));
        Assert.Equal("poke",poke.Kind);Assert.Equal("Alex poked Sam",poke.Text);
        var reaction=Assert.Single(QQMessageParser.Parse(B(40800,[..I(45002,8),..I(47702,1),..S(47705,"Alex"),..S(47714,"Sam")]),5));
        Assert.Equal("recall",reaction.Kind);Assert.Equal("Sam",reaction.ReferenceSender);Assert.Equal("Alex",reaction.ReferenceText);Assert.Equal(1,reaction.RecallRole);
    }
    [Fact]
    public void BuiltInEmojiExtractsOnlyRequestedLocalResourcesAndUsesConfigAliases() {
        string root=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            string source=Path.Combine(root,"source"),folder=Path.Combine(source,"nt_qq/nt_data/Emoji/BaseEmojiSyastems/EmojiSystermResource/0/png");Directory.CreateDirectory(folder);
            byte[] png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7l8AAAAASUVORK5CYII=");File.WriteAllBytes(Path.Combine(folder,"0.png"),png);
            string destination=Path.Combine(root,"reading");var emoji=new BuiltInEmoji(source,destination);
            Assert.Empty(emoji.Exported);emoji.UseParts("QQ",[new("sticker"){Inline=true,EmojiId="0"}]);
            Assert.Single(emoji.Exported);Assert.Equal(png,File.ReadAllBytes(Path.Combine(destination,emoji.Exported["QQ:0"])));
            emoji.AddWeChat(new(){{"face.png",png},{"newemoji-config.xml",Encoding.UTF8.GetBytes("<newemoji><emoji><cn-value>[Fixture face]</cn-value><fileName>face.png</fileName></emoji></newemoji>")}});
            emoji.UseParts("WeChat",[new("text","Hi [Fixture face]")]);Assert.Contains("WeChat:[Fixture face]",emoji.Exported.Keys);
            Assert.Null(emoji.Use("QQ:missing"));Assert.Single(Directory.GetFiles(Path.Combine(destination,"assets")));
        } finally {Directory.Delete(root,true);}
    }
    [Fact]
    public void QtEmojiReaderRejectsCyclicResourceTrees() {
        byte[] data=new byte[160];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4),2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(6),1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(64),5);Encoding.BigEndianUnicode.GetBytes("emoji").CopyTo(data,70);
        Assert.Throws<InvalidDataException>(()=>QtEmojiResources.Decode(data,0,64,100));
    }
    [Fact]
    public void QQSegmentsKeepMixedTextImagesRepliesAndStickersInOrder() {
        byte[] payload=[..B(40800,[..I(45002,1),..S(45101,"Fixture text")]),
            ..B(40800,[..I(45002,2),..S(45402,"fixture.png")]),
            ..B(40800,[..I(45002,7),..S(47421,"Quoted sender"),..B(47423,[..I(45002,1),..S(45101,"Quoted text")])]),
            ..B(40800,[..I(45002,6),..S(47602,"/smile")])];
        var parts=QQMessageParser.Parse(payload,2);Assert.Equal(new[]{"text","image","reply","sticker"},parts.Select(p=>p.Kind));
        Assert.Equal("Fixture text",parts[0].Text);Assert.Contains("fixture.png",parts[1].MediaKeys!);
        Assert.Equal("Quoted text",parts[2].ReferenceText);Assert.Equal("/smile",parts[3].Title);
        Assert.Equal("unparsed",Assert.Single(QQMessageParser.Parse([0xff,0xff],2)).Kind);
        Assert.StartsWith("Fixture text",ChatExporter.DecodeProto(payload));
    }
    [Theory]
    [InlineData(3,"file")][InlineData(4,"audio")][InlineData(5,"video")][InlineData(8,"system")]
    [InlineData(9,"redpacket")][InlineData(10,"link")][InlineData(11,"sticker")][InlineData(14,"markdown")]
    [InlineData(16,"forward")][InlineData(17,"buttons")][InlineData(21,"call")]
    public void QQSpecialSegmentsHaveTypedPlaceholders(int type,string expected) {
        Assert.Equal(expected,Assert.Single(QQMessageParser.Parse(B(40800,I(45002,(ulong)type)),0)).Kind);
    }
    [Fact]
    public void CompressedLegacyMessageAndUnknownSourceSurviveExportWithoutTruncation() {
        string root=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N")),source=Path.Combine(root,"account");Directory.CreateDirectory(source);
        try {
            string body="<msg><appmsg><type>57</type><title>Compressed reply</title><refermsg><content>Fixture quote</content></refermsg></appmsg></msg>";
            byte[] bytes=Encoding.UTF8.GetBytes(body),packed=new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(bytes.Length)];
            int length=K4os.Compression.LZ4.LZ4Codec.Encode(bytes,0,bytes.Length,packed,0,packed.Length);packed=packed[..length];
            Assert.Equal(body,ChatExporter.DecodeLegacyContent(packed));Assert.Null(ChatExporter.DecodeLegacyContent([0xff,0xff]));
            string unknown="<msg><appmsg><type>999999</type><data>"+new string('x',25000)+"</data></appmsg></msg>";
            Assert.Equal(0,sqlite3_open_v2(Path.Combine(source,"message_0.db"),out nint db,6,0));
            try{Assert.Equal(0,sqlite3_exec(db,"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent,CompressContent);"+
                "INSERT INTO MSG VALUES(49,1700000000,'fixture','preview',X'"+Convert.ToHexString(packed)+"');"+
                "INSERT INTO MSG VALUES(49,1700000001,'fixture','"+unknown+"',NULL);",0,0,0));}finally{sqlite3_close_v2(db);}
            var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
            var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
            Assert.False(result.Complete);Assert.Equal(1,result.UnsupportedMessages);
            var messages=File.ReadLines(Path.Combine(result.Directory,"messages.jsonl")).Select(l=>JsonSerializer.Deserialize<ChatMessage>(l)!).ToArray();
            Assert.Equal("reply",messages[0].Parts[0].Kind);Assert.Equal(Convert.ToBase64String(packed),messages[0].ExtraRawBase64);
            string page=File.ReadAllText(Directory.GetFiles(Path.Combine(result.Directory,"conversations"),"*-?.js").Single());
            var reading=JsonSerializer.Deserialize<ChatMessage[]>(page[page.IndexOf('[')..(page.LastIndexOf(']')+1)])!;
            Assert.Equal(unknown,reading[1].Text);Assert.Equal("unsupported",reading[1].Parts[0].Kind);
        } finally {Directory.Delete(root,true);}
    }
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint state,nint error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close_v2(nint db);
}
