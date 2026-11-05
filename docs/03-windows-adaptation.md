# ClipLine · macOS → Windows 11 适配难点与解决方案

> 对照 `01-feature-alignment.md` 的 `N-01 … N-11` 与 `⚠️` 项。每一节给出：**macOS 怎么做 → Windows 差在哪 → 具体方案 → 验收口径**。
>
> 总原则：**功能结果对齐，系统路径可替换**；凡是 Windows 没有对应私有能力的，用平台等价实现补齐，并在设置里给出可见的开关或说明，不偷偷降级。
>
> **v1 范围裁剪（2026-11-04 用户决策）**：多显示器与每屏 DPI 的适配及测试用例一并删除，只保证**单显示器 + 单一系统缩放**；原「多显示器 / 每显示器 DPI」一节已移除，回归矩阵相关维度已剔除。单屏 100% / 125% / 150% / 200% 缩放测试保留。
>
> **决策确认（同日）**：其余 17 项难点按本文推荐方案定稿执行。

## 差异总览

| # | 难点 | 严重度 | Windows 等价路径 |
|---:|---|---|---|
| 1 | 透明整屏窗口 + 局部鼠标穿透 + 不抢焦点 | 🔴 最高 | 自管分层 HWND + 动态 `WS_EX_TRANSPARENT` |
| 2 | 截图「接管」机制不存在 | 🔴 最高 | 剪贴板监听 + 默认目录监听，不改系统设置 |
| 3 | 顶部菜单栏热区不存在 | 🔴 高 | 屏幕顶边中间 1/3 热条；任务栏置顶时避让 |
| 4 | OLE 拖放的 copy / move / 回收站语义 | 🔴 高 | `DoDragDrop` + 目标 PIDL 判定 + 0.6 s 复查 |
| 5 | 回收站路径不可得，撤销难做 | 🟠 高 | 自有软删除索引 + Shell 还原 |
| 6 | Quick Look / Markup / 修剪器都没有 | 🟠 高 | 自研预览窗 / 标注 / MediaFoundation 修剪 |
| 7 | 虚拟桌面与全屏检测 | 🟠 中高 | `IVirtualDesktopManager` + 前台窗口/通知状态 |
| 8 | 力感应点按 | 🟠 中 | 中键 / 触控笔压感 / 可配置长按组合 |
| 9 | 剪贴板私密标记无标准 | 🟠 中 | 消息驱动 + 格式白名单 + 进程黑名单 |
| 10 | 全局热键与「临时借用」键 | 🟡 中 | `RegisterHotKey` 动态注册/注销 |
| 11 | 托盘三态图标（E 定稿）与 16 项菜单 | 🟡 中 | `Shell_NotifyIconW` + WinUI `MenuFlyout` |
| 12 | 文件完整性判定 | 🟡 中 | 同字节校验 + 独占打开 + 0.5 s 稳定 |
| 13 | 开机自启 | 🟢 低 | MSIX `startupTask` / 注册表 Run |
| 14 | 资源管理器右键扩展 | 🟡 中 | 稀疏 MSIX + `IExplorerCommand` |
| 15 | 深浅色 / 高对比度 / 减少动效 | 🟡 中 | `UISettings` / `WM_SETTINGCHANGE` |
| 16 | 本地化与无障碍 | 🟡 中 | `.resw` + 自绘卡片的 UIA Provider |
| 17 | 声音、录屏来源、更新检查、签名 | 🟢 低 | 资源音效 / 监视 Captures / 浏览器跳转 / 代码签名 |

---

## 1. 🔴 透明整屏窗口、局部命中、不抢焦点

**macOS**：`NSPanel` 设 `borderless + nonactivatingPanel`、`isOpaque=false`、`level=.floating`、`ignoresMouseEvents=true`、`canBecomeKey=false`、`collectionBehavior=[.canJoinAllSpaces, .fullScreenAuxiliary]`；每帧根据指针是否落在卡片命中矩形动态切换 `ignoresMouseEvents`；收起 0.3 s 后 `orderOut`，**不留下任何窗口**。

**Windows 差异**：WinUI 3 的 `Window` 自带非客户区、不能做逐像素命中；`WS_EX_TRANSPARENT` 一旦设置就**整窗**穿透，无法「部分可点」；`WS_EX_LAYERED` + `UpdateLayeredWindow` 与 DirectComposition 不能同时用同一 HWND 的所有路径。

**方案**

