# PopBar

Windows 上的 [PopClip](https://pilotmoon.com/popclip/)：选中文本，鼠标旁立刻弹出操作栏（复制、搜索、翻译、AI 解释、打开链接、发送到应用……），另有屏幕 OCR、截图、屏幕翻译。

C# / WPF / .NET 10。弹窗不抢焦点，常驻约 35-75 MB。操作栏与面板为毛玻璃（自行模糊底图，不依赖系统亚克力），设置窗口为 Mica，主题跟随系统。功能全部是插件，内置与第三方走同一套接口 `PopBar.Sdk`。

## 安装

[Releases](https://github.com/iccyuan/PopBar/releases) 里两个包：

- `PopBar-x.y.z-win-x64.exe`：小，需先装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。
- `PopBar-x.y.z-win-x64-standalone.zip`：解压即用。

需要 Windows 10 2004 及以上。首次运行打开设置，之后常驻托盘：右键菜单，双击设置，左键动作可在设置里选（打开设置、菜单，或截图、OCR、屏幕翻译等插件托盘功能）。

## 构建与发版

```powershell
dotnet build PopBar.slnx -c Release          # 需要 .NET 10 SDK
dotnet test PopBar.Tests                     # 单元测试：匹配规则、文字转换、快捷键解析、配置迁移、保存合并等
dotnet publish PopBar\PopBar.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

推送 `v*` 标签即自动发版（`.github/workflows/release.yml`），版本号取自标签；本地构建的版本号在 `Directory.Build.props`。

## 工作原理

```
鼠标/键盘低级钩子 ──► 拖选 / 多击 / Shift+点击 ──► 工作线程取词
                                                  直接读控件，无副作用、不碰剪贴板：
                                                  1. 控件消息：Scintilla（Notepad++ 等）、Edit、RichEdit
                                                  2. UIA TextPattern
                                                  3. Java Access Bridge（JetBrains IDE 等 Java 程序）
                                                  4. IAccessible2（Firefox、LibreOffice、Qt 5 等）
                                                  能读的一步说「没选中」就到此为止；都读不了才
                                                  5. 剪贴板兜底：备份 → Ctrl+C → 读取 → 还原（仅 I 型光标或双击时）
                                                       ▼
                                      操作栏（WS_EX_NOACTIVATE，多屏多 DPI 钳位）──► 点按钮 ──► 浮动面板
```

- 操作栏不抢焦点；替换粘贴时备份剪贴板、把焦点还给源窗口再 Ctrl+V。
- 外部点击、滚轮、按键后操作栏消失；面板可用图钉固定。
- 默认「按键后显示」：选中时按住，或 4 秒内单独轻按一下显示键（默认 Alt，可改；不会激活菜单栏）才弹出，组合键（Ctrl+C、Shift+字母）不会触发；可改为「选中后自动显示」。
- 连击按 Windows 规则判定，三击、四击原地更新操作栏。插件声明了分组的多个动作会收成一个按钮，点开在按钮下方弹出列表再选，操作条本身不变；超出「最多显示按钮数」的收进「···」，同样下拉选择。
- 联网走系统代理，按地址族建连（规避某些 VPN / TUN 下双栈套接字被拒的 10013）。

## 内置插件

| 插件 | 做什么 |
|---|---|
| 翻译 | 面板里直接出译文，标明服务商；Google 或 AI 模型（可选服务商与模型）；本地检测源语言，原文已是目标语言时改译备用语言 |
| AI 助手 | 解释 / 总结 / 润色，流式输出；先选服务商再选模型；深度思考与深度默认跟随「服务」页，插件内可单独覆盖，深度档位由服务商 / 模型决定，推理过程显示在答案上方、可收起；提示词可自定义，每条一个按钮 |
| 屏幕 OCR | Ctrl+Alt+O 框选 → Windows 内置 OCR → 操作栏 + 可编辑结果 |
| 截图 | 对标 Snipaste：Ctrl+Alt+A 截图，自动识别窗口，标注（矩形 / 椭圆 / 箭头 / 画笔 / 马赛克 / 文字），取色；长截图自动滚动拼接；录 GIF；F3 贴图 |
| 发送到应用 | 把选中内容粘到指定程序、带参数启动程序或打开 URL 协议；多个目标合并成一个按钮 |
| 二维码 | 生成二维码；框选或整屏识别二维码 / 条形码 |
| 编码工具 | 内容符合格式才出现：Base64 / URL / Unicode / HTML 解码、JSON 格式化、JWT 解析、时间戳互转；另有编码 + 哈希面板 |
| 便签 | 选中内容贴成桌面便签，可编辑、换色、置顶，重启恢复 |
| 屏幕翻译 | Ctrl+Alt+T 框选 → OCR → 翻译 → 译文覆盖在原位置，按住空格看原文 |

AI 模型在「设置 → 服务」配置：`anthropic`（官方 SDK）或 `openai`（任何兼容 `/chat/completions` 的服务）。那里的模型只是默认值，AI 助手、翻译等插件可以在各自设置里单独选模型（同一接口与密钥）。热键、保存目录等都在各插件设置里。

## 设置

- 改动需点保存（Ctrl+S）才生效，取消恢复；主题、按钮文字、按钮数、毛玻璃不透明度即时生效。
- 「动作」页拖动排序，同插件的动作成组；「插件」页每个插件一张卡片，齿轮展开其设置。
- 命令行：`--settings` 打开设置，`--run ocr:capture` 触发某个动作（可绑到任意快捷键工具），`--install <插件 .zip / .dll 或 GitHub 仓库地址>` 安装插件，`--dev-plugin <构建目录>` 直接加载开发中的插件（见下）。

## 写一个插件

新建类库引用 `PopBar.Sdk`，实现 `IPopBarPlugin`：声明 `Id` / `Name`，在 `GetActions()` 里返回操作条按钮（`IsApplicable` 决定是否显示、`Execute` 决定点了做什么），需要的话用 `CreateSettingsView()` 提供设置页。内置插件和第三方插件用同一套接口。

```csharp
public sealed class WordCountPlugin : IPopBarPlugin
{
    IPluginHost _host = null!;
    public string Id => "wordcount";
    public string Name => "字数统计";
    public string Description => "统计选中文本的字数。";
    public Version Version => new(1, 0, 0);
    public void Initialize(IPluginHost host) => _host = host;

    public IReadOnlyList<PluginAction> GetActions() =>
    [
        new PluginAction
        {
            Id = "count", Label = "字数", Icon = "hash",
            IsApplicable = ctx => ctx.Text.Length > 0,
            Execute = async (ctx, ct) =>
            {
                var panel = await _host.ShowPanelAsync(new PanelOptions { Title = "字数", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY });
                panel.SetText($"{ctx.Text.Length} 个字符");
            },
        },
    ];

    public void Dispose() { }
}
```

开发时用 `PopBar.exe --dev-plugin <构建目录>` 直接加载，再执行一次即重新加载；发布后用户在「插件」页从 GitHub 仓库或本地 `.zip` / `.dll` 安装。完整示例见 `plugins/Hello`。

**完整的开发文档见 [docs/plugin-development.md](docs/plugin-development.md)**：结果面板、设置页与草稿、宿主能力、调用 AI 模型、网络与代理、插件间共享服务、线程规则、生命周期、调试、打包发布、SDK 版本兼容。

## 分层

```
PopBar.Sdk          插件契约：IPopBarPlugin / IPluginHost / IPanel / ILlmService / IHostUi（IHostDialogs / IHostInputs / IHostVisuals / ILlmUi）/ Ui.Form / Ui.ScreenWindows
PopBar/             核心程序，不含任何具体插件
  Core              SelectionEngine、TextCapture（控件消息 → UIA → Java Access Bridge → IAccessible2 → 剪贴板兜底的策略链）、LlmService、ThemeManager、InputHook
  Plugins           插件宿主：PluginManager（加载 / 热卸载）、PluginHost、PluginInstaller
  UI                PopupWindow、PanelWindow、FrostedGlass、Icons；SettingsWindow 只管导航与保存，各页在 UI/Settings
  App               组合根：托盘、单实例、热键、注册内置插件
plugins/            插件，每个一个项目：内置的 Translate、Ai、Ocr、Screenshot、SendTo、Qr、Codec、Notes、ScreenTranslate，
                    以及第三方插件示例 Hello（不随程序发布）
```

内置插件和第三方插件走同一套契约：每个插件项目只引用 `PopBar.Sdk`（`plugins/Directory.Build.props`），编译器保证它碰不到核心代码；插件之间也互不引用，要共享能力就用 `host.Services`（如屏幕翻译用 OCR 和翻译插件提供的 `IOcrEngine`、`ITextTranslator`）。主程序只在组合根里把它们作为内置插件注册、随程序一起发布。

扩展点：操作栏按钮用 `IPopBarPlugin`；AI 提供商用 `ILlmProvider`（`host.Llm.RegisterProvider`），兼容 `/chat/completions` 的服务直接用 `new OpenAiCompatibleProvider(options, dialect)`，只在 `IChatCompletionsDialect` 里写与众不同的部分（思考参数、token 上限字段、推理字段）；「服务」页可配置多家服务商并指定默认，`LlmRequest.Provider` 选用其中一家、`Thinking` / `Effort` 留空即跟随该服务的深度思考设置，深度档位由 `ILlmProvider.EffortLevels` 按模型给出；取词方式用 `ITextCaptureStrategy`；插件间能力用 `host.Services.Provide<T>()`。

## 已知边界

- 以上几种都读不到的窗口（自绘界面、游戏、部分 Electron 应用）才走剪贴板兜底；日志里 `via Clipboard from xxx.exe` 列出这些程序。
- Java 程序靠 Access Bridge：「设置 → 通用 → Java 程序直接取词」（默认开启，等同 `jabswitch -enable`）写入 `~/.accessibility.properties`，对之后启动的 Java 程序生效。
- 密码框不取词；管理员权限的程序收不到非管理员 PopBar 的按键。
- Windows OCR 质量取决于已装语言包；Google 翻译在国内需要代理。
