# ClipLine · Windows 11 技术选型（已定稿）

> **决策记录（2026-11-04）**：用户确认「其他按推荐落地」——推荐方案 A（C# .NET 8 + WinUI 3 + Win2D/Composition + CsWin32 自管分层 HWND）、分层架构、性能预算与 P0→P6 实施顺序**全部定稿**，按此执行。
>
> **v1 范围裁剪（同日）**：砍掉多显示器与每屏 DPI 的适配及测试，只保证**单显示器 + 单一系统缩放**；单屏 100% / 125% / 150% / 200% 缩放仍属验收范围。

## 1. 先定约束，再选栈

Tendedero 的体验之所以成立，靠的是几件对技术栈很挑剔的事：

| 约束 | 对技术栈的硬要求 |
|---|---|
| 3.5 MB 体积、常驻内存极低、空闲几乎不耗 CPU | 不能带浏览器运行时；不能常驻轮询；解码要离屏、缩略图要限量 |
| 全屏宽透明窗口、**默认鼠标穿透、仅卡片区可点** | 需要能直接控制 HWND 样式与命中测试，不能只靠 XAML 的 `Window` |
| 60/120 fps 的弹性、抛物线、逐帧飞入动画 | 需要 GPU 加速的 2D 绘制与合成动画（不是 XAML Storyboard 勉强凑） |
| 系统级能力：全局热键、剪贴板、目录变更、OLE 拖放、回收站、托盘、虚拟桌面 | 需要顺畅的 Win32/COM 互操作 |
| 原生观感（Fluent：Mica/亚克力、深浅色、高对比度、系统字体与图标） | 用 Windows App SDK 的原生控件与系统画刷 |
| 本地优先、无账号、无遥测（v1.5 起允许唯一白名单内的 GitHub Release 更新） | 不能选默认带遥测/强制更新器/账号体系的框架；网络能力必须自管且可审计 |
| 品牌全新（名称/图标不得沿用） | 所有视觉资源必须自绘、可矢量化、可代码生成 |

结论：**选 C# / .NET 8+ / WinUI 3 作外壳与菜单，但把「晾衣绳」本体放进一个自管的 Win32 分层窗口，用 Win2D + Composition 自绘。** 这是唯一同时满足「原生观感 + 像素级控制 + 低资源 + 开发效率」的组合。

---

## 2. 候选方案对比

| 方案 | 原生观感 | 体积/资源 | 透明窗口与命中控制 | Win32/COM 互操作 | 开发效率 | 结论 |
|---|---|---|---|---|---|---|
| **A. C# .NET 8 + WinUI 3 + Win2D/Composition + CsWin32**（推荐） | ★★★★★ | 框架依赖时 5–15 MB；空闲 < 0.1% CPU、~50 MB | ★★★★★ 自管 HWND + DirectComposition | ★★★★★ 源生成器生成 P/Invoke | ★★★★ | **选它** |
| B. C++/WinRT + WinUI 3 / 纯 Win32 + Direct2D | ★★★★★ | 最接近 3.5 MB、内存最省 | ★★★★★ | ★★★★★ | ★★ | 性能最优，但领域模型/状态机/编辑器全用 C++ 写，工期与维护成本翻倍；留作 v2 优化 |
| C. C# .NET + WPF（透明 Window + HwndSource） | ★★★ | 30–60 MB、启动更重 | ★★★★ `AllowsTransparency` + `IsHitTestVisible` 可控 | ★★★★ | ★★★★★ | 最快出原型，但渲染管线旧、DPI 坑多、Fluent 需自己补；**可作 2 周验证原型** |
| D. Tauri / Rust + WebView2 | ★★ | WebView2 运行时 ~100 MB+，多进程 | ★★ WebView 无法做透明全屏条带 + 逐像素命中 | ★★★ | ★★★★ | ❌ 不是原生体验，且拖放/热键/托盘都要绕层 |
| E. Electron | ★ | > 100 MB / > 150 MB 内存 | ★ | ★★ | ★★★★★ | ❌ 直接淘汰 |
| F. Flutter Windows | ★★ | 运行时较大、自绘但不跟系统主题 | ★★★ | ★★★ | ★★★★ | ❌ 系统菜单、拖放、Mica 都需插件，且不是原生 |
| G. Rust + windows-rs + Direct2D 全自绘 | ★★★★★ | 最接近原版 | ★★★★★ | ★★★★★ | ★ | 技术上最漂亮，但标注编辑器 / 视频修剪 / 11 语言 / 无障碍都要从零造，**不适合作为首发** |

> 一个现实提醒：**3.5 MB 这个数字不可直接照搬**。macOS 版是 Swift 系统框架动态链接；Windows 上若选框架依赖的 Windows App SDK，安装包可以很小，但要求目标机已装运行时（Win11 上通常有）。若要求「解压即用」，走自包含 + 裁剪（NativeAOT）大约 20–40 MB。建议首发用 **MSIX 框架依赖包**，安装包控制在 15 MB 内，并把运行时作为前置依赖声明。

---

## 3. 推荐技术栈明细