1. 用 `CreateWindowEx` 建自管 HWND：`WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP`，`SetWindowPos(HWND_TOPMOST)`，内容用 `Windows.UI.Composition` 的 `DesktopWindowTarget` + Win2D 交换链绘制（alpha 预乘）。
2. **点击穿透用动态切换而非 `WM_NCHITTEST` 一把梭**：30 fps 追踪指针，若命中任一卡片矩形（外扩 4 pt）则清除 `WS_EX_TRANSPARENT`，否则置位；与原版 `updateMousePassThrough` 完全同构。切换用 `SetWindowLongPtr` + `SetWindowPos(..., SWP_NOACTIVATE|SWP_FRAMECHANGED)`，避免闪烁。
3. 无任务栏按钮：`WS_EX_TOOLWINDOW` + 不设 `WS_EX_APPWINDOW`，且不 `ShowWindow(SW_SHOW)` 到任务栏；进程主线程只跑消息循环。
4. 收起后 0.3 s `DestroyWindow` / `ShowWindow(SW_HIDE)`，保证不留下不可见顶层窗口（否则会影响屏幕自动化、窗口枚举、Alt+Tab 与截图工具）。
5. `DwmSetWindowAttribute(DWMWA_CLOAKED)` 不要滥用；线收起时直接隐藏即可。
6. 处理 `WM_DPICHANGED`、`WM_SETTINGCHANGE`、`WTSSESSION_CHANGE`（锁屏时收起并停止追踪）。进程启动时仍调用 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`——这是 Win11 上避免系统位图拉伸的必要设置，**与是否支持多屏无关**；所有布局常量按 DIP 存储，绘制时乘 `GetDpiForWindow(hwnd)/96`。`WM_DPICHANGED` 只读取**单一系统缩放**并重算布局，v1 不枚举显示器、不处理热插拔重排。

**验收**：线展开时点空白区域能点到下面的应用；点卡片能触发单击/拖拽；`Alt+Tab` 与任务栏不出现 ClipLine；收起后用 `EnumWindows` 找不到残留窗口；全屏视频播放时不被抢焦点。

---

## 2. 🔴 截图「接管」机制不存在（最大的一处不等价）

**macOS**：`Inbox.apply()` 通过 `CFPreferencesSetAppValue` 改 `com.apple.screencapture`：把 `location` / `location-screenshot` / `location-screenrecording` 指向自有目录、`target*` 设为 `file`、`show-thumbnail` 设为 false（关掉浮动缩略图 = 消除 5 秒落盘延迟），退出时 `restore()` 还原。

**Windows 差异**：没有用户级「截图保存位置 / 目标」的偏好设置可改；`Win+Shift+S` 默认只进剪贴板，`PrtScn` 进剪贴板；Win11 截图工具的「自动保存截图」只固定到 `图片\Screenshots`；`Win+Alt+R`（Xbox Game Bar）录屏落到 `视频\Captures`；第三方工具各有目录。**也没有 macOS 那个 5 秒浮动缩略图**，所以「关掉它」这一条在 Windows 上是 N/A，反而更即时。

**方案（三层捕获，全部本地）**

1. **主路径 · 剪贴板**：`AddClipboardFormatListener` 监听 `WM_CLIPBOARDUPDATE`。命中图像格式时把图片转 PNG 存到 `%LOCALAPPDATA%\ClipLine\Screenshots\`，文件名 `Screenshot yyyy-MM-dd at HH.mm.ss.png`，原子写 + ACL 仅当前用户。这样 `Win+Shift+S`、`PrtScn`、以及应用内复制图片都能捕获，且不改任何系统设置。
2. **副路径 · 默认目录**：默认监听 `%USERPROFILE%\Pictures\Screenshots`（截图工具自动保存）、`%USERPROFILE%\Videos\Captures`（Game Bar 录屏）、以及用户自选的最多 3 个目录；用 `ReadDirectoryChangesW`。
3. **可选 · 自带选区截图热键**：不抢占 `Win+Shift+S`，另提供可配置组合（如 `Ctrl+Alt+Shift+S`），用 `Windows.Graphics.Capture` + 自绘选区 UI 得到**真实捕获矩形**，写盘同时把矩形写进自有 sidecar 文件。这样飞入动画的起点能与 macOS 一致（见 §4）。默认关闭，避免破坏「同样的快捷键、同样的肌肉记忆」的原始承诺。
4. 「接管截图」菜单项在 Windows 上改名为语义等价的**「接管新截图」**：开启 = 启用剪贴板捕获 + 默认目录监听；关闭 = 只处理显式拖入/右键挂上的图片。首次运行仍保留「是否让 ClipLine 接管新截图？」询问（只问一次），但文案改成描述本地捕获，不承诺修改系统设置。
5. 备份/还原逻辑保留为「用户选择的被监视目录列表」的快照，退出时无需回滚系统设置 —— 这是 Windows 上更安全的等价物。

**验收**：`Win+Shift+S` 选区后 < 100 ms 挂上；`PrtScn` 挂上；开启截图工具「自动保存」后不重复挂；Game Bar 录完 `.mp4` 带播放按钮挂上；卸载后系统截图设置零残留。

---

## 3. 🔴 顶部菜单栏热区

**macOS**：热区 = 菜单栏带的**中间 1/3**（`revealBand`），停留 0.25 s 展开；点击菜单栏任意处立即收起并保持压制直到指针离开；菜单栏自动隐藏时用系统厚度兜底。

**Windows 差异**：Win11 任务栏默认在**底部**；顶部既没有系统菜单栏，也没有「菜单栏中间」这个概念；任务栏可以被用户拖到顶部；顶边中央还是 **Win11 贴靠布局（Snap Layouts）**的触发区；还有自动隐藏任务栏。

**方案**

1. 定义「顶部带」：取**主显示器** `GetMonitorInfo(MONITOR_PRIMARY).rcWork` 与 `rcMonitor` 之差求系统保留区；任务栏不在顶部时，取屏幕顶边 **8–12 DIP** 高的不可见热条；高度不足 1 时用 `GetSystemMetrics(SM_CYSIZE)` 兜底，逻辑与 `menuBarBand(of:)` 同构。v1 不枚举其他显示器。
2. `SHAppBarMessage(ABM_GETTASKBARPOS)` 判断任务栏位置：在顶部时，热区改为任务栏**中段空白区**，并排除开始按钮、搜索框、托盘区与窗口按钮；中段仍按 `宽/3` 计算。
3. 热区命中 + 停留 0.25 s（30 fps 计时）才展开；空绳不展开；`FullScreen.blocksLine` 时阻挡。
4. 全局鼠标按下检测用 `WH_MOUSE_LL` 钩子（仅取 `WM_LBUTTONDOWN/WM_RBUTTONDOWN`，不做键盘记录），命中顶部带即 `menuBarSuppressed = true`；指针离开顶部带时解除。钩子常驻但只做一次坐标比对，开销可忽略；也可退化为 30 fps 下 `GetAsyncKeyState` 轮询以避免全局钩子带来的杀软误报。
5. **贴靠布局冲突**：拖拽窗口到顶边时 Windows 会弹出贴靠栏；我们的线窗口设 `WS_EX_NOACTIVATE|WS_EX_TRANSPARENT`，默认不吃鼠标，只有指针落在卡片上才临时接管。需在测试矩阵里专门验证「拖窗口到顶部 → 贴靠栏正常出现 + 线不误展开」。
6. 任务栏自动隐藏时热条仍可用；锁屏 / UAC 安全桌面时停止追踪。

**验收**：单屏下顶边中段能唤出线；点开始菜单/托盘立即收起；任务栏置顶时托盘图标区不误触；贴靠布局可用；100% / 125% / 150% / 200% 单一系统缩放下热区高度正确。

---

## 4. 🟠 飞入起点：捕获区域元数据

**macOS**：读文件扩展属性 `com.apple.metadata:kMDItemScreenCaptureGlobalRect`（4 个 NSNumber，全局点，左上原点），换算成 AppKit 屏幕坐标，作为飞入动画的 `from`；无该属性时直接落位。

**Windows 差异**：没有等价元数据。

**方案**：按来源分级推算 —— ① 自带选区热键 → 用真实选区矩形（写入 sidecar）；② 剪贴板图像 → 若尺寸等于某显示器整屏，用该显示器 `rcMonitor`；否则用光标所在显示器中央、按图片比例构造矩形；③ 目录监听到的文件 → 同 ②，但用文件创建时光标所在屏；④ 无法判断时不飞，直接落位（与原版 `from == nil` 分支一致）。同时把推算结果与置信度写日志（仅本地、可关闭），便于回归。

**验收**：全屏截图从整屏飞入；选区截图从选区飞入；复制的图片不出现离谱的飞行轨迹；「减少动态效果」时全部跳过。

---

## 5. 🔴 OLE 拖放与三种目标语义

**macOS**：`NSDraggingSource` 在应用外上下文返回 `[.copy, .move, .delete]`；应用拿 `.copy`（原图留绳上）、Finder 拿 `.move`（文件移动、原图下线）、Dock 回收站回报 `.delete` 由应用自己删文件；取消时 `animatesToStartingPositionsOnCancelOrFail = true` 飞回；`.move` 后 0.6 s 再复查一次。

**Windows 差异**：`DoDragDrop` 返回 `DROPEFFECT_COPY / MOVE / NONE`，**没有 `DROPEFFECT_DELETE`**；回收站是 Shell 命名空间扩展，拖进去通常表现为 `MOVE` 且由 Shell 自行删除；同盘拖到资源管理器默认是移动、跨盘默认是复制，与 macOS 的「应用 vs 文件夹」规则不是一回事；`IDataObject` 需要同时提供 `CF_HDROP` / `CFSTR_FILEDESCRIPTOR` / `CF_DIB` 才能被各类应用接受。

**方案**

1. `IDataObject` 提供 `CF_HDROP`（真实文件路径）、`CFSTR_FILEDESCRIPTOR{A,W}` + `CFSTR_FILECONTENTS`（供虚拟目标）、`CF_DIBV5` / 注册格式 `PNG`（供图像类应用）；`IDropSource` + `IDragSourceHelper` 生成半透明拖拽预览并把锚点对齐指针。
2. 允许 `DROPEFFECT_COPY | DROPEFFECT_MOVE`；**额外用落点判定目标类型**：`WindowFromPoint` → 若属于资源管理器/桌面且 `SHGetPathFromIDList` 得到目录 → 视为「文件夹」；若命中回收站（`SHGetKnownFolderPath(FOLDERID_RecycleBinFolder)` / Shell PIDL 比较）→ 视为「回收站」；否则视为「应用」。
3. 语义归一：**文件夹/桌面 → move（下线且文件保留在新位置）**；**应用 → copy（留绳上）**；**回收站 → 由 Shell 完成移动，应用随后 `prune()` 下线**。跨盘导致的 `COPY` 若落点是文件夹，仍按「用户意图是保存」处理：主动把文件复制/移动到目标目录并下线，保证结果与 macOS 一致。
4. `DoDragDrop` 返回 `DRAGDROP_S_CANCEL` / `DROPEFFECT_NONE` 时用 Composition 动画把卡片飞回原位；`MOVE` 后 **0.6 s** 再检查原路径是否存在，避免 Explorer 异步移动造成误判。
5. 拖拽期间 `isDragging = true`，线不收起、卡片 0.45 透明度。

**验收**：拖到微信/Word → 图片粘贴进去、卡片仍在；拖到文件夹 → 文件出现在文件夹、卡片消失；拖到回收站 → 文件进回收站、可撤销；拖到空白处松手 → 卡片飞回；连续拖 20 次无句柄泄漏。

---

## 6. 🟠 回收站与 20 步撤销

**macOS**：`FileManager.trashItem(at:resultingItemURL:)` 直接返回回收站里的新路径，撤销时 `moveItem` 从该路径搬回原目录；`clipboardFollows` 同步剪贴板引用。

**Windows 差异**：`IFileOperation`（`FOF_ALLOWUNDO`）不返回回收站内的路径；回收站里的实际文件名是 `$I…/$R…`，需要解析 `$I` 元数据才能还原；Shell 的 `InvokeVerb("restore")` 只能还原到**原位置**，不能重命名/指定位置，且不同 Windows 版本行为有差异。

**方案（双轨）**

- **应用内丢弃（角标 / 右键 / 全部取下）**：走 ClipLine 自有软删除索引。把文件原子移动到 `%LOCALAPPDATA%\ClipLine\.Trash\<uuid>\<原名>`，在 `state.json` 记录 `原路径 / 回收路径 / 绳上索引 / kept / byHand / 所属 step`。撤销时直接从索引精确搬回，100% 可靠，且不依赖 Shell 版本。若用户明确要求「真的进系统回收站」，再按原路径调用 `IFileOperation` 删除并保留一份索引副本用于尽力还原。
- **拖到系统回收站**：由 Shell 处理；`FolderWatcher` 观察到原文件消失后 `prune()` 下线；撤销时用 `Shell.Application.NameSpace(10)` 找到对应项调用 restore，失败则蜂鸣（与原版「回收站已清空则蜂鸣」一致）。
- 两种路径都执行 `clipboardFollows`：若剪贴板上仍是指向该文件的 `CF_HDROP`/文件 URL 且剪贴板序号未变，更新为新路径。

**验收**：连续丢弃 25 次后前 20 步可精确放回原索引；清空系统回收站后放回蜂鸣但不崩溃；丢弃→放回后粘贴到别处仍是有效文件。

---

## 7. 🟠 Quick Look / Markup / 视频修剪：三个系统替身

| macOS | Windows 方案 |
|---|---|
| `QLPreviewPanel` + ← → 浏览整条绳、Space/Esc 关闭 | 自绘 WinUI 预览窗（`Image` / `MediaPlayerElement`），`Window` 置顶但可关闭；← → 切到绳上相邻项目；Space/Esc 关闭；支持图片与录屏 |
| `com.apple.MarkupUI.Markup` 共享扩展 | 自研标注编辑器：Win2D 图层（矩形/椭圆/箭头/自由笔迹/文字/马赛克/裁剪）+ 撤销栈；保存时用 WIC 按**原格式**重新编码写回原文件，避免 PNG 化导致体积暴涨；缺 HEIC 编码器时给出明确提示（见 §12） |
| `AVPlayerView.beginTrimming` + `AVAssetExportSession(Passthrough)` | `MediaFoundation` / `MediaTranscoder` 按时间范围 **passthrough** 导出到同目录隐藏临时文件 `.trimming-<uuid>.<ext>`（监听器忽略隐藏文件），`ReplaceFileW` 原子替换原文件；窗口按视频比例、最大 720×520 |

**共性要求**：三者保存后都必须 `reloadThumbnail` / 刷新「拿在手上」窗口；编辑器打开期间线保持可访问但不抢焦点；失败时蜂鸣并保留原文件。

**验收**：标注后文件大小与原格式不变、绳上缩略图立刻更新；剪辑后视频可播放、原文件被原子替换、无残留临时文件；预览窗 ← → 顺序与绳上顺序一致。

---

## 8. 🟠 全屏与虚拟桌面

**macOS**：`FullScreen.swift` 用私有但稳定的 `CGSCopyManagedDisplaySpaces` 判断当前 Space 的 `type == 4`；`showLineOver` 默认关；Space 切换 / 应用激活后立即重查、0.8 s 后再查；`keepOnActiveSpace()` 在窗口被绑定到单个 Space 时**重建窗口**（原版 issue #80 的 workaround）。

**Windows 差异**：没有 Spaces 类型；虚拟桌面 API 只有 `IVirtualDesktopManager`（`GetWindowDesktopId` / `IsWindowOnCurrentVirtualDesktop` / `MoveWindowToDesktop`），没有公开的「切换事件」；全屏窗口的检测需要自己拼。

**方案**

1. **全屏判定**（任一成立即认为全屏）：前台窗口 `GetWindowRect` 覆盖整个 `rcMonitor` 且无标题栏/为 `WS_POPUP`；或 `SHQueryUserNotificationState` 返回 `QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_PRESENTATION_MODE` / `QUNS_BUSY`（ presentations）；或游戏/播放器声明了全屏独占。
2. **默认隐藏**（对齐 `showOverFullScreen = false`），菜单「在全屏应用上」可打开；`openedOnPurpose`（快捷键/菜单主动展开）可临时覆盖，收起后失效。
3. **虚拟桌面**：线窗口尝试通过 `IVirtualDesktopManager::MoveWindowToDesktop` 跟随当前桌面；监听 `WTSSESSION_CHANGE` / 定时 0.8 s 复查 `IsWindowOnCurrentVirtualDesktop`，不可见时**重建窗口**（直接照搬 macOS 的 `keepOnActiveSpace()` 思路，也是原版自己的解法）。不依赖未公开的 `IVirtualDesktopNotificationService`。
4. 切换虚拟桌面 / 激活应用后 0.8 s 二次复查，避开切换动画。

**验收**：全屏播放视频/演示时线不出现；手动快捷键能强制唤出；切虚拟桌面后线仍可用。

---

## 9. 🟠 力感应点按（唯一无法物理对齐的一项）

**macOS**：触控板 `pressureChange` 的 `stage == 2` 触发「快速查看」，且**优先于单击与长按**（`didLongPress = true` 阻止后续复制）。

**Windows 差异**：没有系统级 Force Touch；精确式触控板的压感不通过标准消息暴露；只有触控笔 `WM_POINTER` 的 `POINTER_PEN_INFO.pressure` 可用。

**方案（做成设置项，不硬编码）**

- 默认映射：**鼠标中键** → 快速查看（最容易被接受的等价手势）。
- 触控笔：压力 > 阈值且持续一小段时间 → 快速查看。
- 可配置项：`长按 + Shift`、`双击中键`、`禁用`；在设置里明确写「Windows 无压感，用力点按已映射为 …」。
- 保证优先级一致：触发快速查看后抑制本次单击复制与长按标记。

**验收**：中键只弹预览、不改剪贴板、不打开标记；长按与双击仍互不干扰。**这是唯一必须向用户明示「交互结果对齐、物理输入不同」的项。**

---

## 10. 🟠 剪贴板：从轮询到消息驱动，且私密过滤要自己补

**macOS**：0.5 s 轮询 `changeCount`；忽略 `.string` / `.fileURL` / `org.nspasteboard.{Concealed,Transient,AutoGenerated}Type`；只收 png/tiff；≤ 64 MB / ≤ 8000 万像素；解码后二次核对 changeCount。

**Windows 差异**：`AddClipboardFormatListener` 可以做到零轮询，但 `WM_CLIPBOARDUPDATE` 在部分应用使用延迟渲染（`WM_RENDERFORMAT`）或 `OleFlushClipboard` 时可能多次触发；**没有跨平台统一的「私密/临时」标记规范**（nspasteboard.org 那套是社区约定，Windows 侧无对应标准）；密码管理器常见做法是放一个自定义格式或清空剪贴板。

**方案**

1. `AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE`；`GetClipboardSequenceNumber` 去重；`OleGetClipboard` 取 `IDataObject`；延迟渲染时用 `GetClipboardData` + 超时保护，绝不阻塞 UI 线程。
2. **白名单**：只接受 `CF_DIB / CF_DIBV5 / CF_BITMAP` 或注册格式 `PNG`；**黑名单**：出现 `CF_UNICODETEXT` / `CF_TEXT` / `CF_HDROP` / `CFSTR_FILENAME*` 即整体拒绝（对应「同时带文字或文件」）。
3. **私密标记**：维护可配置的格式名黑名单（含 `CanIncludeInClipboardHistory` / `ExcludeFromClipboardHistory` 及常见密码管理器私有格式），并提供「忽略来自以下进程的剪贴板内容」用户列表；默认把常见密码管理器进程列入。命中即不读、不落盘、不留缓冲。
4. 保留 64 MB / 8000 万像素上限、后台解码、解码完成后二次核对序号与开关状态；PNG 直存，DIB→PNG 编码；0600 等价用 Windows ACL（仅当前用户 + SYSTEM）。
5. 因为无法像 macOS 那样 100% 识别私密内容，**默认仍然关闭**，并在开启时给出一句明确提示。

**验收**：复制纯图片挂上；复制文字/文件/图文混排不挂；密码管理器复制后不落盘（可检查目录与内存）；连续复制同一图片只挂一次；关闭开关后无任何读取。

---

## 11. 🟡 全局热键与「临时借用」键

**macOS**：Carbon `RegisterEventHotKey`（无需辅助功能权限）；默认 `⌃⌥T`；录制器要求必须含 Cmd/Ctrl/Alt；冲突时蜂鸣回滚；`⌘Z` / `Space` / `Delete` 只在指针位于线内时**临时注册**热键，离开立即注销。

**Windows**：`RegisterHotKey` 同样是系统级且无需 UAC，语义几乎 1:1；差别是 `Win+*` 组合被系统保留、`Ctrl+Z` 在前台应用中极常用。

**方案**

1. `RegisterHotKey(hwnd, id, MOD_CONTROL|MOD_ALT, 'T')` → `WM_HOTKEY`；录制窗口用 `WM_KEYDOWN` 捕获，Esc 取消、Delete 清空为「无」；注册失败（`ERROR_HOTKEY_ALREADY_REGISTERED`）→ `MessageBeep` + 回滚旧值。禁止登记 `MOD_WIN` 组合。
2. `⌘Z → Ctrl+Z`、`Space → Space`、`⌫ → Delete`；菜单显示 Windows 符号（`Ctrl+Alt+T`）。
3. **借用键用动态注册**：指针进入线区域且有可撤销步骤 → 注册 `Ctrl+Z`；悬停某张卡片 → 注册 `Space` / `Delete`；离开或卡片状态变化立即 `UnregisterHotKey`。与 macOS 的 `holdPutBackKey` / `holdPhotoKeys` 完全同构，不安装全局键盘钩子，降低杀软误报与隐私顾虑。
4. 被其他应用占用时记录 `refused` 标记，指针下次进入再试，不每帧重试。

**验收**：光标在线上时 Ctrl+Z 放回；光标离开后 Ctrl+Z 正常作用于当前应用；冲突时不崩溃、蜂鸣提示。

---

## 12. 🟡 托盘图标与 16 项菜单

**macOS**：`NSStatusItem` 用 `tshirt` / `tshirt.fill`，空绳 `appearsDisabled`；菜单每次打开重建、`autoenablesItems = false`。

**Windows 差异**：`NOTIFYICONDATA.szTip` 有长度限制（经典 128 字符含结尾，气球提示更长）；深浅色与 DPI 需要多套图标；右键菜单的复选/子菜单/禁用态要自己做；Win11 有「溢出区」，图标可能被折叠。

**方案**

1. `Shell_NotifyIconW(NIM_ADD)`，消息用 `WM_APP+1`，`NIF_ICON|NIF_TIP|NIF_MESSAGE`；准备 16/20/24/32/48/64 px 多分辨率 `.ico`，浅色/深色两套，随 `WM_SETTINGCHANGE("ImmersiveColorSet")` 与 `WM_DPICHANGED` 切换；空绳用变暗版；开启剪贴板监听用填充版。
2. 提示文字压缩到 127 字符内（两行短句），更长的说明放到右键菜单首项或气球提示。
3. 菜单用 WinUI `MenuFlyout`（Fluent 观感、复选、子菜单、禁用态齐全）放在一个透明 XAML 岛上；若 XAML 岛与自管 HWND 的层级冲突，退回 owner-draw `TrackPopupMenuEx`。**每次打开重建**，逐项设置 `IsEnabled` / `IsChecked`。
4. 处理图标被收进溢出区：首次运行给出一次气泡提示，引导用户把 ClipLine 固定到任务栏通知区。

**验收**：`01` 文档菜单表 16 项在深浅色、单屏 100% / 125% / 150% / 200% 缩放下全部可读、状态正确。

---

## 13. 🟡 文件完整性判定（v1.8.1 修复点）

**macOS**：按格式读文件尾判完整；未完成每 0.5 s 复查、600 s 放弃。

**Windows 差异**：其他应用在写入期间常持有独占句柄，直接读尾会抛 `IOException`；`ReadDirectoryChangesW` 会在写入过程中连续报 `MODIFIED`；隐藏临时文件命名规则与 macOS 不同。

**方案**：① 先尝试 `FileStream(share: FileShare.None)` 独占打开，失败即视为仍在写入；② 成功后按格式做同样的字节校验（PNG IEND / JPEG `FF D9` / GIF `3B` / WebP RIFF 长度 / HEIC box 链）；③ 其他格式用「0.5 s 内大小不变」；④ 防抖 0.2 s，未完成项每 0.5 s 重查，600 s 超时丢弃；⑤ 忽略以 `.`、`~`、`$` 开头的临时文件和 `.trimming-*`；⑥ 系统截图整体落盘的场景一次通过，**不加任何额外 sleep**。HEIC 依赖「HEIF 图像扩展」，缺失时明确提示而不是静默跳过。

**验收**：模拟「先建文件、分 10 次写入、间隔 1 秒」→ 只在最后一次写完后挂一张完整卡片；下载中断的文件 10 分钟后不再占用内存。

---

## 14. 🟢 开机自启、右键扩展、更新检查、签名

| 项 | macOS | Windows 方案 |
|---|---|---|
| 开机自启 | `SMAppService.mainApp` | MSIX 声明 `windows.startupTask` 并 `StartupTask.RequestEnableAsync()`（用户可在「设置 → 应用 → 启动」看到并关闭）；便携版写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；失败给明确提示 |
| 右键「挂到 ClipLine」 | `NSApp.servicesProvider` + `NSUpdateDynamicServices` | 稀疏 MSIX 包中的 `IExplorerCommand` 扩展（`DesktopWindowXamlSource` 不需要），支持多选图片；未安装扩展时提供「拖到托盘图标」兜底 |
| 检查更新 | 打开 `https://tendedero.app/?update=<版本>&lang=<语言>` | **只**在用户点击时打开 ClipLine 自己的 URL，参数一致；应用本身不发任何请求；不引入自动更新器 |
| 代码签名 | Developer ID + 公证 | Authenticode（OV/EV）签名 + MSIX 包签名，避免 SmartScreen 拦截；CI 里锁定签名证书指纹 |

