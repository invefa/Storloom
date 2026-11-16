# Storloom.Wpf

面向 .NET 10 的独立 Windows WPF 组件库。仅依赖 Windows 桌面框架，不引用 Storloom 主程序、归档引擎、文案服务、WPF-UI 或 WebView2。

## 在其他软件中使用

添加对 `Storloom.Wpf.csproj` 的项目引用，或生成本地 NuGet 包：

```powershell
dotnet pack src/Storloom.Wpf/Storloom.Wpf.csproj -c Release -o artifacts/packages
```

创建 WPF `Application` 后初始化：

```csharp
using Storloom.Wpf;
ComponentTheme.Apply(ComponentPalette.Dark, reduceMotion: false);
var banner = new StatusBanner("正在加载", StatusTone.Busy);
// 异步工作完成后：
banner.Show("完成", StatusTone.Success);
```

`StatusBanner` 可在 XAML 中使用；`Text`、`Tone`、`ReduceMotion` 支持绑定。加载圈只在忙碌且可见时转动，完成、隐藏、卸载或减少动画时停止。耗时任务应异步执行，避免阻塞界面线程。

## 可复用组件

- `AdaptiveTable`：内容填充后自动测量并占满横向空间，优先压缩最长列；手动调宽后允许横向滚动。`FitToViewport()` 恢复自动布局，`UseManualWidths()` 暂停自动布局。
- `ISelectableRow` / `TableSelection`：针对当前筛选视图管理全不选、全选和部分选择，并提供反选。先用 `ComponentVisuals.AddSelectionColumn(table)` 添加选择列；全选文案由调用方传入，列菜单通过 `TableInteraction.Configure(table, translate)` 翻译。
- `ControlFeedback`：通用鼠标、键盘反馈，支持减少动画。
- `ComponentVisuals.Icon`：使用矢量路径，不依赖字体字符。
- 资源字典：按钮、文本和密码输入、三态选择框、表格、标签页、展开器、滚动条及菜单。部分选择使用内部圆角方块。

状态文案全部由调用方提供；行模型只需实现 `ISelectableRow`。在界面线程上更新绑定数据和控件状态；每个表格使用一个 `TableSelection`。

## 主题

`ComponentTheme.Apply` 提供亮色、暗色、OneDark，并在应用范围安装隐式样式，请按需启用。之后可覆盖 `Canvas`、`Surface`、`Ink`、`Muted`、`Line`、`Accent`、`OnAccent`、`Selection`、`Hover`、`Danger` 等画刷资源，以及文字、滚动条相关资源。`ComponentTheme.SetReduceMotion(true)` 会同步现有状态组件；单个组件也可以设置 `ReduceMotion`。组件不读取业务配置或客户端主题。

已有主题系统时，依次合并 `/Storloom.Wpf;component/Styles.xaml` 和 `/Storloom.Wpf;component/CompactControls.xaml` 并提供配色资源即可，Storloom 主程序采用这种方式。原生标题栏由宿主负责。

## 独立展示程序

```powershell
dotnet run --project samples/ComponentDemo
dotnet run --project samples/ComponentDemo -- --smoke
```

展示程序只引用新库，可体验换肤、输入、三态批量选择、表格自适应及加载动画。冒烟模式验证依赖隔离、选择状态、动画实际前进和减少动画。

MIT 许可，与仓库根目录一致。英文说明见 [README.md](README.md)。
