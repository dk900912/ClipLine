# ClipLine Windows 11 交互与 UI 走查报告

> 走查对象：`ClipLine 1.0.1`（HEAD `cfeeb01`，真实交付包 `ClipLine_1.0.1.0_x64.msix` / `_x64_portable.zip`）
> 走查方式：源码逐条核对（`src/ClipLine.Shell`、`src/ClipLine.Platform`、`src/ClipLine.Application`）+ 真实证据帧（`dist/ui-evidence/`、`dist/*.png`）+ 对照 `docs/05-ui-design.md` 与 Windows 11 Fluent / Windows 桌面交互约定
> 判定口径：
> - **P0 阻碍体验**：核心任务做不完、会丢数据、或界面在"骗"用户（点了没反应 / 承诺了不存在的快捷键）。
> - **P1 效率损耗**：能做完但绕、慢、违反平台约定，或缺少桌面应用的基本预期（深色模式、Alt+Tab、键盘可达）。
> - **P2 视觉打磨**：不影响任务，但一眼看出"不是原生应用"的细节。

## 0. 总体判断

产品骨架（绳子隐喻、卡片、托盘 16 项菜单）是成立的，1.0.1 的功能链路也真实跑通了。但**"能跑"和"Windows 原生"之间还差一层**：四个工具窗是纯自绘 `WS_POPUP`，没有系统窗口行为；深浅色、DPI、键盘、无障碍这些桌面基线没有接进渲染层；更新流程把"全自动"做成了"全不可见"。

最致命的三件事，建议先于任何视觉打磨处理：

1. **工具窗不可拖动、不进 Alt+Tab、永远置顶** —— 用户打开"快速查看"想对照旁边的文档时，第一步就卡住。
2. **标注窗 Esc / 空格直接关闭且不确认** —— 一次误触丢掉全部标注。
3. **更新发现即下载、校验即安装、装完自动重启** —— 用户正在编辑时会被无预告打断。

---

## P0 · 阻碍体验（6 项）

### P0-1 工具窗没有系统窗口行为：不能拖、不能缩放、不进 Alt+Tab、永远置顶

- **证据**：`ToolWindow.cs:242` 用 `WS_POPUP` 创建，`WndProc`（356–462）没有 `WM_NCHITTEST` / `WM_NCLBUTTONDOWN` / `WM_GETMINMAXINFO`；`ToolWindow.cs:241` 带 `WS_EX_TOOLWINDOW`（从 Alt+Tab 与任务栏隐藏）；`ToolWindow.cs:248` 每次 `SetWindowPos(..., HwndTopmost, ...)`。
- **后果**：窗口固定在屏幕正中，挡住参考内容也挪不开；切到浏览器后无法用 Alt+Tab 找回，只能回绳子重新打开（而重新打开会清空标注，见 P0-2）；Topmost 让它压在全屏演示/视频之上。
- **建议**：
  - 改用 `WS_OVERLAPPEDWINDOW`（保留自绘客户区），或在 WinUI 3 里用 `Window` + `ExtendsContentIntoTitleBar`；至少补 `WM_NCHITTEST → HTCAPTION`、双击标题栏最大化、`WM_GETMINMAXINFO` 最小尺寸。
  - 去掉 `WS_EX_TOOLWINDOW`，让工具窗进任务栏与 Alt+Tab；「拿在手上」可保留置顶 + 工具窗样式（它本来就是浮窗语义）。
  - `QuickLook` / `Markup` 默认普通层，提供标题栏「置顶」图钉。
- **验收**：拖动标题栏移动、双击最大化、Alt+Tab 可切回、Win+Z Snap 可用、不被其他窗口永久遮挡。

### P0-2 关闭标注窗即丢失全部内容，无确认、无撤销

- **证据**：`ToolWindow.cs:437-440` 把 `Esc` 和 `Space` 都映射到 `Close(true)`；`ToolWindow.cs:312-316` 的 `Close` 直接 `Hide()`；`ToolWindow.cs:218` 每次 `Show` 都 `_shapes.Clear()`；全文件没有 dirty 标记、没有 `Ctrl+Z`。
- **后果**：标注过程中按空格（很多人习惯空格拖动画布）或 Esc，全部笔迹瞬间消失；再打开也无法恢复。
- **建议**：
  - 增加 `_dirty`，关闭前用 `TaskDialogIndirect`（或 WinUI `ContentDialog`）三选一：**保存 / 丢弃 / 取消**。
  - `Ctrl+S` 保存、`Ctrl+Z` / `Ctrl+Shift+Z` 撤销重做、`Delete` 删除选中标注。
  - 标注模式下 `Space` 改为「临时抓手/平移」，不要当关闭键；`Esc` 先退出当前工具，再按一次才关窗。
  - 崩溃/断电兜底：每 30 s 写一份临时草稿，下次打开提示恢复。