---

## 15. 🟡 主题、动效偏好与无障碍

- **深浅色**：`UISettings.GetColorValue(UIColorType.Background)` + `WM_SETTINGCHANGE("ImmersiveColorSet")`；绳的三层描边与玻璃框的高光/阴影需按主题重算（原版绳是中性灰，深浅背景都要可读）。
- **高对比度**：`SystemParametersInfo(SPI_GETHIGHCONTRAST)`；高对比度下关闭材质模糊，改用系统色实边，保证夹子/角标可见。
- **减少动态效果**：`UISettings.AnimationsEnabled` / `SPI_GETCLIENTAREAANIMATION` → 关闭飞入、下落、微风（保留功能性展开/收起但改为淡入淡出）。
- **无障碍**：WinUI 控件自带 UIA；但**自绘的卡片、夹子、角标**必须手写 `IRawElementProviderSimple` / UIA Provider，暴露 `Image` / `Button` 角色、`Name`（如「已复制」「丢弃」）、`BoundingRectangle` 与 `Invoke` 模式，否则读屏器在绳上什么也读不到（macOS 侧同样做了 `setAccessibilityRole`）。
- **触控 / 笔**：长按用 `WM_POINTER` 的保持时间（0.45 s），压力用于「用力点按」替身；系统双击间隔用 `GetDoubleClickTime()`。