| 层 | 选型 | 理由 |
|---|---|---|
| 语言 / 运行时 | **C# 12 / .NET 8 LTS**（可平滑升 .NET 10）；`Nullable enable`、`ImplicitUsings` | 领域模型可从 Swift 结构近乎直译，异步/取消/弱引用成熟，NativeAOT 有升级路径 |
| UI 框架 | **WinUI 3 / Windows App SDK 1.6+** | Fluent 控件、`Mica`/`Acrylic` 系统画刷、系统主题与高对比度、`MenuFlyout`、XAML 岛 |
| 绳本体渲染 | **Win2D (`Microsoft.Graphics.Canvas`)** 画绳/卡片/夹子 + **Windows.UI.Composition** 做弹簧/摆动动画 | GPU 加速；可用 `CompositionSpring`/`KeyFrameAnimation` 精确复刻 spring 参数；支持逐帧 120 fps 飞入 |
| 窗口宿主 | **自管 HWND**（`CreateWindowEx`），`WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`，`WS_EX_NOREDIRECTIONBITMAP` + DirectComposition | 只有自管 HWND 才能可靠做到「整屏点击穿透 + 局部命中」与「收起后不留窗口」 |
| Win32 互操作 | **CsWin32** 源生成器（生成 `Shell_NotifyIcon`、`RegisterHotKey`、`AddClipboardFormatListener`、`ReadDirectoryChangesW`、`DoDragDrop`、`SHFileOperation` 等） | 免手写 P/Invoke，编译期生成、AOT 友好，比 `DllImport` 手撸安全得多 |
| 图像解码 | **WIC (`Windows.Graphics.Imaging` / `IWICImagingFactory`)**；HEIC 走 HEIF 扩展，缺失时降级提示 | 支持 png/jpg/jpeg/heic/tif/tiff/gif/webp；缩略图按 `320 × 缩放` 上限，后台线程解码 |
| 视频首帧 / 剪辑 | **MediaFoundation + `Windows.Media.Editing` / `MediaTranscoder`**（或 `Windows.Media.Transcoding`） | 系统自带、无需第三方解码器；passthrough 导出到隐藏临时文件后原子替换 |
| 标注编辑器 | 自研 **Win2D + InkCanvas / 自绘图层**（矩形、箭头、文字、马赛克、裁剪），WIC 按原格式写回 | Windows 没有系统 Markup 扩展；自研才能做到「写回原文件且体积不膨胀」 |
| 预览窗口 | WinUI `Window` + `Image` / `MediaPlayerElement`，← → 浏览、Space/Esc 关闭 | 对应 Quick Look |
| 全局热键 | `RegisterHotKey` / `UnregisterHotKey`（`WM_HOTKEY`） | 与 Carbon `RegisterEventHotKey` 等价：**不需要辅助功能权限**；动态注册实现「只在线内借用 Ctrl+Z」 |
| 剪贴板 | `AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE`，`OleGetClipboard` 读 `IDataObject` | 消息驱动，替代 0.5 秒轮询，空闲零开销 |
| 文件监听 | `ReadDirectoryChangesW`（FILE_ACTION_ADDED / RENAMED_NEW_NAME / MODIFIED）+ 0.2 s 防抖 | 对应 `DispatchSource`，低开销 |
| 拖放 | OLE `IDataObject` + `IDropSource` + `DoDragDrop`，`IDragSourceHelper` 做半透明预览 | 对应 `NSDraggingSource` 的 copy/move/delete 语义 |
| 回收站 | `IFileOperation` + `FOF_ALLOWUNDO`，配合 ClipLine 自有软删除索引 | 保证 20 步撤销可恢复 |
| 托盘 | `Shell_NotifyIconW`（`NIF_ICON/NIF_TIP/NIF_MESSAGE`）+ WinUI `MenuFlyout` 或自绘 Win32 菜单 | **三态图标**（默认 / 监听开启 / 空绳变暗，E 定稿）、复选子菜单 |
| 设置 / 状态 | `%LOCALAPPDATA%\ClipLine\settings.json` + `state.json`，`System.Text.Json` 源生成 | 对应 UserDefaults；原子写 + 备份 |
| 本地化 | `.resw` + `ResourceLoader`；脚本校验 11 语言 key 一致 | 对应 `.lproj` + `check-strings.swift` |
| 打包 / 分发 | **MSIX（`runFullTrust` + `startupTask` + 稀疏包右键扩展）**；同时提供便携 zip | 干净卸载、开机自启受系统管理、右键扩展可声明；Authenticode 签名避免 SmartScreen |
| 测试 | xUnit（领域模型 / 状态机）+ 手工交互验收清单 + WinAppDriver（可选） | `Line` 的容量、撤销、排序逻辑是纯函数式，最适合先测 |

---

## 4. 分层架构