- **验收**：误按 Esc 不丢内容；关闭一定有确认；撤销至少 20 步。

### P0-3 自动更新全程不可见、不可取消，装完自动重启

- **证据**：`AppFacade.Update.cs:71` 发现新版本立即 `Emit(DownloadUpdate)`；`:105` 校验通过立即 `Emit(InstallUpdate)`；`:122` 安装成功直接提示"正在重启"；`UpdateSnapshot.Progress` **没有任何 UI 消费**（全仓仅掉落动画用 `Progress`）；通知卡是 `WS_EX_TRANSPARENT` 且 3.2 s 消失（`NoticeWindow.cs:127`、`:24`）。
- **后果**：用户正在标注/修剪时被强杀重启；不知道下载多大、多久、更新了什么；错过 3.2 s 通知就完全不知道发生过更新；无法推迟到方便的时候。
- **建议**：
  - 下载可后台自动，但**安装与重启必须显式确认**：`发现 v1.0.2（28.9 MB）` → `[立即重启更新] [稍后] [查看更新说明]`。
  - 新增更新面板（或可点击通知卡）：版本号、渠道（MSIX/便携）、体积、Release Notes、进度条、取消。
  - 检测到标注/修剪窗口有未保存内容时自动延后，并在下次空闲时再提示。
  - 失败要可操作：`重试` / `打开日志` / `复制错误`，不要只丢一句"下载失败"。
- **验收**：任意时刻能知道"在下载 / 已下载待安装 / 安装失败"；能取消；能推迟；重启前有确认。

### P0-4 「修剪」是假按钮，且把工程占位文案暴露给用户

- **证据**：`ToolWindow.cs:426-431` 点击「修剪」只把 `_status` 改成"v1 未接入视频编码：修剪范围已记录，导出请交给系统播放器"；`DefaultStatus(ToolMode.Trim)`（`:189`）同文案；没有任何写文件/编码动作。
- **后果**：用户点主按钮后界面毫无变化（只有一行灰字），会判定"应用坏了"；"v1 未接入"是工程语言，不该出现在成品里。
- **建议**：v1 没实现就**不要露出入口**——托盘/卡片菜单里去掉「修剪」，或按钮置灰 + tooltip「即将支持」。若必须保留预览，标题写「录屏预览」，主按钮改为「用系统播放器打开」，不要放一个点了没结果的「修剪」。
- **验收**：界面上不存在"点了没有任何结果"的控件。

### P0-5 拖放完全缺失，与规范的核心手势冲突

- **证据**：全仓无 `RegisterDragDrop` / `IDropTarget` / `WM_DROPFILES` / `DoDragDrop`（Grep 零命中）；但 `docs/05-ui-design.md` §8 把「拖进应用=副本留绳，拖进文件夹=移动并下线，拖到回收站=应用自己删」列为核心交互。
- **后果**：原版最高频的整理动作只能退化成右键菜单逐项操作；且验收文档与实现自相矛盾。
- **建议**：
  - 至少实现**出向拖**：卡片按下超过 4 DIP 后 `DoDragDrop`，`IDataObject` 提供 `CF_HDROP` + `CF_DIB`；拖到资源管理器=移动，拖到回收站=删除（`SHFileOperation` 走回收站）。
  - 入向拖可延后，但要在规范里标注 v1 不支持。
  - 若 v1 确定不做，**同步删除规范 §8 的拖拽条目**并改验收口径，不能留着当"已实现"。
- **验收**：能把卡片拖到桌面/文件夹/回收站，并有半透明拖拽预览与取消回弹。

### P0-6 键盘与屏幕阅读器完全不可用

