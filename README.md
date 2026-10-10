# PopBar

Windows 上的 [PopClip](https://pilotmoon.com/popclip/)：选中文本后立刻在鼠标旁弹出操作栏（复制、搜索、翻译面板、AI 解释、打开链接、大小写转换、发送到应用……），另有屏幕 OCR 取词。

C# / WPF / .NET 10，弹窗不抢焦点，常驻内存约 35-75 MB。功能以插件形式组织，内置插件和第三方插件走同一套接口（`PopBar.Sdk`）。

## 构建

```powershell
# 需要 .NET 10 SDK（winget install Microsoft.DotNet.SDK.10）
dotnet build PopBar.slnx -c Release
# 单文件发布（依赖系统 .NET 10 桌面运行时）
dotnet publish PopBar\PopBar.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
# 自包含发布（无需安装运行时）
dotnet publish PopBar\PopBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-sc
```

NuGet 依赖：`Interop.UIAutomationClient`（UIA 取词）、`Anthropic`（官方 SDK，AI 功能的 Anthropic 协议）。目标框架 `net10.0-windows10.0.19041.0`，为的是用系统内置的 `Windows.Media.Ocr`。

## 工作原理

```
WH_MOUSE_LL / WH_KEYBOARD_LL  ──► SelectionEngine（钩子线程里只做判定）
   拖选 ≥5px / 双击 / Shift+点击 ──► 延迟 60ms ──► 工作线程 (STA)
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

## 内置插件

| 插件 | 做什么 | 配置 |
|---|---|---|
| 翻译 `translate` | 浮动面板内直接显示译文；Google（免费，需代理）/ 微软（免费，直连）/ AI 模型三种后端；源语言已是目标语言时自动改译备用语言 | 目标语言、备用语言、服务 |
| AI 助手 `ai` | 解释 / 总结 / 润色，流式输出到面板；每个提示词一个按钮，可自定义系统提示和模板 | 提示词列表、effort、最大 tokens |
| 屏幕 OCR `ocr` | 热键（默认 Ctrl+Alt+O）或托盘菜单 → 框选屏幕区域 → Windows 内置 OCR → 弹出操作栏 + 可编辑结果面板 | 热键、识别语言、是否显示面板 |
| 发送到应用 `sendto` | 把选中内容粘贴进指定进程的窗口（找不到可自动启动）、带参数启动程序、或打开 URL 协议（如 `obsidian://`） | 目标列表 |

AI 模型在 **设置 → 插件 → AI 模型服务** 统一配置：协议 `anthropic`（官方 SDK，默认模型 `claude-opus-5-5`）或 `openai`（任何 OpenAI 兼容的 `/chat/completions`：LiteLLM、Ollama、DeepSeek……）。

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

`IPluginHost` 提供：日志、每插件 JSON 存储、共享 `HttpClient`、`ILlmService`（流式）、浮动面板 `ShowPanel`、`ShowPopup`（为任意文本弹操作栏）、`CaptureScreenRegionAsync`（框选截图）、剪贴板 / 粘贴 / 激活窗口、全局热键、托盘菜单项、`RunActionAsync`（调用其他动作，如 `"translate:panel"`）。`PopBar.Sdk.Ui.Form` 里是和设置页同款样式的表单助手。

第三方插件在独立的 `AssemblyLoadContext` 里加载，只与宿主共享 `PopBar.Sdk`；其他依赖随 DLL 一起放。

## 目录

```
PopBar.Sdk/              插件接口（IPopBarPlugin / IPluginHost / IPanel / ILlmService / Ui.Form）
PopBar/
  App.xaml(.cs)          入口：单实例、托盘、热键、插件装配、--run 命令通道
  Native/NativeMethods   全部 P/Invoke
  Core/                  InputHook、SelectionEngine、TextCapture、ClipboardBackup、InputSender、LlmService
  Actions/               内置动作定义、匹配规则、执行器
  Plugins/               PluginManager（发现/加载/动作同步）、PluginHost、PluginStorage、HotkeyManager
  Plugins/Builtin/       Translate / Ai / Ocr / SendTo
  UI/                    PopupWindow（操作栏）、PanelWindow（浮动面板）、RegionCaptureWindow、SettingsWindow、ActionEditWindow、Icons
  Themes/Modern.xaml     设置页样式
samples/HelloPlugin/     最小第三方插件示例
tools/                   图标生成、自动化测试脚本
```

## 已知边界

- 旧式 Win32 控件、部分 Electron/游戏类窗口不暴露 UIA 选区，需剪贴板回退（默认只在文本光标下或双击时启用）。
- 密码框不取词；以管理员运行的程序，PopBar 若非管理员则无法向其发送按键。
- Windows OCR 的识别质量取决于已安装的语言包（设置 → 时间和语言 → 语言 → 该语言的“光学字符识别”），只装中文包时英文识别偏弱。
- Google 翻译在国内需要代理（走系统代理）；微软翻译接口可直连。