---

## 16. 🟡 本地化与文案

原版以英文原文作 key（`L("Hide line")`），每种 `.lproj` 一份，用 `check-strings.swift` 校验 key 齐全。Windows 侧：

- `.resw` 资源文件按语言目录（`Strings/zh-Hans/Resources.resw` 等），key 保持英文原句，`ResourceLoader` 取值；写一个构建期脚本比对 11 语言 key 集合，缺失即构建失败（等价 `check-strings.swift`）。
- `⌘ → Ctrl`、`⌥ → Alt`、`⌃ → Ctrl` 在**中文文案**里直接写 Windows 符号；「访达 → 文件资源管理器」「废纸篓 → 回收站」「预览 → 默认应用」「标记 → 标注」「拿在手上」保留（这是产品隐喻，不要改成「固定窗口」）。
- 日期文件名 `yyyy-MM-dd at HH.mm.ss` 中的 `at` 保持英文以避免文件名本地化带来的排序歧义。

---

## 17. 🟢 声音、录屏来源、锁屏与隐私边界

- **声音**：系统没有 Tink / Pop / 「拖到废纸篓」；打包三个极短 WAV 资源（< 20 KB），用 `PlaySound(SND_ASYNC|SND_MEMORY)`；错误蜂鸣映射 `MessageBeep(MB_ICONHAND)`。保持音量 0.35 / 0.25 与「系统截图不补提示音」的规则。
- **录屏**：默认把 `%USERPROFILE%\Videos\Captures` 加入监视（Game Bar `.mp4`）；也接受 `.mov / .mp4`；只有自有目录与默认录屏目录接受视频，普通文件夹只收图片（对齐原版 `acceptsRecordings`）。
- **锁屏 / 切换用户 / UAC 安全桌面**：`WTSSESSION_CHANGE` 与 `WM_WTSSESSION_CHANGE` 时收起线、停止指针追踪与剪贴板读取，避免在不安全桌面留下内容。
- **隐私边界**：所有路径都在用户目录内；日志默认关闭且只写本地文件；不读取剪贴板文字内容（只检查格式名）；不上传任何截图、文件名、路径或崩溃信息。

