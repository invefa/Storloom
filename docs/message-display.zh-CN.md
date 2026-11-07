# 消息展示范围

消息展示模型属于派生数据。消息全文及能够取得的原始二进制保存在JSONL中，也能在阅读版折叠的原始内容区域中查看。XML、JSON和Protobuf不会作为HTML或脚本执行。较大的源码节点只在展开时创建，阅读版不再将文本截断为20000个字符。

| 类型 | 微信 | NTQQ |
|---|---|---|
| 文本与引用 | 普通文本、应用类型57中的被引用人和内容 | 按顺序展示文本与回复段，使用能够取得的引用信息 |
| 媒体 | 图片、视频、语音和动画表情 | 图片、视频、语音、普通与商城表情段 |
| 文件 | 显示能够取得的附件名 | 文件卡片与文件名 |
| 支付 | 转账金额与备注、红包说明 | 钱包或红包占位，不猜测金额和交易状态 |
| 卡片 | 链接、小程序、音乐、视频号与直播 | 可识别的结构化JSON卡片，其余使用类型占位 |
| 其他 | 名片、位置、合并转发、系统通知与通话 | 系统通知、通话、转发、礼物、交互与格式化消息 |

仅解释有明确含义的字段。转账消息是历史记录，不代表交易当前的结算状态。联系人资料缺失时使用标识符。未知元素保留占位和源码。本地资源缺失与已找到但格式不受支持使用不同提示。

媒体只在本地进行明确匹配，不从远程CDN下载。文本表情和表情名称保持可读，不附带分发专有表情包。

旧版微信的`CompressContent`可能使用LZ4压缩，解码上限为16 MiB，原始压缩字节仍然保留。新版消息继续支持Zstandard。NTQQ根据数据库外层类型及有序的`40800`、`45002`段解析，只将已知文本字段解释为UTF-8。Protobuf解析限制字段数量、数据长度和递归深度。

具备可验证密钥时，支持的微信V1/V2图片容器现可离线解码。WxAM/HEVC图片、仅有资源索引的引用、SILK语音、未知数据库结构及尚未支持的结构化字段仍需后续适配。显示卡片或占位不表示每个字段都已解析。`unsupportedMessages`与`unresolvedMedia`会影响阅读版完整性，原始备份核验及保留源文件的行为不变。

已有阅读版不会自动被覆盖。可以从已核验的原始备份重新导出以使用新解析；备份完整且具备必要密钥时，不需要原始账号目录或运行客户端。

## 调研资料

解析器独立使用C#实现。以下协议资料具有版本限制，不是覆盖所有客户端的官方规范：

- [QQ数据库消息体字段](https://github.com/QQBackup/nt_msg_db_util/blob/master/db_docs/c2c_msg_table/40800.md)与[共享结构定义](https://github.com/QQBackup/nt_msg_db_util/blob/master/msgdb/proto/c2c_40800.proto)。
- [QQ外层类型与消息元素对照](https://github.com/shuakami/qq-chat-exporter/issues/167)。运行时元素枚举和数据库字段编号属于不同编号体系。
- [微信消息与应用类型](https://pkg.go.dev/github.com/ysy950803/chatlog/internal/model)。
- [微信XML示例](https://github.com/lefex/iWeChat/blob/master/MESSAGE.md)。

## 引用、转发与通知

原始内容入口放在消息气泡下方，使用较小文字；展开后在独立的辅助气泡中展示。系统通知、邀请和戳一戳使用居中通知布局，不再虚构发件人。微信通知模板会代入能够取得的成员名字。微信内嵌合并转发记录与NTQQ转发缓存可显示其中包含的消息、发件人和时间；只有服务器资源标识的卡片保留内容不可用提示。转发解析限制嵌套深度与条目数量。

引用可跳转至同一会话中的原消息，支持跨页。微信服务端消息标识与NTQQ群内序号按字符串建立索引，保留64位整数精度。私聊引用中的候选消息标识只在当前会话存在精确目标时使用。原消息不在备份中时会提示，不猜测跳转位置。媒体优先使用完整文件；只能显示缩略图时明确标注，导出不会被误判为完整。

- [微信媒体格式与离线图片密钥派生](https://github.com/qiaodogbear/ChatTrace/blob/f9fcdb42e8fb99a527c0bcd7d6ff7ea4bc891563/src/chattrace/service/image_key.py)。
- [NTQQ群引用关联](https://github.com/QQBackup/nt_msg_db_util/blob/master/db_docs/group_msg_table/summary.md)与[转发缓存字段](https://github.com/QQBackup/nt_msg_db_util/blob/master/msgdb/proto/group_40900.proto)。
