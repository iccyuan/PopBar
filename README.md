# PopBar

Windows 上的 [PopClip](https://pilotmoon.com/popclip/)：选中文本后立刻在鼠标旁弹出操作栏（复制、搜索、翻译面板、AI 解释、打开链接、大小写转换、发送到应用……），另有屏幕 OCR 取词。

C# / WPF / .NET 10，弹窗不抢焦点，常驻内存约 35-75 MB。操作条与面板为高斯模糊毛玻璃（弹出前截取底图自行模糊，不依赖系统亚克力，任何 Windows 版本一致），设置窗口为 Mica（Windows 11 22H2+），主题默认跟随系统，不透明度可调。功能以插件形式组织，内置插件和第三方插件走同一套接口（`PopBar.Sdk`）。

## 安装

到 [Releases](https://github.com/iccyuan/PopBar/releases) 下载：

- `PopBar-x.y.z-win-x64.exe`：体积小，需要先装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。
- `PopBar-x.y.z-win-x64-standalone.zip`：解压即用，无需运行时。

需要 Windows 10 2004（19041）及以上。首次运行会打开设置；之后常驻托盘，右键托盘图标是菜单，双击打开设置。

## 构建

```powershell
# 需要 .NET 10 SDK（winget install Microsoft.DotNet.SDK.10）
dotnet build PopBar.slnx -c Release
# 单文件发布（依赖系统 .NET 10 桌面运行时）
dotnet publish PopBar\PopBar.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
# 自包含发布（无需安装运行时）
dotnet publish PopBar\PopBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-sc
```

NuGet 依赖：`Interop.UIAutomationClient`（UIA 取词）、`Anthropic`（官方 SDK，AI 功能的 Anthropic 协议）、`ZXing.Net`（二维码）。目标框架 `net10.0-windows10.0.19041.0`，为的是用系统内置的 `Windows.Media.Ocr`。

发版：推送 `v*` 标签（`git tag v0.1.1 && git push origin v0.1.1`），GitHub Actions（`.github/workflows/release.yml`）在 windows-latest 上发布上述两个包并创建 Release，版本号取自标签。

## 工作原理

```
WH_MOUSE_LL / WH_KEYBOARD_LL  ──► SelectionEngine（钩子线程里只做判定）
   拖选 ≥10px / 多击 / Shift+点击 ──► 延迟 60ms ──► 工作线程 (STA)
                                                    │
                                 ┌──────────────────┴───────────────────┐
                                 │ 1. UIA TextPattern.GetSelection()    │  无副作用：Edge/Chrome/Word/记事本/Windows Terminal
                                 │ 2. 剪贴板回退：备份全部格式 → Ctrl+C  │  仅在 I 型光标或双击时；终端类程序永不使用
                                 │    → GetClipboardSequenceNumber 确认  │
                                 │    → 读文本 → 恢复备份                │
                                 └──────────────────┬───────────────────┘
                                                    ▼
                    PopupWindow（WS_EX_NOACTIVATE，物理像素定位，多屏多 DPI 钳位，先量后显）
                                                    │ 点按钮
                                 内置动作 / 插件动作 ──► PanelWindow（翻译、AI、OCR 结果等浮动面板）
```

- **不抢焦点**：操作栏加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。
- **替换粘贴**：备份剪贴板 → 写入结果 → `AttachThreadInput` + `SetForegroundWindow` 还焦点给源窗口 → Ctrl+V → 还原剪贴板。
- **钩子**只忽略 PopBar 自己注入（`dwExtraInfo` 打标）的按键，远程桌面、AutoHotkey 等注入输入照常触发。
- **消失时机**：操作栏在外部点击、滚轮、任意按键后消失；面板在外部点击时关闭，可用图钉固定。
- **显示方式**：默认选中即弹；可改为「按键后显示」——选中时按住 Ctrl/Alt/Win，或选中后 4 秒内按一下该键才出现，适合不想被频繁打扰的场景。
- **连击**：多击按 Windows 的规则判定（按下到按下的间隔与位置）；双击后接着三击、四击时操作条原地更新而不是关了再弹，连击中某一击没选到文字会等一个双击时间再收起。
- **操作条长度**：同一插件的多个动作（如多个发送目标）合并成一个按钮、点开原地展开；超过「最多显示按钮数」（默认 7）的按钮收进末尾的「···」里。
- **联网**：共享 `HttpClient` 走系统代理，并按地址族建立连接（某些 VPN / TUN 环境下 .NET 默认的双栈 IPv6 套接字会被拒绝，报 10013）。

## 内置插件

| 插件 | 做什么 | 配置 |
|---|---|---|
| 翻译 `translate` | 浮动面板内直接显示译文，标题旁标明服务商 / 模型；Google（免费，走系统代理）或 AI 模型两种后端；源语言按书写系统本地检测（中 / 日 / 韩 / 俄 / 阿 / 泰 / 英），一律译成目标语言，原文已是目标语言时改译备用语言 | 服务、流式输出、目标 / 备用语言（下拉） |
| AI 助手 `ai` | 解释 / 总结 / 润色，输出到面板（可关流式）；每个提示词一个按钮，可自定义系统提示和模板 | 提示词列表、effort、最大 tokens、流式 |
| 屏幕 OCR `ocr` | 热键（默认 Ctrl+Alt+O）或托盘菜单 → 框选屏幕区域 → Windows 内置 OCR → 弹出操作栏 + 可编辑结果面板 | 热键、识别语言、是否显示面板 |
| 截图 `screenshot` | 对标 Snipaste：F1 冻结屏幕截图，悬停自动识别窗口、单击选中，拖动框选，八向手柄调整、方向键微调；放大镜取色（C 复制色值）；矩形 / 椭圆 / 箭头 / 画笔 / 马赛克 / 文字标注（R / E / A / P / M / T 切换），撤销；Enter·双击复制、Ctrl+S 保存、Ctrl+T 贴图，所有键可改。**长截图**：自动滚动目标窗口并按行哈希拼接（识别吸顶 / 吸底栏、忽略滚动条）；**录制 GIF**：流式编码，只存变化区域，可暂停、可录鼠标。F3 把剪贴板图片或文字贴到屏幕：拖动、滚轮缩放、Ctrl+滚轮透明度、双击销毁 | 热键、保存目录、放大镜、窗口识别、GIF 帧率 / 时长 / 鼠标 |
| 发送到应用 `sendto` | 把选中内容粘贴进指定进程的窗口（找不到可自动启动）、带参数启动程序、或打开 URL 协议（如 `obsidian://`）；可同时开启多个目标，默认合并成一个「发送到」按钮、点开在操作条里原地展开选择（也可关掉合并改为平铺），增删即时生效 | 目标列表 |
| 二维码 `qr` | 选中内容生成二维码（复制图片 / 保存，按钮默认关闭）；框选（Ctrl+Alt+Q）或整屏扫描识别二维码 / 条形码，结果弹出操作条 | 两个热键 |
| 编码工具 `codec` | 选中内容符合格式时才出现：解码（Base64 / URL / Unicode / HTML 实体）、JSON 格式化（语法高亮）、JWT 解析、时间戳互转；另有可选的「编码」面板（各种编码 + MD5/SHA 哈希） | — |
| 便签 `notes` | 把选中内容贴成桌面便签（按钮默认关闭）：可编辑、换颜色、置顶、Ctrl+滚轮透明度，重启后自动恢复 | 新建 / 显示全部热键 |
| 屏幕翻译 `screentranslate` | Ctrl+Alt+T 框选区域 → OCR → 按段落翻译 → 用取样的背景色 / 文字色把译文覆盖在原位置；按住空格看原文 | 热键、看原文 / 复制键 |

AI 模型在 **设置 → 服务** 统一配置：协议 `anthropic`（官方 SDK，默认模型 `claude-opus-5-5`）或 `openai`（任何 OpenAI 兼容的 `/chat/completions`：LiteLLM、Ollama、DeepSeek……）；页面上标出所选协议支持的能力（流式、系统提示词、思考深度、模型列表），可获取模型列表、测试连接。

## 设置

- **保存**：改动先留在窗口里，底部出现「有未保存的更改」，点保存或 Ctrl+S 才写入，取消恢复到上次保存；插件卡片里的设置同样如此。外观项（主题、按钮文字标签、最多显示按钮数、毛玻璃不透明度）例外，改了即时生效，主题切换整页渐变。
- **动作**：按顺序显示在操作条上，拖动排序；同一插件的多个动作归为一组（可收起、整组拖动、组内排序）。
- **插件**：每个插件一张卡片，开关 + 齿轮展开它自己的设置；第三方插件目录在页面顶部。
- **托盘菜单**：启用 / 暂停、各插件登记的入口（截图、贴图、OCR、二维码、屏幕翻译、便签）、设置、退出。

命令行：`PopBar.exe --settings` 打开设置；`PopBar.exe --run ocr:capture` 触发 OCR 框选（可绑到任何快捷键工具）。

## 写一个插件

1. 新建类库，引用 `PopBar.Sdk`（见 `samples/HelloPlugin`），实现 `IPopBarPlugin`：

```csharp
public sealed class HelloPlugin : IPopBarPlugin
{
    public string Id => "hello";                 // 动作 id 会变成 "hello:count"
    public string Name => "Hello";
    public string Description => "…";
    public Version Version => new(1, 0, 0);

    IPluginHost _host = null!;
    public void Initialize(IPluginHost host) => _host = host;

    public IReadOnlyList<PluginAction> GetActions() => new[]
    {
        new PluginAction
        {
            Id = "count", Label = "字数", Icon = "hash",
            IsApplicable = ctx => ctx.Text.Length > 0,          // 可选：决定按钮是否出现
            Execute = (ctx, ct) =>                               // 工作线程；UI 一律走 host
            {
                var panel = _host.ShowPanel(new PanelOptions { Title = "字数", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY });
                panel.SetText($"{ctx.Text.Length} 字符");
                return Task.CompletedTask;
            },
        },
    };

    public FrameworkElement? CreateSettingsView() => Form.Card(Form.Row("选项", Form.Toggle(true)));
    public void Dispose() { }
}
```

2. 把编译出的 DLL 放到 `%AppData%\PopBar\plugins\<任意目录名>\`，重启 PopBar。新动作会自动出现在「动作」页，顺序和启用状态由用户控制。

`IPluginHost` 提供：日志、每插件 JSON 存储、共享 `HttpClient`、`ILlmService`（流式）、浮动面板 `ShowPanel`（标题标签 `Badge`、`SetError` 醒目报错、进度指示）、`ShowPopup`（为任意文本弹操作栏）、`CaptureScreenRegionAsync`（框选截图）、剪贴板 / 粘贴 / 激活窗口、全局热键、托盘菜单项（可带图标）、`RunActionAsync`（调用其他动作，如 `"translate:panel"`）、`RefreshActions`（动作列表变化后即时同步到操作条）、`Services`（插件间共享能力：内置翻译提供 `ITextTranslator`，OCR 提供带行坐标的 `IOcrEngine`，按类型取用，不必互相引用）、`Settings`（设置草稿，见下表）。`PluginAction` 可声明 `Description`、`EnabledByDefault`、`Group`（同组动作在操作条上合并成一个按钮）。`IHostUi` 提供和设置页同款的控件与对话框（按键录制、运行中程序、图标、文件 / 文件夹 / 保存对话框、确认框）；`PopBar.Sdk.Ui.Form` 是表单助手，`Ui.CodeView.Json` 是带高亮的只读代码块，`Ui.Shortcut` 解析用户配置的窗口内快捷键。

第三方插件在独立的 `AssemblyLoadContext` 里加载，只与宿主共享 `PopBar.Sdk`；其他依赖随 DLL 一起放。

## 架构分层

```
PopBar.Sdk   契约层：IPopBarPlugin / IPluginHost / IPanel / ILlmService + ILlmProvider / IHostUi / Ui.Form
   ▲ 只依赖 WPF 基础类型，不依赖主程序
Core         引擎与服务：SelectionEngine（依赖 IPopupPresenter / IPanelRegistry 接口，不引用窗口类）、
             TextCapture（ITextCaptureStrategy 策略链：UIA → 剪贴板）、LlmService（ILlmProvider 注册表）、
             ThemeManager、InputHook、ClipboardBackup、InputSender
Plugins      PluginManager（发现/加载/动作同步）、PluginHost + HostUi（IPluginHost / IHostUi 实现）、
             Builtin/*（内置插件，与第三方同契约）
UI           WPF 实现：PopupWindow（实现 IPopupPresenter）、PanelWindow、SettingsWindow、EditDialog、
             IconImage / KeyCaptureBox / InfoTip 等控件
App          组合根：装配以上各层、托盘、单实例、热键
```

扩展点一览：

| 要扩展什么 | 实现接口 | 注册方式 |
|---|---|---|
| 操作条按钮 / 面板功能 | `IPopBarPlugin` + `PluginAction` | DLL 放 plugins 目录，或内置列表 |
| AI 模型提供商 | `ILlmProvider` | `host.Llm.RegisterProvider(...)`（内置：Anthropic、OpenAI 兼容、Ollama、DeepSeek） |
| 取词方式 | `ITextCaptureStrategy` | `TextCapture` 构造时传入策略链 |
| 弹条/面板呈现 | `IPopupPresenter` / `IPanelRegistry` | `SelectionEngine` 构造注入 |
| 插件间共享能力 | `IServiceRegistry`（契约定义在 SDK，如 `ITextTranslator`、`IOcrEngine`） | `host.Services.Provide<T>(impl)` / `Get<T>()` |
| 插件设置页控件 | `IHostUi`（按键/快捷键录制、运行中程序、图标选择、文件选择、编辑对话框）+ `Form.ListRow` | 插件在 `CreateSettingsView` 里直接用 |
| 设置保存后生效 | `ISettingsSession`（`host.Settings`）+ `SettingsDraft.Clone` | 设置视图改草稿并调 `Changed()`，`OnApply` 里写回、注册热键；用户点「保存」才执行，取消即丢弃 |
| 模型后端能力 | `ILlmProvider.Features`（`LlmFeatures`：流式、系统提示词、思考深度、模型列表） | 「服务」页按所选协议展示 |

## 目录

```
PopBar.Sdk/              插件接口（IPopBarPlugin / IPluginHost / IPanel / ILlmService / Ui.Form）
PopBar/
  App.xaml(.cs)          入口：单实例、托盘、热键、插件装配、--run 命令通道
  Native/NativeMethods   全部 P/Invoke
  Core/                  InputHook、SelectionEngine、TextCapture、ClipboardBackup、InputSender、LlmService
  Actions/               内置动作定义、匹配规则、执行器
  Plugins/               PluginManager（发现/加载/动作同步）、PluginHost、PluginStorage、HotkeyManager
  Plugins/Builtin/       Translate / Ai / Ocr / SendTo / Qr / Codec / Notes / ScreenTranslate / Screenshot（含长截图、GIF 编码器）
  UI/                    PopupWindow（操作栏）、PanelWindow（浮动面板）、RegionCaptureWindow、SettingsWindow、ActionEditWindow、
                         FrostedGlass（毛玻璃）、LogoArt（矢量 logo，--export-icon 导出 ico）、Icons
  Themes/Modern.xaml     设置页样式（主题调色板在 Core/ThemeManager，切换时画刷原地渐变）
samples/HelloPlugin/     最小第三方插件示例
tools/                   图标生成（make-icon.ps1）、取词弹条测试（test-popup.ps1）
.github/workflows/       release.yml：打标签自动发布
```

## 已知边界

- 旧式 Win32 控件、部分 Electron/游戏类窗口不暴露 UIA 选区，需剪贴板回退（默认只在文本光标下或双击时启用）。
- 密码框不取词；以管理员运行的程序，PopBar 若非管理员则无法向其发送按键。
- Windows OCR 的识别质量取决于已安装的语言包（设置 → 时间和语言 → 语言 → 该语言的“光学字符识别”），只装中文包时英文识别偏弱。
- Google 翻译在国内需要代理（走系统代理）。
- 字体为 Microsoft YaHei UI，常规字重（该字体没有半粗，SemiBold 会落成粗体）；文字关闭了 hinting 以获得和 DirectWrite 程序一致的平滑中文。
