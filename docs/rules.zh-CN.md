# 规则参考

规则目录是本地JSON数据，要求`version: 1`及非空的`rules`数组。未知属性、非法枚举、重复标识、非法匹配模式、负数大小和未知路径变量都会被拒绝。[JSON结构定义](../rules/rules.schema.json)供编辑器校验，引擎还会在运行时检查。

| 字段 | 含义 |
|---|---|
| `id` | 唯一标识，格式为`^[a-z][a-z0-9-]{1,63}$` |
| `name`、`description` | 默认名称和说明，名称必填 |
| `names`、`descriptions` | 可选的翻译字典，语言键如`en`和`zh-CN` |
| `action` | 删除`Delete`、复制`Copy`、移动`Move`或原生命令`Command`，默认删除 |
| `roots` | 文件规则的非空绝对根目录模式；原生命令必须为空 |
| `tool` | 原生命令类型：`NuGetHttpCache`、`NpmCache`、`PipCache`、`UvPrune` |
| `destinationRoot` | 复制与移动必填，必须是新目录，不合并已有目录 |
| `include`、`exclude` | 相对文件匹配模式，分别默认为`["**"]`与`[]` |
| `processes` | 不带`.exe`的程序名称，执行前必须退出 |
| `enabled`、`recursive` | 均默认为真；命令行仍可显式选择禁用的规则 |
| `risk` | 默认`Review`或`Low`；界面中两者均默认不勾选 |
| `minimumAgeHours` | 默认168，创建与最后写入时间均需达到要求；范围0至876000 |
| `minimumBytes`、`maximumBytes` | 包含边界的大小限制；默认最小0，无最大限制 |

根目录模式允许在单层目录名中使用`*`和`?`，不支持`**`。文件模式使用`/`或转义后的Windows分隔符：`*`匹配一层中的字符，`?`匹配单个字符，`**`跨越目录。`**/*.log`也包含根目录下的日志。排除条件优先，重叠文件归属于目录中首先匹配的规则。

支持的变量为`%LocalAppData%`、`%AppData%`、`%ProgramData%`、`%UserProfile%`、`%Windows%`、不带末尾分隔符的`%SystemDrive%`，以及配置后可用的`%BackupRoot%`。不展开任意环境变量。复制和移动目标可使用`{RootName}`代表来源目录的末级名称；多个来源解析到同一目标会报冲突，应使用不同目标或拆分规则。

下面将某个应用中超过30天的日志复制到新备份目录，并保留原件：

```json
{
  "$schema": "../rules/rules.schema.json",
  "version": 1,
  "rules": [{
    "id": "archive-example-logs",
    "name": "归档示例日志",
    "description": "复制超过30天的日志并保留原件。",
    "names": { "zh-CN": "归档示例日志" },
    "action": "Copy",
    "roots": ["%LocalAppData%\\ExampleApp\\Logs"],
    "destinationRoot": "%BackupRoot%\\ExampleApp\\Logs-archive",
    "include": ["**/*.log"],
    "exclude": ["**/*.lock"],
    "processes": ["ExampleApp"],
    "minimumAgeHours": 720,
    "risk": "Review"
  }]
}
```

复制和移动规则仅处理命中的文件及相对路径，不包含未匹配文件和空目录。完整目录请使用聊天原始备份或命令行完整目录转移。界面中的归档属于清理流程。扫描会跳过只读、系统、硬链接文件及目录链接，应先检查诊断；不能把部分选择称为完整聊天备份。

界面按标识将`rules/sets.json`中启用的规则集与`data/rules.json`本地覆盖合并。导入只更新本地覆盖，不修改内置文件。首选项控制加载哪些规则集，手动编辑后需要重新加载。目录或设置变化会使已有计划失效。清理计划默认24小时过期，绑定机器、用户与配置；转移计划也有独立的24小时时限。

## 本地设置

可在程序旁创建私有`data/settings.json`，命令行使用`--settings FILE`。规则中的`backupRoot`与聊天页的备份首选项是两个设置。默认允许已就绪的固定磁盘，`protectedRoots`默认为空。

```json
{
  "allowedDrives": ["C:\\"],
  "protectedRoots": [],
  "backupRoot": "B:\\Backups",
  "planLifetimeHours": 24,
  "maximumCandidates": 500000
}
```

示例盘符只是占位。`allowedDrives`与`protectedRoots`约束删除规则；显式选择的转移有自己的边界和移动来源保护，不构成全局磁盘访问策略。程序不会自动选择备份磁盘。