- **证据**：`RopeWindow.cs` 无 `WM_KEYDOWN` / `WM_CONTEXTMENU` / `WM_GETOBJECT`（Grep 零命中）；卡片是纯像素，无 `IRawElementProviderSimple`；`ThemeTokens.AccessibilityLabels`（`ThemeTokens.cs:45`）是死代码，从未接入；卡片菜单只能鼠标右键（`RealInputBridge.cs:449`）。
- **后果**：纯键盘用户无法选中卡片、无法打开右键菜单、无法复制/取下/固定；Narrator 读不到任何卡片；不满足 Windows 11 无障碍基线。
- **建议**：
  - `WM_GETOBJECT` 返回 `UiaHostProviderFromHwnd` + 自定义 `IRawElementProviderSimple`；卡片暴露 `Button` 角色、`Name`=文件名、"已复制/已固定"状态。
  - 绳窗口增加焦点模型：`Tab`/`Shift+Tab` 在卡片间移动，画 2 DIP 品牌色焦点框（`FocusVisualKind=Reveal` 等价物）；`Enter` 复制、`Delete` 取下、`Shift+F10` 或菜单键打开卡片菜单。
  - 卡片菜单与托盘菜单补 `&` AccessKey；打开菜单时把焦点交给菜单。
- **验收**：拔掉鼠标能完成"选中卡片 → 复制 → 取下 → 打开所在文件夹"；Narrator 能报出卡片名与角标。

---

## P1 · 效率损耗（14 项）

| # | 问题 | 证据 | 落地建议 |
|---|---|---|---|
| P1-1 | **深色模式与高对比度视觉完全没生效**：卡片/工具窗/通知卡全是硬编码白底 | `RopeCanvas.cs:344/663/761/782/979` 均 `0xFFFFFF`；`ThemeTokens` 只在 `AppFacade.P5.cs:77` 进快照；全仓无 `WM_SETTINGCHANGE` | 所有绘制改读 `ThemePalette`；监听 `UISettings.ColorValuesChanged` 或 `WM_SETTINGCHANGE("ImmersiveColorSet")` 重建；高对比度走 `SystemColors` 且关模糊 |
| P1-2 | **系统主题切换不响应**，必须重启 | 无 `WM_SETTINGCHANGE` 处理 | 同上；另加 `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` 让标题栏跟随 |
| P1-3 | **托盘双击 = 三次 toggle**，视觉上闪一下回到原状 | `ShellTray.cs:120` 把 `WM_LBUTTONUP` 与 `WM_LBUTTONDBLCLK` 都接到 `LeftClicked` | 只处理 `WM_LBUTTONUP`，用 `GetDoubleClickTime()` 去抖；双击定义为「唤出并聚焦绳子」 |
| P1-4 | **菜单承诺的快捷键不存在**：`Ctrl+Q` 未注册；`Ctrl+Z` 仅指针在线内有效（且 `RopeWindow` 根本没键盘处理） | `TrayMenu.cs:118` 写 `Ctrl+Q`；全仓无 `VkQ`/`RegisterHotKey` for Q；`RopeWindow.cs` 无 `WM_KEYDOWN` | 注册 `Ctrl+Q` 或删掉提示；`Ctrl+Z` 改成全局可用（绳子展开期间），否则不要显示 |
| P1-5 | **「更改快捷键…」没有录制器**，点了没有 UI | `TrayMenu.cs:112` 只是普通命令；无按键捕获窗口 | 做录制弹层：按下组合键→`RegisterHotKey` 试注册→冲突提示→回滚；提供「恢复默认」 |
| P1-6 | **快捷键注册失败静默**：`Ctrl+Alt+T` 与 Terminal/截图工具冲突概率高 | `RealInputBridge.cs:67-69` 失败只写诊断；`hotkey=False` 用户不可见 | 失败时托盘图标警示态 + 通知「快捷键被占用，点此更换」；备用 `Ctrl+Shift+T` |
| P1-7 | **「检查更新…」点击后 0 反馈**，重复点击被状态机静默吞掉 | `AppFacade.Update.cs:22-26` 非终态直接 return，没有 `Emit`；只在完成时弹通知 | 立即把菜单项切成「正在检查…」禁用态 + 托盘 tooltip；或弹可取消的小面板 |
| P1-8 | **右键菜单是 Win32 经典菜单**：无图标列、无快捷键列、无 Win11 质感；贴边会溢出 | `RealInputBridge.cs:311/690` `TrackPopupMenuEx`；只有 ToggleLine 拼了 `\t`；无 `TPM_WORKAREA` | 改 WinUI `MenuFlyout` 或 owner-draw；补 16 px 图标、快捷键右对齐、`TPM_WORKAREA`；卡片菜单加「复制路径」 |
| P1-9 | **快速查看缩略图最多 8 张**，第 9 张起没有高亮；无缩放/适应窗口；大图同步解码无 loading | `RopeCanvas.cs:736` `Math.Min(f.Paths.Count, 8)`；`DrawToolImage` 同步 `ThumbnailCache.Get` | 缩略图条横向滚动 + 自动滚到当前项；`Ctrl+0/±`、`Ctrl+滚轮`；解码放后台 + 骨架占位 |
| P1-10 | **标注能力缺口**：只有 4 种笔型（规范 7 种），无撤销/重做、颜色/粗细、删除选中、文字/马赛克/裁剪 | `RopeCanvas.cs:755` `{"▭","○","↗","✎"}`；`ToolWindow.cs` 无 undo 栈 | 先补 `Ctrl+Z`/`Delete`，再补文字与马赛克；工具面板加颜色/粗细 |
| P1-11 | **保存标注直接覆盖同名文件**，无「另存为」、无冲突确认 | `ToolWindow.cs:323-325` 固定 `-已标注.png`；`DumpCanvas` 直接写 | 同名时 `IFileSaveDialog` 或「覆盖/另存」确认；保存后提供「在资源管理器中显示」 |
| P1-12 | **「添加文件夹…」用 Vista 前的老式目录树对话框** | `RealInputBridge.cs:749+` `SHBrowseForFolderW` | 改 `IFileOpenDialog` + `FOS_PICKFOLDERS`（可搜索、有常用位置、跟随深色） |
| P1-13 | **通知卡不可点击/不可关闭/3.2 s 消失/无视勿扰/无排队** | `NoticeWindow.cs:127` `WS_EX_TRANSPARENT`；`:24` `HoldUntilMs=3200`；`Show()` 直接覆盖上一条 | 去 `TRANSPARENT`，加关闭按钮与点击动作；查 `SHQueryUserNotificationState` 尊重勿扰；多条排队 |
| P1-14 | **高 DPI 只在启动读一次**，运行中改缩放会错位 | `ToolWindow._scale` 构造时固定（`:152`）；全仓无 `WM_DPICHANGED` | 处理 `WM_DPICHANGED`：重算尺寸、重建 canvas、重排；验收 100/125/150/200% 热切换 |