---

## 18. 建议的回归测试矩阵

| 维度 | 取值 |
|---|---|
| Windows 版本 | Win11 22H2 / 23H2 / 24H2；x64 + ARM64 |
| DPI | 100% / 125% / 150% / 200%（单屏单一系统缩放，不含主副屏不同缩放） |
| 任务栏 | 底部 / 顶部 / 自动隐藏 / 图标溢出区 |
| 主题 | 浅色 / 深色 / 高对比度 / 减少动态效果 |
| 全屏场景 | 全屏视频、PPT 演示、独占全屏游戏、虚拟桌面切换、贴靠布局 |
| 截图来源 | Win+Shift+S、PrtScn、截图工具自动保存、Game Bar 录屏、第三方工具、复制图片、右键挂上 |
| 拖放目标 | 微信/Word/浏览器、文件夹、桌面、跨盘、回收站、空白取消 |
| 私密场景 | 密码管理器复制、复制文件、复制图文混排、锁屏期间剪贴板变化 |

**放行门槛**：`01-feature-alignment.md` 全部复选框勾完，上述矩阵无 P0/P1 缺陷，且出站连接仅限方案 D 白名单（`api.github.com` / `github.com` / `objects.githubusercontent.com`）并由 `tools/check-privacy.sh` 静态门禁 + 抓包复核；其余场景零出站。v1 不含多显示器 / 每屏 DPI 组合（见 `01` 的 `N-11`）。