删除按文件永久执行，不递归清空整个目录。系统目录、安装目录、桌面、文档、下载、凭据、仓库内部及过宽的根目录受到保护。不会把整个软件目录视为缓存。删除前重新检查时间、身份、元数据、链接数量、进程和路径，权限失败时报告，不接管所有权。

## 原生命令

```json
{
  "version": 1,
  "rules": [{
    "id": "prune-unused-uv",
    "name": "清理未使用的uv缓存",
    "description": "也可能移除共享项目环境，执行前审核。",
    "action": "Command",
    "tool": "UvPrune",
    "roots": [],
    "risk": "Review"
  }]
}
```

原生命令必须为Review风险，无文件根目录和目标。它选择应用维护的固定操作，不接受任意Shell程序或参数。`script`、`executable`、`arguments`等未知字段会被拒绝。扫描记录程序SHA-256及必要的npm脚本哈希，不运行清理。执行前重建操作并在启动期间保持程序与脚本只读。保留输出上限为1048576个字符，非零退出、超时与取消分别记录，工具已完成的修改不会回滚。

| 类型 | 实际参数 |
|---|---|
| NuGetHttpCache | `dotnet nuget locals http-cache --clear` |
| NpmCache | `node <已安装的npm-cli.js> cache clean --force` |
| PipCache | `python -m pip cache purge` |
| UvPrune | `uv cache prune` |

这些操作覆盖工具配置的整个缓存，包括已经转移的缓存。逐文件排除、时间和大小限制不适用。uv可能移除共享项目环境。对同一缓存选择文件规则或原生命令之一，避免重复操作。本地设置可指定`toolPaths`中的dotnet、node、python、uv路径，以及默认300、范围1至3600秒的`commandTimeoutSeconds`。程序不会提权或通过Shell运行这些操作。

官方资料：[NuGet](https://learn.microsoft.com/zh-cn/dotnet/core/tools/dotnet-nuget-locals)、[npm](https://docs.npmjs.com/cli/commands/npm-cache/)、[pip](https://pip.pypa.io/en/stable/cli/pip_cache/)、[uv](https://docs.astral.sh/uv/concepts/cache/)。

## 规则集与临时归档方式

默认启用系统、浏览器和开发工具类别。桌面工具、通信软件、媒体软件和原生命令为可选类别。`builtin.json`仅包含系统类别。命令行默认加载清单的默认类别，`--catalog`只加载指定文件。

界面中的归档方式不改写规则，而是根据已扫描并勾选的同一批文件创建核验转移计划。目标按规则标识、来源哈希与新运行目录分开。不能将原生命令转换为文件归档。保存的扫描计划包含候选文件，重新载入后仍须选择和审核处理方式。文件规则继续禁止目录链接；转移缓存应在私有覆盖中明确填写已核验的物理缓存末级目录。

## 原生缓存容量

原生命令可以配置`measurement`，默认为`{"mode":"Tool"}`。程序运行固定只读查询，取得缓存目录并扫描逻辑大小：`dotnet nuget locals http-cache --list`、`node <npm-cli.js> config get cache`、`python -m pip cache dir`或`uv cache dir`。npm只测量与清理范围对应的`_cacache`。查询最多30秒，限制输出且支持取消。不接受规则指定的任意程序、Shell表达式和输出解析代码。不存在的缓存为零，无法访问、含链接、不受支持或超时则为未知。

已经核验的物理转存目录可以显式指定：

```json
"measurement": {
  "mode": "Directories",
  "roots": ["%LocalAppData%\\ExampleApp\\Cache"]
}
```

测量目录只读，使用相同变量与匹配机制，拒绝链接和整盘。文件规则不能配置测量。重叠根目录只计算一次，测量不授权额外清理路径，清理范围仍由原工具决定。

执行前后重新测量，结果记录`beforeBytes`、`afterBytes`、`freedBytes`。它们是逻辑大小差，若同时有写入可能为负，不保证等于文件系统分配空间。未知或部分扫描不声明释放字节。文件删除使用实际删除文件的大小，复制归档释放源空间为零。规则字段变化会改变目录哈希，旧计划需要重新扫描。

仅pip支持`{"mode":"ToolReport"}`，调用`python -m pip cache info`解析HTTP和wheel容量。原生输出有舍入，预览用`~`标记。执行仍使用前后目录的精确测量，不用舍入值计算释放量；未知报告格式保留未知。其他工具使用默认目录查询或显式目录测量。