补充两条同类问题（合并计 16 项，不单独占表）：

- **首次运行没有真正引导**：只有 3.2 s 通知卡，没解释"顶部中间 1/3 悬停 0.25 s 才唤出"；空态文案「截个图，它就会挂在这里」也没说怎么截。建议首次用 3 步教学卡（截图 → 自动挂上 → 悬停顶部唤出）+「不再显示」。
- **卡片没有 hover/pressed**：`DrawCard`（`RopeCanvas.cs:332-404`）无 hover 分支，`_inCard` 只是离屏缓冲标志。用户不知道卡片可点、角标可点。补规范 §8 的 hover 1.035 / 按下 0.95 / 角标淡入 + 400 ms tooltip。

---

## P2 · 视觉打磨（12 项）

| # | 问题 | 证据 | 落地建议 |
|---|---|---|---|
| P2-1 | **阴影是 3 层硬边圆角矩形**，不是高斯模糊，边界有色阶 | `RopeCanvas.cs:660-662` | 改用 DWM 系统阴影（`DwmExtendFrameIntoClientArea`）或预烘焙 9-patch 高斯阴影 |
| P2-2 | **按钮尺寸/圆角不合 Win11**：高 30 DIP、圆角 6、文字偏移 30 px 不居中、无 hover/pressed/disabled | `RopeCanvas.cs:776-787` | 统一组件：高 32、圆角 4、左右 12 内边距、按实测文字宽居中；补三态 |
| P2-3 | **工具栏图标用 Unicode 字形** `▭○↗✎`，fallback 不可控、重量不一、无 tooltip | `RopeCanvas.cs:755` | 换 Segoe Fluent Icons glyph 或 20 px 线性图标；40×40 热区 + tooltip + 快捷键字母 |
| P2-4 | **文字用 GDI `ANTIALIASED_QUALITY`**（非 ClearType），小字号发虚；字宽按 `fontH*0.58` 估算，导致截断/居中偏差 | `RopeCanvas.cs:1113+`、`:1042-1048` | `TextRenderingHint.ClearTypeGridFit`（透明层用 `AntiAliasGridFit`）；用 `TextRenderer.MeasureText`/`IDWriteTextLayout` 真实测量；字体栈 `Segoe UI Variable Text` + `Microsoft YaHei UI` |
| P2-5 | **通知卡排版失衡**：正文两行行距偏紧、下边距 6 px 贴底、图标与标题基线不齐 | `RopeCanvas.cs:1000-1008` + `dist/ClipLine_1.0.0.0_notice.png` | 12/16 字号、行高 18、上下 14/16 对称；图标 24×24 与标题基线对齐 |
| P2-6 | **窗口圆角 10 但无 DWM 边框/阴影**，和系统窗口并排像"贴纸"；标题栏渐变竖条在圆角处可能溢出 | `RopeCanvas.cs:657/675-681` | `DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND` + 自绘内容裁剪到圆角路径 |
| P2-7 | **关闭按钮 26×26、灰底**，非 Win11 46×32 标题栏按钮，无 hover 红底 | `RopeCanvas.cs:690-696` | 46×32 热区；hover `#C42B1C` 白叉；按下降 10% 亮度 |
| P2-8 | **托盘/卡片菜单无法匹配规范 §4** 的 246–262 宽 / 30 行高 / 圆角 9 / Acrylic 30 | Win32 菜单由系统绘制 | 要么改 owner-draw / MenuFlyout，要么更新 `docs/05` §4 承认系统菜单外观 |
| P2-9 | **夹子是无圆角矩形像素**，无阴影、无顶部卡槽，放大看像色块 | `RopeCanvas.cs:385-400` | 圆角 3.5、金属渐变、5×1.4 卡槽、`黑 .30 / 2 / y1.5` 阴影 |
| P2-10 | **缩略图 52×40 固定、选中环 2 px 硬边**，不按图片比例 | `RopeCanvas.cs:735-747` | 56×42、圆角 6、选中环品牌色 2 px + 8% 外发光 |
| P2-11 | **卡片图片 contain 留白用饱和蓝渐变**，非 3:2 图片会出现明显蓝边 | `RopeCanvas.cs:346-357` | 留白改中性 `#F3F4F8`；或 `UniformToFill` + 圆角裁剪，更像相框 |
| P2-12 | **全应用无 Mica/Acrylic**，纯色 GDI，和 Win11 桌面质感有差距 | `RopeCanvas.cs` 全量 `FillRoundRectRgb(..., 0xFFFFFF, ...)` | 工具窗迁 WinUI 3 + `MicaBackdrop`，卡片用 `AcrylicBrush` TintOpacity 0.7；绳子保留 GDI |

