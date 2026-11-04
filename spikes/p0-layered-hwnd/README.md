# ClipLine P0 平台验证切片 · **THROWAWAY**

> **这不是产品代码。** 它是一次性技术验证原型，用于回答 #2 / P0 的那个平台问题；验证结论写回工单后，产品实现仍按定稿的 C# .NET 8 + WinUI 3 + Win2D/Composition + CsWin32 自管分层 HWND 落地。
> 按仓库的 prototype 约定，本目录应留在 **throwaway 分支**，不要把原型的绘制路径或 P/Invoke 写法直接搬进 `src/`。

## 要回答的问题

Windows 11 上，自管分层 HWND 能否同时做到：

1. 全屏宽、透明、置顶、不抢焦点、不在 Alt+Tab / 任务栏出现；
2. **默认整窗点击穿透，仅卡片命中矩形可点**（动态切换 `WS_EX_TRANSPARENT`）；
3. 顶部中间 1/3 热条停留 `0.25 s` 展开、移开 `0.5 s` 收起、收起 `0.3 s` 后窗口被真正销毁；
4. 收起销毁后仍能从热区重新创建，且托盘图标不随之消失；
5. 上述时序可以用**假时钟确定性验证**，不依赖真实 `sleep` 或鼠标。

## 环境

- Windows 11（本切片在 `10.0.26300` 上验证），x64
- .NET 8 SDK（本机原先只有运行时，SDK 解压在临时目录，未做系统级安装）
- **零 NuGet 依赖**：只用 BCL + Win32 P/Invoke，`dotnet build` 可离线完成

## 构建与运行

```bash
dotnet build -c Release spikes/p0-layered-hwnd/ClipLine.P0Spike.csproj
spikes/p0-layered-hwnd/bin/Release/net8.0-windows/ClipLine.P0Spike.exe --self-test
spikes/p0-layered-hwnd/bin/Release/net8.0-windows/ClipLine.P0Spike.exe
```

- `--self-test`：注入虚拟指针坐标与假时钟（t = 0 / 100 / 260 / 300 / 350 / 400 / 700 / 950 / 1100 / 1300 / 1400 / 1700 ms），逐项输出 `PASS` / `FAIL` 与 `VERDICT`，然后自动清理退出。**不移动真实鼠标**。
- 无参数：交互模式，`SetTimer` 约 30 fps 追踪真实指针，可看到顶部绳与一张卡片；**右键托盘图标退出**。

## 自检项与 #2 验收口径的对应

| 自检项 | #2 验收 / 功能对齐清单 |
|---|---|
| 进程级 DPI 感知 PMv2 | 适配方案 §1、UI 规范 §10 |
| LAYERED / NOACTIVATE / TOOLWINDOW 样式、无 APPWINDOW | `V-01`、`A-01`、适配 §1 |
| `UpdateLayeredWindow` 上屏 | `V-07` 抛物线与卡片可见 |
| 初始 `WS_EX_TRANSPARENT` + 卡片命中动态切换 + `WindowFromPoint` 双向验证 | `V-06`、`V-16` 命中矩形外扩 4 |
| 热区 `0.25 s` / 收起 `0.5 s` / 销毁 `0.3 s` | `S-03`、`S-04`、`V-05` |
| `EnumWindows` 无残留 + 可重新创建 | `V-05`、状态机契约 |
| 托盘 message-only 宿主 + 16 px E「双线」图标 | `T-01`（占位） |

## 实现要点

- **32bpp 预乘 alpha DIB**：`CreateDIBSection` 用负高度得到 top-down 位图，逐像素做 source-over 混合（0xAABBGGRR），`UpdateLayeredWindow` + `AC_SRC_ALPHA` 上屏。绳按源码公式 `sag = min(30, 宽×0.018)`、`ropeY(x) = 10 + 4·sag·(x/w)·(1−x/w)` 画三层描边并做两端 8% 渐隐；卡片 `150×104`、圆角 16 / 照片圆角 12、夹子 `9×26`。
- **两个窗口，职责分离**：瞬时「线窗口」负责显示，收起 0.3 s 后 `DestroyWindow`；长期「message-only 宿主窗口」负责托盘图标与 `WM_TIMER`。这是切片验证出来的一条实现结论——托盘不能挂在线窗口上，否则每次收起都会把图标一起销毁。
- **一个可注入的状态步**：`Tick(px, py, nowMs)` 接收坐标与时间，交互模式传真值、自检模式传假值。因此时序断言完全确定性，也和综合规格里「应用门面」接缝的纪律一致。
- **托盘图标程序绘制**：16×16 直接画 E「双线」图案（两条线 + 下垂弧 + 暖橙夹子），用 `CreateIconIndirect` 生成 HICON，不依赖 SVG 栅格化工具；产品实现仍应使用 `E-tray-16*.svg` 构建期生成的多分辨率资源。

## 已知简化（有意不做，别拿它当产品实现）

- 绘制走 **GDI `UpdateLayeredWindow`**，不是产品级的 Win2D + DirectComposition；因此**不设** `WS_EX_NOREDIRECTIONBITMAP`（该样式属于产品路径，与 GDI 分层窗口互斥）。自检里专门验证了这一点。
- 没有 spring 展开 / 收起位移、飞入、下落、微风动画；展开收起只用 `ShowWindow`，视觉参数以 UI 规范 §9 为准，留给 P3 / P5。
- 卡片是程序绘制的示意图，不接真实截图、剪贴板或目录监听；没有卡片手势、右键菜单、拖放、热键。
- 托盘只有图标与右键退出，没有 16 项菜单；没有设置持久化、本地化、主题跟随、高对比度、UIA。
- 进程级 DPI 感知已开，但布局按物理像素计算；产品实现必须按 DIP 存储、绘制时乘 `GetDpiForWindow/96`。
- v1 范围为单显示器 + 单一系统缩放，本切片不枚举显示器、不处理热插拔。

## 本机验证结果（2026-11-04）

`VERDICT: PASS (20/20)`，退出码 0。环境：Windows `10.0.26300` x64、单屏 `1920×1080`、系统缩放 125%（`systemDpi=120`）、`.NET SDK 8.0.427`（临时目录解压，未做系统级安装）。

- 20 项全绿：PMv2 DPI 感知、窗口样式、初始穿透、Alt+Tab/任务栏排除、GDI 路径样式约束、`UpdateLayeredWindow` 上屏、热区/收起/销毁三段时序、卡片与空白区的 `WindowFromPoint` 双向命中、`EnumWindows` 无残留、销毁后可重建、托盘宿主解耦。
- 首轮为 `19/20`：唯一 FAIL 是自检代码自身 `GetDpiForWindow(NULL)` 返回 0（该函数要求有效 HWND），改用 `GetDpiForSystem()` 后全绿；**不是平台能力问题**。
- `--self-test` 会额外导出 `bin/Release/net8.0-windows/p0-preview.bmp`（把预乘 alpha 画布合成到深蓝桌面底色），用于目视核对抛物线与卡片几何；已核对：绳下垂与两端渐隐、圆角玻璃卡片、夹子、示意标题条均正常。

**仍未覆盖**：只在 125% 单屏缩放验证；100/150/200%、深浅色、高对比度、全屏应用下不抢焦点的实机目视、16 px 托盘三态辨识度，均留给 P5 / P6 的回归矩阵。交互模式（无参数）可在本机手动跑一遍真实鼠标。

## 结论去向

自检结果（含每项 PASS / FAIL、环境与关键发现）以评论形式写回 GitHub Issue **#2**；若某项 FAIL，把原因与对产品实现的影响一并记录，不静默略过。
