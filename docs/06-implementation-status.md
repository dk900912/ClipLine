# ClipLine · 实现状态与工程约定

> 本文随 P1→P6 逐阶段更新（2026-11-05 追加渲染壳 v0），记录**真实可编译、可运行**的代码范围，以及本机环境造成的能力边界。
> 规格来源：[#1](https://github.com/dk900912/ClipLine/issues/1)；功能基线：`01-feature-alignment.md`；视觉与交互基准：`05-ui-design.md` + `assets/ui/prototype.html`；渲染壳：`08-renderer-shell.md`。

## 1. 本机环境事实（2026-11-04 采集，11-05 更新）

| 项 | 状态 | 影响 |
|---|---|---|
| .NET 8 运行时 | 有 | 可运行 `net8.0` 程序 |
| .NET SDK | **系统未安装**；从官方通道下载 `8.0.427` 解压到临时目录 | 纯 C# 项目可编译；不做系统级安装、不改用户环境 |
| Visual Studio 工作负载 | **无** | .NET 的 WinUI 3 不需要 VS 工作负载；改用 NuGet + self-contained，见 §3.2 |
| WindowsAppSDK / BuildTools | NuGet `Microsoft.WindowsAppSDK 1.6.250108002` + `Microsoft.Windows.SDK.BuildTools` | WinUI 3 应用宿主可编译 |
| WindowsAppRuntime 系统包 | 只有 1.6 / 1.7 / 1.8 的 **Framework** 包，缺 Main / Singleton / DDLM | 框架依赖部署的 bootstrap 失败，改用 self-contained |
| Windows SDK 头文件 | 有 `10.0.26100.0` | Win32 P/Invoke 与 `mt.exe` 可用 |
| Node.js | 有 | 仅用于校验 HTML 语法 |
| Python | 无（Store 占位符） | 不使用 |

**因此**：P1–P6 把**全部领域逻辑、状态机、捕获规则、交互语义、菜单模型、本地化与验收矩阵**落成零 NuGet 依赖的纯 C#，可在本机编译并跑测试。P0 已用真实 Win32 分层 HWND 验证过平台假设（见 #2 结论与 `spikes/p0-layered-hwnd/`）。**2026-11-05 追加的渲染壳 v0 已把 WinUI 3 宿主 + 分层 HWND 真正跑起来**（`--smoke` 14/14 PASS），但界面仍是 GDI DIB 而非 Win2D，详见 `08-renderer-shell.md`。

## 2. 唯一测试接缝

按 #1 定稿，**整个 v1 只有应用门面一个接缝**：

```
离散事件 AppInput ──► AppFacade ──► StateSnapshot + IReadOnlyList<AppCommand>
                          ▲                        │
                          │                        └─ 平台层执行（剪贴板/目录/拖放/合成器…）
              假时钟 AdvanceTo / 假环境 / RecordingCommandSink
```

- `src/ClipLine.Domain/`：纯 C#，零 UI / 零 Win32 依赖，直译 `Line.swift`。
- `src/ClipLine.Application/`：`AppFacade`（`partial`，P2–P5 用同一文件扩展点加分支）、`Contracts`（输入 / 命令 / 快照）、`Fakes`（假时钟、假环境、命令记录器）。
- `tests/ClipLine.Tests/`：零依赖控制台 runner，反射发现所有 `ITestSuite`，用例编号直接对应 `01` 的验收编号；支持 `--report <csv>` 导出追溯矩阵数据。
- 好测试只断言 `StateSnapshot` 与 `AppCommand`，用 `AdvanceTo` 推进时序，不依赖真实剪贴板、文件系统、窗口或 sleep。

## 3. 构建与验证

```bash
# 已系统安装 SDK 时
bash tools/verify.sh

# 使用临时目录的 SDK
DOTNET_SDK="/c/Users/.../Temp/clipline-tools/dotnet/dotnet.exe" bash tools/verify.sh

# 真实桌面会话冒烟（PowerShell，需先编译）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/smoke.ps1
```

`tools/verify.sh` 先编译领域 / 应用 / 平台 / 测试四个项目，再编译 `src/ClipLine.App`（WinUI 3 self-contained 渲染壳），最后跑全部测试；退出码非 0 表示编译或用例失败。冒烟测试依赖真实桌面会话，不放进无桌面的 CI。

### 3.1 P2 的平台适配器（`src/ClipLine.Platform/`）

零 NuGet 依赖、可编译的真实 Win32 P/Invoke 封装：`AddClipboardFormatListener`（消息驱动，空闲零开销）、`ReadDirectoryChangesW`（`FILE_NOTIFY_INFORMATION` 解析 + 0.2 s 防抖）、`SystemParametersInfoW(SPI_GETWORKAREA)` 取主屏工作区、`GetDpiForSystem` 换算单一系统缩放、`RegisterHotKey`、`SHFileOperationW(FOF_ALLOWUNDO)` 回收站、以及 P0 结论要求的**独立 message-only 托盘宿主窗口**。`PlatformCommandSink` 把门面命令映射到这些适配器，渲染类命令由 Shell 层消费。

正式产品按 `02-tech-stack.md` 仍改用 **CsWin32 源生成器**；当前手写声明只为在没有工作负载的本机把平台边界编译验证一遍。

### 3.2 渲染壳 v0（`src/ClipLine.App/` + `src/ClipLine.Shell/`）

WinUI 3 应用宿主 + 自管分层 HWND 绳窗口 + 三态托盘，经**同一个** `AppFacade` 接缝接入领域逻辑。已通过 NuGet + WindowsAppSDK **self-contained** 部署跑通，无需系统工作负载或管理员权限。编译 0 警告 0 错误，真实桌面会话 `--smoke` 14/14 PASS。工具链阻塞与解法、冒烟清单、已知偏离见 `08-renderer-shell.md`。

**仍与定稿有偏离**：渲染走 P0 验证过的 GDI 32bpp DIB，而非 Win2D / DirectComposition；没有 XAML 窗口（托盘菜单、编辑器、预览窗尚未接界面）。

## 4. 阶段进度

| 阶段 | 工单 | 状态 | 主要交付 |
|---|---|---|---|
| P0 | #2 | ✅ 已关闭 | 透明分层 HWND 技术切片（`spikes/p0-layered-hwnd/`，20/20 自检） |
| P1 | #3 | ✅ 已完成（提交 `648869b`） | 领域模型 + 应用门面 + 46 条用例 |
| P2 | #4 | ✅ 已完成 | 三层捕获准入、完整性校验、剪贴板过滤、飞入参数、Win32 适配器 + 23 条用例 |
| P3 | #5 | ✅ 已完成 | 手势状态机、右键菜单条件、拖放目标语义、键盘借用、热键录制 + 20 条用例 |
| P4 | #6 | ✅ 已完成 | 快速查看 / 标注 / passthrough 修剪 / 拿在手上 / 右键扩展的可测模型 + 15 条用例 |
| P5 | #7 | ✅ 已完成 | 16 项菜单模型与可用性、11 语言 key 校验、主题/无障碍令牌、MSIX 清单草稿 + 15 条用例 |
| P6 | #8 | ✅ 已完成 | 128 条用例全绿、137 项编号追溯矩阵（82 项自动化）、隐私静态检查、最终放行报告 |
| 渲染壳 v0 | #9 | ✅ 已完成 | WinUI 3 宿主 + 分层 HWND + 托盘 + 帧驱动重绘，14/14 冒烟 PASS |

## 5. 明确未覆盖（不得误报为完成）

- **Win2D / Composition 渲染**、真实 Acrylic / 弹簧动画、120 fps 飞入；当前是 GDI DIB 占位路径。
- **XAML 界面**：16 项托盘菜单、快速查看、标注、修剪、拿在手上、欢迎窗尚未接 WinUI 窗口。
- 真实 OLE 拖放、MSIX 稀疏包右键扩展、MediaFoundation 修剪、WIC 解码与 HEIC 扩展。
- 多显示器 / 每屏 DPI（v1 主动范围外）、力感应点按的物理一致性。
- 实机 DPI 100/150/200%、深浅色、高对比度、虚拟桌面、任务栏置顶、全屏不抢焦点的手工矩阵。
- MSIX 打包、代码签名与交互模式的人工目视验收。

以上均在 P2–P6 里以**可测模型 + 平台契约 + 待接入说明**的形式落地，并在最终回归报告里逐条标注「自动化已验 / 需实机」。
