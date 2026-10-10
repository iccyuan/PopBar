# PopBar

Windows 上的 [PopClip](https://pilotmoon.com/popclip/)：选中文本后立刻在鼠标旁弹出一条操作栏（复制、搜索、翻译、打开链接、大小写转换、粘贴、自定义命令……）。

C# / WPF / .NET 10，零第三方 UI 依赖，常驻内存约 50-70 MB，弹窗不抢焦点。

## 构建

```powershell
# 需要 .NET 10 SDK（winget install Microsoft.DotNet.SDK.10）
cd PopBar
dotnet build -c Release
# 单文件发布（依赖系统已装的 .NET 10 桌面运行时，体积小）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\publish
# 自包含发布（无需安装运行时，体积 ~70MB）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\publish-sc
```

NuGet 唯一依赖：`Interop.UIAutomationClient`（IUIAutomation 的 COM 互操作，FlaUI 同款）。

## 工作原理

```
WH_MOUSE_LL / WH_KEYBOARD_LL  ──► SelectionEngine（钩子线程里只做判定）
   拖选 ≥5px / 双击 / Shift+点击 ──► 延迟 60ms ──► 工作线程 (STA)
                                                    │
                                 ┌──────────────────┴───────────────────┐
                                 │ 1. UIA TextPattern.GetSelection()    │  无副作用，Edge/Chrome/Word/记事本/Windows Terminal
                                 │ 2. 剪贴板回退：备份全部格式 → Ctrl+C  │  仅在 I 型光标或双击时；终端类程序永不使用
                                 │    → GetClipboardSequenceNumber 确认  │
                                 │    → 读文本 → 恢复备份                │
                                 └──────────────────┬───────────────────┘
                                                    ▼
                                 PopupWindow（WS_EX_NOACTIVATE，SetWindowPos 物理像素定位，
                                 多显示器/多 DPI 钳位，先量后显无闪烁，淡入+位移动画）
```

- **不抢焦点**：窗口加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`，`ShowActivated=false`。
- **替换粘贴**（大小写转换等）：备份剪贴板 → 写入结果 → `AttachThreadInput` + `SetForegroundWindow` 还焦点给源窗口 → Ctrl+V → 还原剪贴板。
- **消失时机**：在操作栏外点击、滚轮、按任意键、执行动作后。

## 目录

```
PopBar/
  App.xaml(.cs)          入口：单实例、托盘、开机自启、配置热更新
  Native/NativeMethods   全部 P/Invoke
  Core/InputHook         全局低级钩子线程
  Core/SelectionEngine   触发判定 + 取词调度 + 隐藏逻辑
  Core/TextCapture       UIA 取选区 / 剪贴板回退
  Core/ClipboardBackup   剪贴板全格式快照与恢复
  Core/InputSender       SendInput Ctrl+C/V、焦点还原
  Actions/               动作定义、匹配规则、执行器
  Config/                AppConfig + JSON 读写（%AppData%\PopBar\config.json）
  UI/PopupWindow         悬浮操作栏
  UI/SettingsWindow      设置（通用 / 动作 / 关于），改动自动保存
  UI/ActionEditWindow    动作编辑对话框
  UI/Icons               Lucide 线条图标（WPF Path）
  Themes/Modern.xaml     设置页样式
tools/make-icon.ps1      生成 Assets/popbar.ico
```

## 动作类型

| type | 说明 | 字段 |
|---|---|---|
| `copy` | 复制到剪贴板 | — |
| `url` | 打开网址 | `url`：`{text}` URL 编码，`{raw}` 原文 |
| `open` | 打开选中的链接 / 邮箱 | 一般配 `match: url` / `email` |
| `transform` | 转换后替换选区 | `op`：upper/lower/title/sentence/trim/reverse/none，`template`：如 `「{text}」` |
| `paste` | 用剪贴板内容替换选区 | — |
| `command` | 运行外部命令 | `command`、`args`（`{text}` 替换） |

`match`：`any` / `url` / `email` / `regex`（配 `regex` 字段），不匹配时按钮不显示。

## 已知边界

- 旧式 Win32 控件、部分 Electron/游戏类窗口不暴露 UIA 选区，需剪贴板回退；回退默认只在文本光标下或双击时启用。
- 密码框（UIA IsPassword）不取词。
- 以管理员运行的程序：PopBar 若非管理员，钩子仍能收到事件但 `SendInput` 会被 UIPI 拦截，剪贴板回退与粘贴在这类窗口无效。
