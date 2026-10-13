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
dotnet publish PopBar\PopBar.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

推送 `v*` 标签即自动发版（`.github/workflows/release.yml`），版本号取自标签。

## 工作原理

```
鼠标/键盘低级钩子 ──► 拖选 / 多击 / Shift+点击 ──► 工作线程取词
                                                  1. UIA TextPattern（无副作用）
                                                  2. 剪贴板回退：备份 → Ctrl+C → 读取 → 还原（仅 I 型光标或双击时）
                                                       ▼
                                      操作栏（WS_EX_NOACTIVATE，多屏多 DPI 钳位）──► 点按钮 ──► 浮动面板
```

- 操作栏不抢焦点；替换粘贴时备份剪贴板、把焦点还给源窗口再 Ctrl+V。
- 外部点击、滚轮、按键后操作栏消失；面板可用图钉固定。
- 默认「按键后显示」：选中时按住或 4 秒内按一下 Ctrl / Alt / Win 才弹出；可改为「选中后自动显示」。
- 连击按 Windows 规则判定，三击、四击原地更新操作栏。同插件的多个动作合并成一个按钮，超出「最多显示按钮数」的收进「···」。
- 联网走系统代理，按地址族建连（规避某些 VPN / TUN 下双栈套接字被拒的 10013）。

## 内置插件

| 插件 | 做什么 |
|---|---|
| 翻译 | 面板里直接出译文，标明服务商；Google 或 AI 模型；本地检测源语言，原文已是目标语言时改译备用语言 |
| AI 助手 | 解释 / 总结 / 润色，流式输出；可开思考，推理过程显示在答案上方、可收起；提示词可自定义，每条一个按钮 |
| 屏幕 OCR | Ctrl+Alt+O 框选 → Windows 内置 OCR → 操作栏 + 可编辑结果 |
| 截图 | 对标 Snipaste：F1 截图，自动识别窗口，标注（矩形 / 椭圆 / 箭头 / 画笔 / 马赛克 / 文字），取色；长截图自动滚动拼接；录 GIF；F3 贴图 |
| 发送到应用 | 把选中内容粘到指定程序、带参数启动程序或打开 URL 协议；多个目标合并成一个按钮 |
| 二维码 | 生成二维码；框选或整屏识别二维码 / 条形码 |
| 编码工具 | 内容符合格式才出现：Base64 / URL / Unicode / HTML 解码、JSON 格式化、JWT 解析、时间戳互转；另有编码 + 哈希面板 |
| 便签 | 选中内容贴成桌面便签，可编辑、换色、置顶，重启恢复 |
| 屏幕翻译 | Ctrl+Alt+T 框选 → OCR → 翻译 → 译文覆盖在原位置，按住空格看原文 |

AI 模型在「设置 → 服务」配置：`anthropic`（官方 SDK）或 `openai`（任何兼容 `/chat/completions` 的服务）。那里的模型只是默认值，AI 助手、翻译等插件可以在各自设置里单独选模型（同一接口与密钥）。热键、保存目录等都在各插件设置里。

## 设置

- 改动需点保存（Ctrl+S）才生效，取消恢复；主题、按钮文字、按钮数、毛玻璃不透明度即时生效。
- 「动作」页拖动排序，同插件的动作成组；「插件」页每个插件一张卡片，齿轮展开其设置。
- 命令行：`--settings` 打开设置，`--run ocr:capture` 触发某个动作（可绑到任意快捷键工具）。

## 写一个插件

新建类库引用 `PopBar.Sdk`，实现 `IPopBarPlugin`，DLL 放到 `%AppData%\PopBar\plugins\<目录>\` 后重启。完整示例见 `samples/HelloPlugin`。

```csharp
public sealed class HelloPlugin : IPopBarPlugin
{
    public string Id => "hello";
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
            IsApplicable = ctx => ctx.Text.Length > 0,
            Execute = (ctx, ct) =>
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

`IPluginHost` 给插件的能力：面板（`ShowPanel`，可改大小、报错、进度、标签；`Bare = true` 时只给一个圆角容器，整个内容由插件 `SetContent` 自己布局）、操作栏（`ShowPopup`）、AI 模型（`Llm`，每次请求可用 `LlmRequest.Model` 指定模型，设置页里的只是默认；`Ui.ModelPicker` 给插件现成的模型下拉）、框选截图、剪贴板 / 粘贴、全局热键、托盘菜单项、调用其他动作（`RunActionAsync`）、插件间共享服务（`Services`，如翻译、OCR）、设置草稿（`Settings`，保存后才生效）、设置页同款控件与对话框（`Ui`）。第三方插件在独立的 `AssemblyLoadContext` 里加载，依赖随 DLL 一起放。

## 分层

```
PopBar.Sdk   接口：IPopBarPlugin / IPluginHost / IPanel / ILlmService / IHostUi / Ui.Form
Core         SelectionEngine、TextCapture（UIA → 剪贴板策略链）、LlmService、ThemeManager、InputHook
Plugins      PluginManager、PluginHost、Builtin/*
UI           PopupWindow、PanelWindow、SettingsWindow、FrostedGlass、Icons
App          组合根：托盘、单实例、热键
```

扩展点：操作栏按钮用 `IPopBarPlugin`；AI 提供商用 `ILlmProvider`（`host.Llm.RegisterProvider`），兼容 `/chat/completions` 的服务直接用 `new OpenAiCompatibleProvider(options, dialect)`，只在 `IChatCompletionsDialect` 里写与众不同的部分（思考参数、token 上限字段、推理字段）；取词方式用 `ITextCaptureStrategy`；插件间能力用 `host.Services.Provide<T>()`。

## 已知边界

- 旧式 Win32 控件、部分 Electron / 游戏窗口不暴露 UIA 选区，需剪贴板回退。
- 密码框不取词；管理员权限的程序收不到非管理员 PopBar 的按键。
- Windows OCR 质量取决于已装语言包；Google 翻译在国内需要代理。