---

## 3. 建议的修复批次

**第一批（P0，1–2 周，先让窗口像 Windows 窗口）**

1. 工具窗补系统 chrome：可拖动、可缩放、进 Alt+Tab、取消默认 Topmost（P0-1）。
2. 标注窗 dirty 保护 + 关闭确认 + `Ctrl+S` / `Ctrl+Z`（P0-2）。
3. 更新流程改成"下载自动、安装确认、可推迟"（P0-3）。
4. 移除/禁用「修剪」假按钮与"v1 未接入"文案（P0-4）。

**第二批（P0 剩余 + P1 高频，2–4 周）**

5. 键盘可达 + UIA（P0-6）；拖放做或从规范删（P0-5）。
6. 深色模式/高对比度真正接进渲染层 + 主题热切换（P1-1/2）。
7. 快捷键诚实化：`Ctrl+Q`、录制器、冲突提示（P1-4/5/6）。
8. 更新检查即时反馈 + 进度面板（P1-7）。
9. 托盘双击 bug、菜单补图标/`TPM_WORKAREA`（P1-3/8）。

**第三批（P2，穿插做）**

10. 统一按钮/文字/阴影/图标四件套，先把"手绘补丁感"压下去（P2-1/2/3/4/7）。
11. 通知卡排版与交互（P1-13 + P2-5）。
12. 若确定迁 WinUI 3，Mica + Acrylic 一次到位（P2-12），否则接受 GDI 并更新规范。