```
ClipLine.sln
├── src/ClipLine.Domain/           纯 C#，零 UI 依赖（可 1:1 直译 Swift 的 Line.swift）
│   ├── Pegged.cs / Line.cs / TakenDown.cs / UndoSteps.cs
│   ├── LineStateMachine.cs        wanted/pinned/peek/blocked 显式状态
│   ├── Layout.cs                  sag / ropeY / spacing / cardWidth / x()
│   └── CaptureIntegrity.cs        PNG IEND / JPEG EOI / GIF / WebP / HEIC 校验
├── src/ClipLine.Platform/         Win32 / COM 互操作（CsWin32 生成 + 手写封装）
│   ├── TrayIcon.cs / HotKey.cs / ClipboardListener.cs / FolderWatcher.cs
│   ├── DragDrop.cs / RecycleBin.cs / FullScreenDetector.cs / VirtualDesktop.cs
│   ├── DisplayInfo.cs             主屏 rcWork + 单一系统缩放
│   └── LineWindow.cs              分层透明 HWND、WS_EX_TRANSPARENT 动态切换
├── src/ClipLine.Rendering/        Win2D + Composition
│   ├── RopeRenderer.cs / CardRenderer.cs / ClipRenderer.cs / KnotRenderer.cs
│   └── CaptureFlightAnimator.cs  0.65 s 飞入 / 0.55 s 下落
├── src/ClipLine.App/              WinUI 3：托盘菜单、设置、预览窗、标注编辑器、剪辑窗
├── src/ClipLine.ShellExtension/   稀疏包 IExplorerCommand「挂到 ClipLine」
└── tests/ClipLine.Domain.Tests/
```

**关键分层原则**：`Domain` 不引用任何 Windows API。这样 Tendedero 的 `Line.swift` 里的容量、撤销、固定、批量取下、放回规则可以逐条写成单元测试，验收时用同一份用例对照 macOS 行为，是「100% 对齐」最可靠的抓手。

---

## 5. 性能预算与实现纪律

| 指标 | 目标 | 手段 |
|---|---|---|
| 空闲 CPU | < 0.1% | 无轮询：剪贴板用消息、目录用 `ReadDirectoryChangesW`、指针 30 fps 计时器**仅在线存在时运行**；线收起后立即停止 |
| 常驻内存 | ≤ 60 MB（20 张卡片） | 每张卡只留一张 `320×scale` 缩略图；飞入用 400–1500 px 临时图且用完即释放；最多 2 个并发飞行 |
| 展开延迟 | < 16 ms 首帧 | 预创建窗口、预合成绳与夹子；展开只改 Composition 偏移 |
| 截图上线延迟 | < 100 ms（系统截图零等待） | 完整性校验对完整 PNG 一次读尾即通过，不做额外 sleep |
| 启动时间 | < 1 s 到托盘就绪 | 不在启动路径上枚举大目录；首次枚举放后台，启动前文件只登记不挂 |
| 电池 | 无后台唤醒 | 无 Timer 常驻；`PowerSettingNotification` 感知挂起/恢复 |

另外三条纪律：

1. **不引入任何联网依赖**：NuGet 包在构建期锁定并本地缓存；运行时不做版本探测、崩溃上报、遥测。
2. **不引入 WebView / Chromium / Node 运行时**，也不引入 FFmpeg 之类的大型二进制。
3. **所有视觉资产代码绘制**（绳、夹子、玻璃框、E「双线」Logo、托盘三态图标），既满足「全新品牌」约束，也把体积压到最低；SVG/矢量在构建期栅格化成多分辨率图标资源。

---

## 6. 实施顺序建议（按风险从高到低）

| 阶段 | 内容 | 为什么先做 |
|---|---|---|
| **P0 验证原型（1 个技术切片）** | 自管 HWND 透明全屏条带 + 局部命中 + 30 fps 顶部热区 + 托盘图标；画一条抛物线与一张卡片 | 这是整个项目最大的不确定性；跑通了才谈其余 |
| **P1 领域模型** | 直译 `Line.swift`：容量、固定、批量取下、20 步撤销、持久化；xUnit 覆盖 | 纯逻辑、可对照源码逐条验收 |
| **P2 捕获链路** | 剪贴板监听 + 目录监听 + 完整性校验 + 缩略图 + 飞入动画 | 「截一张图马上挂上」是产品核心价值 |
| **P3 交互全集** | 单击/双击/长按/角标/右键菜单/OLE 拖放/键盘借用/全局热键 | 交互密度最高的部分 |
| **P4 系统替身** | 预览窗、标注编辑器、录屏剪辑、拿在手上浮动窗、资源管理器右键扩展 | Windows 上要自己造的四件套 |
| **P5 设置与打磨** | 16 项菜单、11 语言、深浅色/高对比度/减少动效、音效、开机自启、包签名 | 对齐「细节 100%」 |
| **P6 回归** | 按 `01-feature-alignment.md` 全表逐项验收 + 单屏缩放/虚拟桌面/全屏/任务栏置顶矩阵（v1 不含多屏） | 交付门槛 |

> 建议 **P0 用 WPF 或甚至纯 Win32 做一次性技术验证**，确认「透明整屏窗口 + 局部命中 + 不抢焦点 + 上屏不被 DWM 裁切」在目标 Win11 版本上都成立，再正式落到 WinUI 3。这个验证能提前暴露后面 80% 的平台坑。
