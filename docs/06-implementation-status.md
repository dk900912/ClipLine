# ClipLine · 实现状态与工程约定

> 本文随 P1→P6 逐阶段更新，记录**真实可编译、可运行**的代码范围，以及本机环境造成的能力边界。
> 规格来源：[#1](https://github.com/dk900912/ClipLine/issues/1)；功能基线：`01-feature-alignment.md`；视觉与交互基准：`05-ui-design.md` + `assets/ui/prototype.html`。

## 1. 本机环境事实（2026-11-04）

| 项 | 状态 | 影响 |
|---|---|---|
| .NET 8 运行时 | 有 | 可运行 `net8.0` 程序 |
| .NET SDK | **系统未安装**；从官方通道下载 `8.0.427` 解压到临时目录 | 纯 C# 项目可编译；不做系统级安装、不改用户环境 |
| Visual Studio / Windows App SDK 工作负载 | **无** | WinUI 3 / Win2D / Composition / CsWin32 项目**当前编译不了** |
| Windows SDK 头文件 | 有 `10.0.26100.0` | Win32 P/Invoke 可行 |
| Node.js | 有 | 仅用于校验 HTML 语法 |
| Python | 无（Store 占位符） | 不使用 |

**因此**：P1–P6 把**全部领域逻辑、状态机、捕获规则、交互语义、菜单模型、本地化与验收矩阵**落成零 NuGet 依赖的纯 C#，可在本机编译并跑测试；Win2D / Composition / XAML 的**渲染与窗口层**按定稿接口留契约与参数，不伪造「能跑的界面」。P0 已用真实 Win32 分层 HWND 验证过平台假设（见 #2 结论与 `spikes/p0-layered-hwnd/`）。

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
```

`tools/verify.sh` 先编译 `ClipLine.sln` 涉及的四个项目（Domain / Application / Platform / Tests），再跑全部测试；退出码非 0 表示有用例失败。

### 3.1 P2 的平台适配器（`src/ClipLine.Platform/`）

零 NuGet 依赖、可编译的真实 Win32 P/Invoke 封装：`AddClipboardFormatListener`（消息驱动，空闲零开销）、`ReadDirectoryChangesW`（`FILE_NOTIFY_INFORMATION` 解析 + 0.2 s 防抖）、`SystemParametersInfoW(SPI_GETWORKAREA)` 取主屏工作区、`GetDpiForSystem` 换算单一系统缩放、`RegisterHotKey`、`SHFileOperationW(FOF_ALLOWUNDO)` 回收站、以及 P0 结论要求的**独立 message-only 托盘宿主窗口**。`PlatformCommandSink` 把门面命令映射到这些适配器，渲染类命令显式记录为未处理，由后续 WinUI 层消费。

正式产品按 `02-tech-stack.md` 仍改用 **CsWin32 源生成器**；当前手写声明只为在没有工作负载的本机把平台边界编译验证一遍。

## 4. 阶段进度

| 阶段 | 工单 | 状态 | 主要交付 |
|---|---|---|---|
| P0 | #2 | ✅ 已关闭 | 透明分层 HWND 技术切片（`spikes/p0-layered-hwnd/`，20/20 自检） |
| P1 | #3 | ✅ 已完成（提交 `648869b`） | 领域模型 + 应用门面 + 46 条用例 |
| P2 | #4 | ✅ 已完成 | 三层捕获准入、完整性校验、剪贴板过滤、飞入参数、Win32 适配器 + 23 条用例 |
| P3 | #5 | ✅ 已完成 | 手势状态机、右键菜单条件、拖放目标语义、键盘借用、热键录制 + 20 条用例 |
| P4 | #6 | ✅ 已完成 | 快速查看 / 标注 / passthrough 修剪 / 拿在手上 / 右键扩展的可测模型 + 15 条用例 |
| P5 | #7 | ✅ 已完成 | 16 项菜单模型与可用性、11 语言 key 校验、主题/无障碍令牌、MSIX 清单草稿 + 15 条用例 |
| P6 | #8 | 待推进 | 功能全表回归、性能与隐私放行 |

## 5. 明确未覆盖（不得误报为完成）

- WinUI 3 / Win2D / Composition 的可运行界面、真实 Acrylic / 弹簧动画、120 fps 飞入。
- 真实 OLE 拖放、MSIX 稀疏包右键扩展、MediaFoundation 修剪、WIC 解码与 HEIC 扩展。
- 多显示器 / 每屏 DPI（v1 主动范围外）、力感应点按的物理一致性。
- 实机 DPI 100/150/200%、深浅色、高对比度、虚拟桌面、任务栏置顶的手工矩阵。

以上均在 P2–P6 里以**可测模型 + 平台契约 + 待接入说明**的形式落地，并在最终回归报告里逐条标注「自动化已验 / 需实机」。