## 4. 判定说明

- **属于 v1 已知裁剪、但按桌面标准仍是缺陷**：XAML 窗口/Mica（P2-12）、视频编码（P0-4）。这些需要产品决策：**补齐，或从规范与界面里移除**，不能留在"看起来有、点了没有"的状态。（原列在此处的拖放 P0-5 与完整标注工具集 P1-10 已在 v1.7 补齐，见 §5。）
- **属于实现 bug、必须修**：P0-2、P1-3、P1-4、P1-6、P1-13。
- **属于规范与实现不一致、需二选一**：P2-8（菜单尺寸）、P1-1（深色模式在规范里写了但没做）。

---

## 5. 落地状态（v1.7，2026-11-06）

三个批次已按上表顺序全部实施，P0-5 与 P1-10 在 v1.7 按用户要求从「不做」翻案补齐，逐项状态如下。**「已修」= 代码里能跑、且被 `verify.sh` / `smoke.ps1` / `ui-click-verify.ps1` 的断言或证据帧覆盖**；「部分」= 主体可用、明确留了尾巴；「决策」= 按 §4 的口径二选一后的结论，规范与界面已同步。

| # | 状态 | 落地内容 / 结论 |
|---|---|---|
| P0-1 | 已修 | `ToolWindow` 补系统窗口行为：44 DIP 标题栏拖动、六向缩放、`WS_EX_APPWINDOW` 进任务栏与 Alt+Tab、取消默认置顶（改为可图钉）、双击标题栏最大化、再次打开沿用上次位置。 |
| P0-2 | 已修 | 脏标记 + 关闭确认三选一（保存 / 丢弃 / 取消）；`Ctrl+S` / `Ctrl+Z` / `Ctrl+Y` / `Delete`；撤销栈上限 30；草稿落盘 `%TEMP%\ClipLine\markup-draft.json`，异常退出后可恢复。 |
| P0-3 | 已修 | 下载完成只进入「待确认」，必须点「立即重启更新」才安装；可「稍后」；下载中可取消（`CancelUpdateDownload` + `CancellationTokenSource`）。单测 U-09 / U-09b / U-09c 覆盖。 |
| P0-4 | 已修 | 修剪窗改名「录屏预览」，主按钮「用系统播放器打开」（`ShellExecuteW open`），界面上不再有点了没结果的控件。 |
| P0-5 | 已修 | **v1.7 补齐**：按住卡片移过 `SM_CXDRAG`/`SM_CYDRAG` 阈值即升级为真 OLE 拖放（`DragDrop.cs`：自实现全 blittable `IDataObject`/`IDropSource` + `Marshal.GetComInterfaceForObject`，只暴露 `CF_HDROP`，载荷 UTF-16 双零结尾）。拖拽中源卡片 45% 透明、指针处跟 `IDragSourceHelper` 半透明预览。**去留不认目标回报的 `DROPEFFECT`，回到磁盘看原路径是否还在**：不在 → 下线并播掉落动画（复用 `FileDisappeared`），还在 → 按复制/取消处理。入向拖放仍不做。 |
| P0-6 | 部分 | 已做：Tab / Shift+Tab / 方向键焦点、2 DIP 品牌色焦点框、`Enter` 复制、`Space` 预览、`Delete` 取下、`Ctrl+Z` 放回、`Shift+F10` / 菜单键卡片菜单（贴着焦点卡片弹）、菜单 `(&X)` 访问键、窗口级无障碍名称 + `EVENT_OBJECT_NAMECHANGE`。未做：卡片级 `IRawElementProviderSimple`（见 §5.1）。 |
| P1-1 | 已修 | `ThemePalette` 三套色板（浅 / 深 / 高对比度）接入卡片、四个工具窗、通知卡、更新面板、快捷键录制器；不再有硬编码 `0xFFFFFF`。 |
| P1-2 | 已修 | 托盘宿主收 `WM_SETTINGCHANGE` / `WM_THEMECHANGED` → `ApplyTheme()` 全窗口即时换色；`uxtheme` 私有序号让系统菜单跟随深浅；`DWMWA_WINDOW_CORNER_PREFERENCE` + `DWMWA_USE_IMMERSIVE_DARK_MODE`。 |
| P1-3 | 已修 | 托盘只认第一次 `WM_LBUTTONUP`，双击窗口内第二次 UP 用 `GetDoubleClickTime()` 吞掉，不再「闪一下回原状」。 |
| P1-4 | 已修 | 真的注册 `Ctrl+Q`（热键 id 2 → 退出）；`Ctrl+Z` 在绳子展开且聚焦时生效。菜单里的提示从此为真。 |
| P1-5 | 已修 | 新增 `ShortcutWindow`：按什么显示什么；只接受 Ctrl/Alt/Shift/Win + 字母数字（与可注册范围一致）；`Delete` 清空、`Esc` 取消、「恢复默认」；结果按门面回写值反馈。 |
| P1-6 | 已修 | 热键所有权收敛到输入桥（`RegisterHotKey` / `UnregisterHotKey` / `RollbackShortcut` 改由壳层执行）；注册失败立即弹通知 + 回灌 `HotKeyConflict` 回滚 + 写诊断日志。 |
| P1-7 | 已修 | 手动检查立刻出更新面板，`Checking` 阶段显示「正在检查更新…」；自动检查仍静默。 |
| P1-8 | 部分 | `TPM_WORKAREA` 防贴边溢出、菜单项访问键、快捷键列、深浅色跟随。未做图标列与 owner-draw（见 §5.1）。 |
| P1-9 | 已修 | 缩略图 56×42、最多 8 个但随当前项横向滚动（第 9 张起不再消失）；`Ctrl+0/±`、`Ctrl+滚轮` 缩放平移；选中项 8% 柔光 + 2 DIP 实环。 |
| P1-10 | 已修 | **v1.7 补齐**：工具栏 6 个笔型（矩形 / 椭圆 / 箭头 / 笔 / 文字 / 马赛克），撤销 / 重做 / 删除上一条 / 保存 / 缩放平移沿用 P1-9。文字走**原生输入窗**（普通窗口 + `EDIT`，中文输入法候选窗、选区、粘贴、触摸全由系统提供）落成 `MarkShape.KindText`，可点已有文字改内容、`[` `]` 调字号；马赛克读主画布已绘制像素按 `max(6, 12 DIP)` 块求平均再填回，与「所见即所得」导出一致。草稿格式向后兼容（新增 `T`/`S` 字段，旧草稿缺字段按 null/0 读）。 |
| P1-11 | 已修 | 同名文件自动 `(2)` `(3)` 递增，不再静默覆盖原图；保存成功后清脏标记与草稿。 |
| P1-12 | 已修 | `IFileOpenDialog` + `FOS_PICKFOLDERS`（可搜索、有常用位置、跟随深色），创建失败才回退 `SHBrowseForFolderW`。 |
| P1-13 | 已修 | 通知卡去 `WS_EX_TRANSPARENT`：可点、可关闭、停留 6 s、尊重勿扰/演示/全屏游戏、多条排队（上限 4）。 |
| P1-14 | 部分 | 工具窗处理 `WM_DPICHANGED`（重算 scale、按系统建议矩形重排、重建画布）。更新面板与录制器是短生命周期窗口，未做热切换重排。 |
| 补充·首次引导 | 未做 | 3 步教学卡仍缺，欢迎通知只覆盖「怎么截」。 |
| 补充·卡片 hover | 已修 | 1.5 DIP 品牌色悬停描边，与命中判定同源（不会出现「描边在这里、点击在那里」）。 |
| P2-1 | 已修 | 3 层硬边阴影改 6–7 层柔阴影（`DrawSoftShadow`），高对比度下自动关闭投影。 |
| P2-2 | 已修 | 统一 `DrawButton`：高 32 DIP、圆角 4、按实测墨迹宽度居中、Primary/Secondary/Danger 三态 + hover/pressed。 |
| P2-3 | 已修 | 工具栏 4 个 Unicode 字形换成线性绘制图标（矩形/椭圆/箭头/笔），40×40 热区；图钉重画为「横头 + 针杆 + 收尖」。 |
| P2-4 | 部分 | 按内容选字面（ASCII → Segoe UI，含中文 → Microsoft YaHei UI）；定位改用 `InkWidth` 实测墨迹宽度，不再按 `fontH×0.58` 估算；仍是灰度抗锯齿（透明蒙版不能用 ClearType，已写进规范）。 |
| P2-5 | 已修 | 通知卡：图标 24×24、行高 `bodyFont+4`、右上角 24×24 关闭叉、上下留白对称。 |
| P2-6 | 已修 | `DWMWCP_ROUND` + 深色边框属性 + 自绘柔阴影；圆角内容裁剪由绘制层保证。 |
| P2-7 | 已修 | 关闭按钮 46×32 热区、hover 红底（`#C42B1C`）白叉、按下再压 10% 亮度。 |
| P2-8 | 决策 | 保留系统菜单渲染，规范 §4 已写入偏差说明；只补 `TPM_WORKAREA`、访问键、快捷键列、深浅色。 |
| P2-9 | 已修 | 夹子重画：圆角 3.5 DIP、金属竖向渐变、5×1.4 DIP 卡槽、下沿投影、左侧高光。 |
| P2-10 | 已修 | 缩略图 56×42、圆角 6、选中环 2 DIP + 8% 外发光。 |
| P2-11 | 已修 | 卡片 contain 留白从饱和蓝渐变改为中性相框色（浅 `#F3F4F8` / 深 `#2B2B2B`），竖图不再有蓝边。 |
| P2-12 | 决策 | 不迁 WinUI 3。分层窗口 + `UpdateLayeredWindow` 用不了系统 Mica/Acrylic（DWM 背景材质要求非分层窗口或 Composition），规范 §12 已承认「GDI 自绘、无 Mica」。 |

