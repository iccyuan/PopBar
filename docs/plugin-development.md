# PopBar 插件开发指南

PopBar 的插件是一个 .NET 类库：引用 `PopBar.Sdk`，实现 `IPopBarPlugin`，PopBar 就会加载它。内置的翻译、AI、OCR、截图等功能也都是这样写的插件，用的是同一套接口，没有额外特权。

本文从零开始写一个插件，然后逐项介绍 SDK 提供的能力。完整可运行的示例见 [`plugins/Hello`](../plugins/Hello)。

- [快速开始](#快速开始)
- [插件的组成](#插件的组成)
- [动作：操作条上的按钮](#动作操作条上的按钮)
- [结果面板](#结果面板)
- [设置页](#设置页)
- [宿主能力一览](#宿主能力一览)
- [调用 AI 模型](#调用-ai-模型)
- [网络与代理](#网络与代理)
- [插件之间共享能力](#插件之间共享能力)
- [线程规则](#线程规则)
- [生命周期](#生命周期)
- [调试](#调试)
- [打包与发布](#打包与发布)
- [SDK 版本与兼容性](#sdk-版本与兼容性)
- [常见问题](#常见问题)

## 快速开始

### 1. 建项目

PopBar 基于 .NET 10 + WPF。插件项目需要：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- 让依赖随插件一起输出，PopBar 用独立的 AssemblyLoadContext 加载它们 -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <!-- PopBar.Sdk 由 PopBar 提供：只引用，不复制到输出目录 -->
    <Reference Include="PopBar.Sdk">
      <HintPath>path\to\PopBar.Sdk.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

`PopBar.Sdk.dll` 目前需要从本仓库编译（`dotnet build PopBar.Sdk`）。在本仓库的 `plugins/` 目录下开发时不用手动配置，[`plugins/Directory.Build.props`](../plugins/Directory.Build.props) 已经设好了目标框架和 SDK 引用。

> 只有 `PopBar.Sdk` 和 .NET / WPF 自身是和 PopBar 共享的。插件用到的其他库（NuGet 包等）要随插件 DLL 一起发布。

### 2. 写插件

```csharp
using System.Windows;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace WordCount;

public sealed class WordCountPlugin : IPopBarPlugin
{
    private IPluginHost _host = null!;

    public string Id => "wordcount";          // 稳定、唯一，见下文
    public string Name => "字数统计";
    public string Description => "统计选中文本的字数。";
    public Version Version => new(1, 0, 0);
    public string Icon => "hash";             // PopBar 图标名

    public void Initialize(IPluginHost host) => _host = host;

    public IReadOnlyList<PluginAction> GetActions() =>
    [
        new PluginAction
        {
            Id = "count",
            Label = "字数",
            Icon = "hash",
            PlaceAfter = "copy",              // 第一次加入操作条时放在「复制」后面
            IsApplicable = ctx => ctx.Text.Length > 0,
            Execute = async (ctx, ct) =>
            {
                var panel = await _host.ShowPanelAsync(new PanelOptions
                {
                    Title = "字数统计", Icon = "hash",
                    AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY,
                    Source = ctx.Text,
                });
                panel.SetText($"{ctx.Text.Length} 个字符");
            },
        },
    ];

    public void Dispose() { }
}
```

### 3. 加载

编译后用开发模式直接从构建目录加载，不用安装：

```
PopBar.exe --dev-plugin bin\Debug\net10.0-windows10.0.19041.0
```

选中一段文字，操作条上就会出现「字数」按钮。改完代码重新编译、再执行一次这条命令，新代码立即生效，详见[调试](#调试)。

## 插件的组成

`IPopBarPlugin` 的成员：

| 成员 | 说明 |
|---|---|
| `Id` | 插件的唯一标识，小写、不含空格，如 `"translate"`。**发布后不要再改**：用户的按钮配置、插件设置（`plugins\{id}.json`）、数据目录、停用状态、`--run {id}:{动作}` 命令都按它记录，改了这些就都对不上了。和已加载的插件重复时不会被加载 |
| `Name` / `Description` | 显示在「插件」页 |
| `Version` | 插件自己的版本号，显示在「插件」页 |
| `Icon` | 代表整个插件的图标（插件卡片、动作分组），默认 `"puzzle"` |
| `Bar` | 动作在操作条上的呈现方式，见 [`BarOptions`](#合并按钮与默认显示) |
| `Initialize(host)` | 加载后调用一次。保存 `host`，在这里注册热键、托盘入口、共享服务、AI 服务商 |
| `GetActions()` | 插件提供的动作（操作条按钮），可以随设置变化 |
| `CreateSettingsView()` | 可选的设置界面，显示在「插件」页该插件卡片的齿轮里；返回 `null` 表示没有设置 |
| `Dispose()` | 插件被停用、卸载或 PopBar 退出时调用。停掉定时器、关闭自己的窗口、取消事件订阅 |

图标名来自 PopBar 内置的图标集（Lucide 风格的线条图标，如 `copy`、`languages`、`sparkles`、`scan-text`、`qr-code`、`code`、`clock`、`image`）。完整列表可以在运行时用 `host.Ui.IconNames` 取得，设置页里也可以用 `host.Ui.IconPicker` 让用户挑选。

## 动作：操作条上的按钮

```csharp
new PluginAction
{
    Id = "count",                     // 插件内唯一；完整 id 为 "{插件id}:{动作id}"
    Label = "字数",
    Icon = "hash",
    Description = "统计字符数",        // 可选，显示在「动作」页
    EnabledByDefault = true,          // 第一次加入操作条时是否打开
    PlaceAfter = "copy",              // 第一次加入时放在哪个动作后面
    IsApplicable = ctx => ...,        // 这次选区是否显示这个按钮
    Execute = async (ctx, ct) => ..., // 点击后做什么
    Configure = () => ...,            // 可选：「动作」页里「编辑」打开的编辑器
}
```

- **`IsApplicable`** 在取词线程上同步执行，每次弹出操作条都会调用，必须很快。只做简单判断（长度、是否像网址），不要做 IO。
- **`Execute`** 在线程池上运行，可以随意 `await`。`ct` 是这次执行的取消令牌。界面相关的操作通过 `IPluginHost` 完成。
- **没处理的异常**：PopBar 会记日志，并在选区旁弹出「执行失败：原因」，用户不会以为点了没反应。插件自己能说清楚的错误，最好用 `IPanel.SetError` 显示更友好的说明。
- **`PlaceAfter`** 可以写内置动作的 id（`search`、`copy`、`paste`、`open`、`mail`），也可以写其他插件动作的完整 id（`"translate:panel"`）。只影响第一次加入的位置，之后以用户的排序为准。
- **`Configure`** 在 UI 线程运行。保存了改动时返回 `true`，并调用 `host.RefreshActions()` 让列表刷新标签。不提供时，「编辑」会跳到插件的设置页。

### 选区上下文

`SelectionContext` 描述用户选中了什么：

| 字段 | 说明 |
|---|---|
| `Text` | 选中的文字 |
| `SourceWindow` / `HasSourceWindow` | 选区所在的窗口句柄（可用于 `PasteText` 写回） |
| `SourceProcess` | 所在程序，如 `"notepad.exe"` |
| `ScreenX` / `ScreenY` | 选区位置（物理像素），打开面板时作为锚点 |

### 动作列表会变

`GetActions()` 的结果可以随插件设置变化（例如「发送到应用」的每个目标都是一个动作）。列表变了以后调用 `host.RefreshActions()`：新动作马上加入操作条，删掉的会离开，打开着的设置页也会刷新。

### 合并按钮与默认显示

`IPopBarPlugin.Bar` 返回 `BarOptions`，决定插件的动作怎么出现在操作条上：

```csharp
public BarOptions Bar => new()
{
    MergeAs = "编码",          // 有两个以上动作同时可用时，合并成一个按钮，点开是列表
    MergeIcon = "code",       // 合并按钮的图标，默认用插件的 Icon
    ShowByDefault = true,     // 安装后动作是否默认显示在操作条上
};
```

只有一个动作适用于当前选区时，它仍以自己的按钮出现。小众插件可以设 `ShowByDefault = false`，让用户到「动作」页自己打开。

## 结果面板

`host.ShowPanelAsync(options)` 在选区附近打开一个浮动面板，返回 `IPanel`。`IPanel` 的方法都可以在任意线程调用。

```csharp
var panel = await _host.ShowPanelAsync(new PanelOptions
{
    Title = "翻译", Icon = "languages",
    AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY,
    Source = ctx.Text,          // 面板顶部折叠显示原文
    Badge = "Google 翻译",       // 标题旁的小标签
    Width = 480,
});
panel.SetBusy(true);
try
{
    var result = await TranslateAsync(ctx.Text, panel.Cancellation);
    panel.SetText(result);
    panel.SetStatus("en → zh-CN");
    panel.AddButton("复制", text => _host.CopyToClipboard(text));
}
catch (OperationCanceledException) { }      // 用户关了面板
catch (Exception ex) { panel.SetError("翻译失败：" + Errors.Describe(ex)); }
finally { panel.SetBusy(false); }
```

| 方法 | 说明 |
|---|---|
| `SetText` / `AppendText` | 设置或追加正文。流式输出时用 `AppendText`，文字会平滑渐显 |
| `AppendThinking` | 追加模型的思考过程，显示在正文上方的折叠块里 |
| `SetStatus` | 正文下方的灰色小字 |
| `SetError` | 醒目的红色错误框；传 `null` 隐藏 |
| `SetBusy` | 顶部的进度光带 |
| `SetTitle` / `SetBadge` | 修改标题、标签 |
| `AddButton(label, onClick)` | 底部按钮，回调拿到当前正文 |
| `SetContent(factory)` | 用任意 WPF 内容替换正文（列表、图片、表单…），`factory` 在 UI 线程执行 |
| `SetSize(width, maxBodyHeight)` | 打开后调整大小 |
| `Text` | 当前正文（可编辑面板里是用户改过的） |
| `Cancellation` | 用户关闭面板时取消，传给网络请求、流式调用 |
| `Pinned` / `Close()` / `Closed` | 固定、关闭、关闭事件 |

`PanelOptions` 的其他选项：

| 选项 | 默认 | 说明 |
|---|---|---|
| `Editable` | `false` | 正文可编辑（如 OCR 结果） |
| `CloseOnOutsideClick` | `true` | 点击面板外关闭（固定后不关） |
| `Pinned` | `false` | 打开时就固定 |
| `ShowTextArea` | `true` | 只用 `SetContent` 时设为 `false` |
| `MaxBodyHeight` | `420` | 正文最大高度，超出滚动 |
| `SourceToolTip` | `false` | 原文被折叠时，鼠标悬停显示全文 |
| `Bare` | `false` | 只要一个圆角容器：没有标题栏、底栏和内边距，整个面板由 `SetContent` 的内容决定 |

## 设置页

`CreateSettingsView()` 返回的界面显示在「插件」页。设置采用**草稿**模式：界面编辑的是一份副本，用户按「保存」后才生效，按「取消」就丢弃。

```csharp
public sealed class Settings
{
    public bool CountSpaces { get; set; } = true;
    public string Hotkey { get; set; } = "";
}

private Settings _cfg = new();

public void Initialize(IPluginHost host)
{
    _host = host;
    _cfg = host.Storage.Get<Settings>();          // 读取 plugins\{id}.json，没有时返回 new Settings()
}

public FrameworkElement? CreateSettingsView()
{
    var d = SettingsDraft.Clone(_cfg);            // 草稿：深拷贝
    _host.Settings.OnApply(d, () =>               // 「保存」时执行
    {
        _cfg = SettingsDraft.Clone(d);
        _host.Storage.Set(_cfg);
    });
    void Changed() => _host.Settings.Changed();   // 每次修改后调用，窗口显示「有未保存的更改」

    return Form.Card(
        Form.Row("字符数包含空白", Form.Toggle(d.CountSpaces, v => { d.CountSpaces = v; Changed(); })),
        Form.Row("快捷键", _host.Ui.HotkeyBox(d.Hotkey, v => { d.Hotkey = v; Changed(); })));
}
```

- 用 `OnApply(draft, apply)` 把草稿对象交给 PopBar，它会比较草稿改动前后的内容：用户把值改回原样时，「未保存」提示会自动消失。所有设置都要放在草稿对象里，否则改动可能被当成没变。
- 每次打开设置窗口都会重新调用 `CreateSettingsView()`，不要缓存界面。
- 设置用 `System.Text.Json` 序列化（属性名转小驼峰），类要有无参构造函数。

### 现成的控件

`PopBar.Sdk.Ui.Form` 生成和 PopBar 设置页一致的界面，不用写 XAML：

| 方法 | 说明 |
|---|---|
| `Card(rows...)` | 圆角卡片，行之间自动加分隔线 |
| `Row(label, control, hint)` | 一行：左边标题和说明，右边控件 |
| `Section(title)` / `Hint(text)` | 小标题、说明文字 |
| `Toggle` / `TextBox` / `Password` / `Combo` / `Button` | 常用输入控件，带变更回调 |
| `ListRow(...)` | 列表行：标题、副标题、开关、编辑、删除 |
| `Stack` / `Buttons` | 纵向、横向排列 |

`host.Ui` 提供更多宿主控件和对话框（只能在 UI 线程调用）：

| 分组 | 内容 |
|---|---|
| `IHostDialogs` | `PickFile`、`PickFolder`、`SaveFile`、`Confirm`、`Alert`、`EditDialog`（模态编辑对话框） |
| `IHostInputs` | `KeyBox`（单个按键）、`HotkeyBox`（组合键）、`AppPicker`、`WindowPicker`（点选屏幕上的窗口）、`FileBrowse`、`FolderBrowse`、`ProxyPicker`、`RunningApps()` |
| `IHostVisuals` | `Icon`、`IconPicker`、`IconNames`、`BlurImage`（磨砂效果用的模糊图） |
| `ILlmUi` | `ProviderPicker`、`ModelPicker`（AI 服务商、模型下拉） |

## 宿主能力一览

`Initialize` 拿到的 `IPluginHost`：

| 能力 | 成员 |
|---|---|
| 日志 | `Log.Info / Warn / Error`，写入 PopBar 日志并带插件 id 前缀 |
| 设置存储 | `Storage.Get<T>()` / `Storage.Set(value)`，存在 `%AppData%\PopBar\plugins\{id}.json` |
| 数据目录 | `DataDir`：`%AppData%\PopBar\plugins\.data\{id}\`（可能还不存在，用前先创建），更新插件不会删除 |
| 面板 | `ShowPanel` / `ShowPanelAsync` |
| 操作条 | `ShowPopup(context)`：对任意文字弹出 PopBar 操作条（如 OCR 出来的文字） |
| 截图 | `CaptureScreenRegionAsync()`：让用户框选区域，返回 PNG 和位置 |
| 剪贴板与输入 | `CopyToClipboard`、`PasteText(text, window)`（替换选区，剪贴板会恢复）、`ActivateWindow`、`FindWindowByProcess` |
| 热键 | `RegisterHotkey(id, "Ctrl+Alt+O", callback)` / `UnregisterHotkey`，组合键被占用时返回 `false` |
| 托盘 | `AddTrayItem(TrayItem)`：托盘右键菜单项、左键动作、`--run {id}:{项}` 命令 |
| 动作 | `RefreshActions()`、`RunActionAsync("translate:panel", ctx)`（调用自己或其他插件的动作） |
| 暂停取词 | `SuppressSelection()`：自己的全屏覆盖层处理鼠标拖动时暂停 PopBar 的选区检测，`Dispose` 后恢复 |
| 设置 | `Settings`（草稿会话）、`OpenSettings()`（打开设置窗口并定位到本插件） |
| 主题 | `Theme`（`"dark"` / `"light"`）、`ThemeChanged` 事件（UI 线程） |
| 界面 | `Ui`、`Dispatcher` |
| AI | `Llm`，见下节 |
| 网络 | `Http`、`HttpFor(route)`，见[网络与代理](#网络与代理) |
| 共享服务 | `Services`，见[插件之间共享能力](#插件之间共享能力) |

另外几个静态工具：

- `Errors.Describe(ex)`：把异常整理成一行给用户看的说明，跳过 .NET 的通用包装信息，留下真正的原因。
- `Ui.Shortcut`：处理用户可自定义的窗口内快捷键（格式和 `HotkeyBox` 一致）。
- `Ui.ScreenWindows`：自己的浮动窗口按物理像素定位、隐藏于 Alt+Tab、置顶。
- `Ui.CodeView.Json(text)`：带语法高亮的只读 JSON 视图，适合放进 `SetContent`。
- `HostInput.InjectedTag`：自己用 `SendInput` 模拟键鼠时填进 `dwExtraInfo`，PopBar 就不会把它当成用户操作。
- `HostChrome.IsChrome`：PopBar 自己的临时窗口（操作条）带这个标记，拾取窗口、截图时应跳过。

## 调用 AI 模型

`host.Llm` 用的是用户在「服务」页配置的模型服务，插件不需要自己管理 API Key。

```csharp
if (!_host.Llm.IsConfigured) { panel.SetError("请先在设置 →「服务」里配置 AI 服务"); return; }

var request = new LlmRequest
{
    System = "你是一个翻译助手。",
    User = ctx.Text,
    Thinking = false,     // 简单任务关掉深度思考；null = 跟随「服务」页的设置
};
await foreach (var chunk in _host.Llm.StreamChunksAsync(request, panel.Cancellation))
{
    if (chunk.IsThinking) panel.AppendThinking(chunk.Text);
    else panel.AppendText(chunk.Text);
}
```

- `StreamAsync` 只返回答案文字；`StreamChunksAsync` 把思考过程和答案分开；`CompleteAsync` 一次性拿到完整回复。
- `LlmRequest.Provider` 指定用哪个已配置的服务（`Llm.ConfiguredProviderIds` 里的一个），不填用默认服务；`Model` 覆盖该服务的默认模型；`Effort` 是思考深度（`LlmEffort.Low` 等），可选档位用 `Llm.EffortLevels(provider, model)` 取得。
- 让用户在插件设置里选服务和模型：`host.Ui.ProviderPicker`、`host.Ui.ModelPicker`。
- 模型能力（是否支持图片、语音）：`Llm.CapabilitiesOf(provider, model)`。

### 接入新的模型服务

插件也可以给 PopBar 增加 AI 服务商，在 `Initialize` 里注册后，它会出现在「服务」页供用户配置。兼容 OpenAI `/chat/completions` 协议的服务一行就够：

```csharp
host.Llm.RegisterProvider(new OpenAiCompatibleProvider(new()
{
    Id = "kimi", Name = "Kimi",
    DefaultBaseUrl = "https://api.moonshot.cn/v1",
    RequiresApiKey = true,
}));
```

思考参数、token 上限字段名等与 OpenAI 不同的地方，继承 `ChatCompletionsDialect` 覆盖对应方法，作为第二个参数传入。完全不同的协议则自己实现 `ILlmProvider`。

## 网络与代理

- `host.Http`：共享的 `HttpClient`，联网方式跟随 PopBar（「代理」页的设置），默认超时 60 秒。
- `host.HttpFor(ProxyRoute route)`：按插件自己的选择联网——`Default` 跟随 PopBar、`Direct` 直连、`Proxy` 走「代理」页设置的代理。本机和局域网地址总是直连。

让用户选择：

```csharp
Form.Row("联网方式", _host.Ui.ProxyPicker(d.Proxy, v => { d.Proxy = v; Changed(); }))
// 请求时
var http = _host.HttpFor(_cfg.Proxy);
```

不要自己 `new HttpClient()`，那样会绕开用户的代理设置。

## 插件之间共享能力

插件之间不直接引用，而是通过 `host.Services` 按接口共享：

```csharp
// 提供方（如翻译插件），在 Initialize 里
host.Services.Provide<ITextTranslator>(new MyTranslator());

// 使用方（如屏幕翻译插件），用到时再取
var translator = _host.Services.Get<ITextTranslator>();
if (translator == null) { panel.SetError("需要启用「翻译」插件"); return; }
```

SDK 目前定义了 `ITextTranslator`（翻译）和 `IOcrEngine`（文字识别）。提供方被停用或卸载时，它提供的服务会被撤掉，`Get` 返回 `null`，所以**每次用之前再取，不要在 `Initialize` 里缓存**。

## 线程规则

- `IPluginHost` 的成员可以在任意线程调用。
- `ShowPanel`、`RegisterHotkey` 会等 UI 线程执行完才返回。不要在持有锁时、或 UI 线程正在等你的时候从工作线程调用它们，否则会死锁。异步代码里用 `ShowPanelAsync`。
- `IPanel` 的方法都可以在任意线程调用。
- `IHostUi`（`host.Ui`）和你创建的 WPF 元素只能在 UI 线程使用。需要时用 `host.Dispatcher.InvokeAsync(...)`。
- `Execute` 在线程池上运行；`IsApplicable` 在取词线程上运行；`CreateSettingsView`、`Configure`、热键和托盘回调、`ThemeChanged` 在 UI 线程运行。

## 生命周期

1. **加载**：PopBar 启动、安装新插件、或执行 `--dev-plugin` 时，创建插件实例。
2. **初始化**：插件处于启用状态时调用 `Initialize(host)`。在「插件」页被关掉的插件不会初始化。
3. **运行**：PopBar 按需调用 `GetActions`、`IsApplicable`、`Execute`、`CreateSettingsView`。
4. **停用**：用户在「插件」页关掉插件时，调用 `Dispose()`，然后 PopBar 撤销它通过 `host` 注册的一切——热键、托盘入口、共享服务、AI 服务商、设置草稿、主题监听。操作条上它的按钮配置会保留，重新启用后恢复。
5. **重新启用**：PopBar 创建一个**新实例**并调用 `Initialize`。不要假设 `Dispose` 过的对象还会被再次使用。
6. **卸载 / 更新**：调用 `Dispose()` 并撤销注册。更新时安装目录会被整体替换，所以数据要放在 `host.DataDir`。

`Dispose` 里要停掉插件自己启动的东西：定时器、后台任务、打开的窗口、对静态事件的订阅。PopBar 能撤销的只有通过 `host` 注册的部分。

## 调试

### 从构建目录直接加载

```
PopBar.exe --dev-plugin <构建输出目录>
```

- PopBar 没在运行时会先启动，然后加载插件；已在运行时，再执行一次就是重新加载（旧实例先 `Dispose`）。
- 插件 DLL 从内存加载，不锁文件，可以照常重新编译。
- 「插件」页里这类插件标为「开发中，从构建目录加载」，没有卸载按钮，免得误删构建目录。
- 不会复制、也不会记住：下次启动 PopBar 时，要再执行一次才会加载。

把它加进插件项目，每次 Debug 编译后自动重新加载：

```xml
<PropertyGroup>
  <PopBarExe>D:\Tools\PopBar\PopBar.exe</PopBarExe> <!-- 换成你的 PopBar.exe -->
</PropertyGroup>
<!-- start 不等待：PopBar 没在运行时会被启动，构建不会卡住；路径末尾的 "." 避免 \" 把引号转义掉 -->
<Target Name="ReloadInPopBar" AfterTargets="Build" Condition="'$(Configuration)' == 'Debug' And Exists('$(PopBarExe)')">
  <Exec Command="start &quot;&quot; &quot;$(PopBarExe)&quot; --dev-plugin &quot;$(TargetDir).&quot;" ContinueOnError="true" />
</Target>
```

### 日志与断点

- 日志在 `%AppData%\PopBar\popbar.log`，`host.Log` 写的内容带 `[插件id]` 前缀；加载失败、初始化异常、动作异常也都在这里。
- 用 Visual Studio / Rider 附加到 `PopBar.exe` 进程即可下断点。插件的 `.pdb` 放在 DLL 旁边，异常堆栈会带行号。
- 用 `PopBar.exe --run {插件id}:{托盘项id}` 可以直接触发托盘入口，方便反复测试。

## 打包与发布

### 安装方式

用户在「插件」页点「安装插件」，可以：

- **从 GitHub 安装**：填仓库地址（`https://github.com/作者/仓库` 或 `作者/仓库`），PopBar 下载最新 Release 里的 `.zip`（名字含 `plugin` 的优先），没有 zip 时下载 `.dll`。
- **从本地文件安装**：选 `.zip` 或 `.dll`。

也可以用命令行：`PopBar.exe --install <路径或仓库地址>`。安装、更新、卸载都即时生效，不用重启。

### 打包

- **只有一个 DLL、没有其他依赖**：直接发布 `.dll`（同名的 `.pdb`、`.deps.json` 会一起带上）。
- **有依赖**：把构建输出打成 `.zip`，插件 DLL 和依赖放在同一层。可以直接放在 zip 根目录，外面套几层只有一个文件夹的目录也行（会自动剥掉）；但插件 DLL 不能放进更深的子目录，PopBar 只在这一层找插件。
- 不要把 `PopBar.Sdk.dll` 打进去，PopBar 始终使用自己的那份（打进去也会被忽略）。

插件会被装到 `%AppData%\PopBar\plugins\<目录>\`，目录名取自 zip、DLL 或仓库的名字。更新时整个目录被替换。

### 安全

第三方插件和 PopBar 拥有相同的权限：能读剪贴板、模拟按键、联网。「插件」页会把第三方插件标为「来源未验证」。请在项目说明里写清插件会访问什么。

## SDK 版本与兼容性

`PopBar.Sdk` 的程序集版本就是插件 API 版本，与 PopBar 的程序版本无关，当前为 **1.0**。

- **新增**接口时次版本 +1（1.0 → 1.1）：旧插件在新 PopBar 上照常运行。
- **破坏性改动**时主版本 +1（1.x → 2.0）。

插件编译时会记下引用的 SDK 版本，PopBar 在安装和加载时比对，满足以下条件才加载：

- 主版本与 PopBar 的相同；
- 次版本不高于 PopBar 的。

不满足时，「插件」页会说明原因（如「插件需要更新版本的 PopBar（插件基于 SDK 1.2，当前 PopBar 提供 SDK 1.0）」）。更新插件时如果新版本不兼容，会在替换之前就拒绝，已安装的旧版本不受影响。

因此，想让插件支持尽可能多的 PopBar 版本，就引用你实际需要的最低版本的 SDK。

## 常见问题

**操作条上没有出现我的按钮**

- 看「插件」页：插件是否加载成功、有没有报错、开关是否打开。
- 看「动作」页：按钮是否被关掉了（`ShowByDefault = false` 或 `EnabledByDefault = false` 时默认是关的）。
- 检查 `IsApplicable` 对这次选区是否返回 `true`。
- 看日志里有没有 `GetActions` 或 `IsApplicable` 抛出的异常。

**「插件」页显示「插件 id 与已加载的插件重复」**

换一个更独特的 `Id`，比如加上作者名前缀（`acme-translate`）。

**设置改了但没生效**

确认 `OnApply` 的回调里把草稿写回了实际使用的设置对象，并调用了 `Storage.Set`；需要重新注册热键的，也在回调里重新注册。

**在后台线程操作界面报错**

WPF 元素只能在创建它的线程（UI 线程）访问。用 `host.Dispatcher.InvokeAsync(...)`，或者改用 `IPanel` 的方法——它们会自动切到 UI 线程。

**引用的第三方库加载失败**

确认项目设置了 `EnableDynamicLoading`，并且依赖的 DLL 和 `.deps.json` 都和插件 DLL 放在一起（zip 发布）。
