# ClipLine · 渲染壳 v0（WinUI 3 宿主 + 分层 HWND 绳窗口）

> 状态：**已实现并验证**（2026-11-05）。规格来源 [#1](https://github.com/dk900912/ClipLine/issues/1)，执行工单 **#9**。
> 视觉基准仍是 `05-ui-design.md` + `assets/ui/prototype.html`；本文记录工具链落地、三个平台阻塞与解法、结构、冒烟覆盖和明确遗留。

## 1. 结论

| 检查 | 结果 |
|---|---|
| `dotnet build src/ClipLine.App` | 0 警告 0 错误 |
| `ClipLine.exe --smoke`（真实桌面会话） | **14 / 14 PASS，退出码 0** |
| 全量 `tools/verify.sh` | 128 条用例全绿（未因渲染壳回归） |
| 环境 | Windows `10.0.26300`、单屏 1920×1080、125% 缩放、`systemDpi=120`、`awareness=2`（PMv2） |

这是**第一个真正跑起来的 WinUI 3 产品进程**：WindowsAppSDK 引导成功、`Application.Start` 消息泵启动、`OnLaunched` 执行，门面 → 分层 HWND → 托盘 → 平台命令的完整链路在真实桌面里闭环。

## 2. 「安装 WinUI 3 工作负载」实际落地了什么

本机没有 Visual Studio，而且 .NET 的 WinUI 3 开发**不需要 VS 工作负载**：所需的是 SDK + NuGet 包 + 运行时。实际装配如下：

| 组件 | 落地方式 |
|---|---|
| .NET SDK `8.0.427` | 官方通道下载后解压到临时目录，**未做系统级安装** |
| `Microsoft.WindowsAppSDK 1.6.250108002` | NuGet 还原（205 MB，含 Framework / Main / Singleton / DDLM 四组 MSIX） |
| `Microsoft.Windows.SDK.BuildTools` | NuGet 还原 |
| Windows SDK `10.0.26100.0` | 本机已装，提供 `mt.exe` |
| WindowsAppRuntime | **不依赖系统包**：self-contained，178 个运行时文件随输出目录部署 |

`Get-AppxPackage` 显示本机只有 `Microsoft.WindowsAppRuntime.1.6 / 1.7 / 1.8` 的 **Framework 包**，缺 Main / Singleton / DDLM —— 这才是「工作负载没装全」的实质，也是下面第 3 个阻塞的根源。

## 3. 三个平台阻塞与解法

| # | 现象 | 根因 | 解法 |
|---|---|---|---|
| 1 | `MSB4062` 无法加载 `ExpandPriContent`（`Microsoft.Build.Packaging.Pri.Tasks.dll` 缺失） | 纯 .NET SDK 没有 VS 自带的 MSBuild PRI 任务 | v0 是 unpacked 应用且无 `.resw` / XAML 资源，`resources.pri` 无内容可索引；在 csproj 关闭 `EnableCoreMrtTooling` / `EnablePriGenTooling` / `AppxGeneratePriEnabled` / `IncludeProjectPriFile`。接多语言资源时随 VS 恢复 |
| 2 | SxS「设置 `…/SMI/2005/WindowsSettings^dpiAwareness` 未注册」，激活上下文失败 | 自定义 `app.manifest` 把 `dpiAwareness` 写在了 2005 命名空间（应为 **2016**） | v0 不嵌入自定义 manifest（self-contained targets 会生成自己的 WinRT 注册清单）；文件保留给 MSIX / 签名阶段并已修正命名空间 |
| 3 | 进程启动即退出 `0x80670016` | `STATEREPOSITORY_E_DEPENDENCY_NOT_RESOLVED`：框架依赖部署的 `[ModuleInitializer]` bootstrap 在 **Main 之前**解析系统包，因缺 Main/Singleton/DDLM 失败并直接 `Environment.Exit(hr)` | 改用 `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` + `<Platform>x64</Platform>` + `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`，运行时 DLL 随应用部署；无需管理员权限、无需改动系统 |

三个问题都不涉及领域逻辑，也没有修改 `docs/02` 的定稿技术栈：`UseWinUI=true` 宿主 + 自管分层 HWND 绳本体，与定稿一致。

## 4. 结构

| 项目 | 职责 |
|---|---|
| `src/ClipLine.App/` | WinUI 3 应用宿主（unpacked）。`Program.Main` 先设 PMv2 DPI 再 `Application.Start`；`App.OnLaunched` 组合 `ShellAppHost`，用 33 ms `DispatcherQueueTimer`（≈30 fps）把真实指针送进门面；`--smoke` 走确定性自检 |
| `src/ClipLine.Shell/` | 组合根 `ShellAppHost`（门面 + 绳窗口 + 托盘 + `ShellCommandSink`）、`RopeWindow`（分层 HWND）、`RopeCanvas`（32bpp DIB 自绘）、`ShellTray`（独立 message-only 宿主 + E「双线」三态图标）、`ShellEnvironment`（主屏 rcWork / 主题 / 减少动效 / 高对比度）、`ShellSmoke` |
| `src/ClipLine.Domain/` · `Application/` · `Platform/` | 保持不变，**唯一测试接缝仍是 `AppFacade`**（离散事件进 → 状态快照 + 外部命令出） |

两个关键接线决定：

- **DPI 时机**：`ShellDpi.EnsurePerMonitorV2()` 必须是 `Main` 的第一句。self-contained 生成的清单不含 DPI 声明，等 XAML 初始化后再设就会停在 UNAWARE（96 DPI），本机 125% 缩放会被系统位图拉伸。
- **帧驱动重绘**：门面的 `PresentWindow` 只在窗口首次呈现时发一次。`ShellAppHost.RenderFrame()` 按帧拉取 `StateSnapshotView`，**仅在快照变化时**重建 DIB，这样新图/固定/取下都能反映到画布，又不会每帧重建。

## 5. 冒烟覆盖（`tools/smoke.ps1`）

真实窗口 + 真实托盘 + 假时钟，经应用门面驱动，14 项：

1. 进程级 DPI 感知 PMv2
2. 托盘图标挂在独立 message-only 宿主
3. 新截图 → peek → `PresentWindow` → 绳窗口可见
4. 窗口样式 `LAYERED | NOACTIVATE | TOOLWINDOW`、不进任务栏、初始穿透
5. 卡片占位按状态渲染
6. 卡片命中时清除穿透
7. 卡片区 `WindowFromPoint` 命中绳窗口
8. 空白区恢复穿透
9. 空白区 `WindowFromPoint` 穿透到下层窗口
10. 收起后门面隐藏绳窗口
11. 0.3 s 后销毁绳窗口
12. `EnumWindows` 无残留绳窗口
13. 托盘与瞬时线窗口解耦
14. 平台命令接收器无异常

假时钟必须推进过 `Layout.WelcomePinMs = 5000`：首次安装会弹欢迎窗并 pin 住绳子，只推进到 peek(2500) 的时序不足以触发 `Hide → Destroy`。

## 6. 运行方式

```bash
# 编译门禁（含领域测试 + 渲染壳）
DOTNET_SDK="/c/Users/.../Temp/clipline-tools/dotnet/dotnet.exe" bash tools/verify.sh

# 真实桌面会话冒烟（PowerShell）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/smoke.ps1
```

`ClipLine.exe` 无参数进入交互模式：30 fps 追踪指针、顶部热区展开绳、托盘右键退出。

## 7. 明确未覆盖（不得误报为完成）

- 渲染仍是 **GDI 32bpp DIB**（P0 验证过的路径），不是定稿的 **Win2D / DirectComposition + `WS_EX_NOREDIRECTIONBITMAP`**；两条路径互斥（P0 结论 #2），升级需另行立项。
- **没有 XAML 窗口**：16 项托盘菜单、快速查看、标注、修剪、拿在手上、欢迎窗目前只有契约与命令，没有 WinUI 界面；托盘右键 v0 仅退出。
- 剪贴板 / 目录监听、OLE 拖放、WIC 解码与 HEIC、MSIX 稀疏包右键扩展、MediaFoundation 修剪未接真实实现。
- MSIX 打包与签名、实机 DPI 100/150/200%、深浅色 / 高对比度、虚拟桌面、任务栏置顶、全屏不抢焦点的手工矩阵仍未做。
- 交互模式只做过程序级验证，**没有人工目视验收**。