### 5.1 未落地项与理由

| 项 | 为什么暂不做 | 影响面 |
|---|---|---|
| P0-6 卡片级 UIA | `IRawElementProviderSimple` + `IRawElementProviderFragmentRoot` 需要在托管侧精确复刻 COM vtable 与片段导航，错误会以 Narrator 崩溃的形式出现，且本机无法用自动化脚本验证朗读结果。已先保证键盘可完成全部操作 + 窗口级名称可读。 | 朗读粒度只到窗口 |
| P1-8 菜单图标列 | 只能 owner-draw，代价是丢掉系统的高对比度、触摸、Alt 导航与输入法行为；收益（16 px 图标）小于代价。 | 观感 |
| P1-14 面板热切换 | 更新面板与录制器打开时长以秒计，重建收益低于复杂度；工具窗（长时间停留的窗口）已处理。 | 极端场景错位 |
| 首次引导 | 需要新的教学卡布局与「不再显示」持久化字段，属独立迭代。 | 新用户理解成本 |
| Mica / Acrylic | 见 P2-12。 | 观感 |

### 5.2 翻案记录：P0-5 与 P1-10（2026-11-06，用户要求）

上一轮这两项被写成「v1 不做」并附了理由，用户直接否掉了这个结论：「完成 1 和 3」。翻案后的事实核对：

- **原判「拖放无法自动化验收」是错的**。真正卡住的只有一件事——用托管「导入接口」（`ComTypes.IDataObject`）时生成的 CCW vtable 与 OLE 期望不一致，会在 `DoDragDrop` 内部把 CLR 打崩（`0x80131506`）。改成**自定义全 blittable 接口 + `Marshal.GetComInterfaceForObject`** 后就不再走那条路径；接口与外层类还必须是 `public`，否则抛「The specified type must be visible from COM」。验收侧也不必驱动真实拖放：`CF_HDROP` 载荷（UTF-16 + 双零结尾）与「去留按磁盘判定」是纯函数，冒烟里读回真载荷断言；真实手势只用 `mouse_event` 证明 `DoDragDrop` 拉起并能安全返回（Esc 取消后文件与卡片都不动）。
- **原判「文字标注要重做排版」也过头了**。文字不需要自绘输入框：普通窗口 + 原生 `EDIT` 就同时拿到输入法候选窗、选区、右键菜单、粘贴与触摸。真正需要自己写的只有「落字」这一步（GDI 生成灰度蒙版 → 3×3 描边 → 合成）。
- **留下的教训**：把「我没把握做对」写成「v1 不做」，等于用规范替实现背锅。以后遇到同类判断，先写清楚**具体卡在哪一行技术点**，再决定是否裁剪。

两个已修项的证据：`ui-click-verify.ps1` 29→33 项（新增文字/马赛克/真实拖拽/输入窗客户区布局），`frame-markup-text.png`、`frame-markup-mosaic.png`、`screen-markup-textinput.png`、`screen-drag-out.png`。
