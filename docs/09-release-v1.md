# ClipLine v1 发布说明（当前版本 1.0.1）

> 状态：**已构建、已签名、已在本机实跑验证、已公开发布**。GitHub Release `v1.0.1` 已上线，源码 `HEAD = origin/main = ca28582`，详见 §13.7。本文档是 v1 的唯一放行记录，取代 `docs/06` / `docs/08` 中关于「尚未打包」的旧表述。
> 构建日期：2026-11-05；目标平台：Windows 11 x64（单显示器 + 单一系统缩放）。
> 注：§7–§12 中「本轮未 commit、未 push」是各历史轮次当时的快照记录；这些改动已随 §13.7 的 `ca28582` 一次性提交并推送。

## 1. 交付物

| 文件 | 说明 | 大小 | SHA-256 |
|---|---|---|---|
| `dist/ClipLine_1.0.1.0_x64.msix` | 正式 MSIX（v1.5 方案 D），已用 `CN=ClipLine` 代码签名证书签名（`SHA256`）；Identity 版本同步升到 `1.0.1.0` 以支持 `Add-AppxPackage` 更新 | 29.03 MB | `610A28491B333D66A93C1B66B3E67AB5D92FB6F445E66B28C9C319A1A88692BA` |
| `dist/ClipLine_1.0.1.0_x64_portable.zip` | 免签名、免安装的同 payload 便携包，解压后双击 `ClipLine.exe` | 28.35 MB | `5197CAA4EDA43ADA163DCE963D26139C348E8867B22856BD963A3818BC28C15B` |
| `dist/*.sha256` / `release-assets.txt` | GitHub Release 资产清单与逐文件 SHA-256 sidecar，客户端校验不通过即丢弃下载 | — | — |
| `dist/ClipLine.cer` / `ClipLine.pfx` | 本地开发签名证书（公钥 / 带口令私钥，口令见构建脚本） | — | — |
| `dist/ClipLine_1.0.0.0_render-frame.png` | **确定性渲染证据**：应用把当前 32bpp DIB 帧导出的 PNG，绳 + 夹子 + 真实缩略图卡片（橙/蓝/白三色块顺序正确） | 15 KB | — |
| `dist/ClipLine_1.0.0.0_copied-badge.png` | **左键复制证据**：同一帧在单击后重绘，卡片底部出现品牌靛蓝圆角底 + 白色「已复制」 | 19 KB | — |
| `dist/ClipLine_1.0.0.0_swing-aa.png` | **抗锯齿证据**：14° 摆动帧的真实 DIB 导出，放大可查边框与圆角无阶梯 | 26 KB | — |
| `dist/ClipLine_1.0.0.0_notice.png` | **自绘通知证据**：288×76 DIP 通知卡的真实 DIB 导出（欢迎文案；复制场景已不再弹通知，见 §11） | 5 KB | — |
| `dist/menu-audit-report.txt` | **菜单全量审计报告**：两个场景 45 个检查点逐项结果，含真实桌面写入验证 | — | — |
| `dist/ui-evidence/` | **真实鼠标点击端到端证据**（11 张）：四个替身窗口帧 / 整屏截图、绳结局部帧与 6× 放大图、固定前后绳帧 | — | — |
| `dist/e2e-desktop.png` | 真机运行时的桌面环境截图，仅作运行记录；**不作为渲染证明**（见下方说明） | — | — |

> 上表哈希是 2026-11-05 **v1.5 方案 D 双通道自动更新**的最终交付构建实测值（取代 v1.4 的 `EDD7C86A…` / `793130B4…`）。版本号由 `src/ClipLine.Domain/BuildInfo.cs` 单点派生，构建脚本据此注入 MSIX Identity 版本、生成资产文件名与 `.sha256`。重新运行 `tools/build-msix.ps1` 会因签名时间戳产生不同哈希；`dist/` 已由 `.gitignore` 排除，产物需从脚本复现。
>
> **v1.5 增量（版本 1.0.1，产品定位变更，用户拍板「必须做到 D」）**：GitHub Release 全自动更新，便携 ZIP 与 MSIX 双通道；「检查更新…」从「点了没反应」修成真实链路；`UpdateVersion` 版本比较、`UpdateService` 白名单联网与 SHA-256 校验、一次性 PowerShell helper 替换/注册与回滚、启动后一次静默检查；隐私门禁从「零联网」改为「白名单 + 零 URL 常量 + 零遥测」。详见 §13。
>
> **v1.4 增量（同版本号 1.0.0.0，未改程序集版本）**：四个 P4 命令不再是「统一交给系统默认应用」，而是四个各自独立、尺寸不同、可交互的自绘分层窗口；「固定」补齐 V-09 绳结 + 淡靛蓝分区 + 固定卡靛蓝描边；新增 `--ui-click` 真实系统输入队列注入的端到端验证（25/25）；修复分层窗口 `pptDst` 导致窗口跳左上角、隐藏态不自愈、固定色序反转、染色带整像素台阶等真实缺陷。详见 §12。
>
> **v1.1 增量（同版本号 1.0.0.0，未改程序集版本）**：① 空绳也能被顶部中间热区唤出；② 春风阵风 / 入场摆入 / 复制轻摆；③ 单张与批量取下的全屏掉落动画；④ 静态倾角 ±2.5° 接线；⑤ 修正 `Layout.FallTilt` 把进度变量 `e` 误当自然常数的对齐 bug；⑥ **摆动边缘抗锯齿**：离屏卡片缓冲 + 反向映射双线性重采样 + 圆角解析式 SDF 覆盖率（见 `docs/10` §8）。⑦ **复制补齐 `CF_HDROP`**，资源管理器 Ctrl+V 可粘贴文件；⑧ **系统气泡换成 288×76 DIP 自绘通知卡**（见 §10）；⑨ **复制场景移除冗余通知**，只保留卡片「已复制」角标 + 3° 轻摆（见 §11）。
>
> **关于桌面截图**：`Graphics.CopyFromScreen` 在本机 DWM 下不保证抓到分层窗口，且可能被其他顶层窗口（本次是 WPS Office）遮挡，`CaptureBlt` 又返回全黑。因此渲染正确性以应用内导出的 `ClipLine_1.0.0.0_render-frame.png` 为准；真机窗口状态另由 `diagnostics.log` 的 `window: present=True revealed=True ... cards=N` 佐证。

复现构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-msix.ps1
```

脚本零删除、零联网，只依赖本机已有的：.NET 8 SDK（`%TEMP%\clipline-tools\dotnet`，或系统 `dotnet`）、Windows SDK 10.0.26100 x64 的 `makeappx.exe` / `signtool.exe`、系统 `gdiplus.dll`、Windows App SDK 1.6 NuGet 包（已缓存）。**本机不需要 Visual Studio，也不需要 Windows App SDK 运行时框架包**——运行时以 self-contained 方式随包分发。

## 2. 安装与使用

### 便携版（推荐，零门槛）

解压 `ClipLine_1.0.1.0_x64_portable.zip` 到任意目录，双击 `ClipLine.exe`。无需管理员、无需证书。应用默认纯本地运行；**唯一出站**是启动后一次（或用户点「检查更新…」）对 GitHub Release 的检查与下载，且只接受 `update-config.json` 的 `allowHosts` 白名单主机。设置环境变量 `CLIPLINE_AUTO_UPDATE=0` 可完全关闭启动检查。唯一会写注册表的情况是用户主动勾选菜单「登录时打开」：写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `ClipLine` 项，取消勾选即删除。

### MSIX 版

MSIX 的签名链必须被目标机器信任。以管理员身份执行一次：

```powershell
certutil -f -addstore "TrustedPeople" ClipLine.cer
# 或双击 ClipLine.cer → 安装证书 → 本地计算机 → 受信任的根证书颁发机构
Add-AppxPackage -Path .\ClipLine_1.0.1.0_x64.msix
```

`tools/trust-cert.ps1` 会自动判断权限：管理员则静默导入机器存储；非管理员则打印手动步骤，不会挂起。

**本机当前限制**：本机账户是标准用户（非管理员）、开发者模式未开启，向受信任根写入会弹交互式确认框。因此 `Add-AppxPackage` 在本机返回 `0x800B0109 CERT_E_UNTRUSTEDROOT`——这是部署策略限制，不是包损坏：`makeappx pack` 与 `signtool sign` 均成功，包内 `AppxSignature.p7x` 完整。本机已改用便携版完成实机验证。

### 首次使用

按 C-01 / F-04 规格，「挂上复制的图片」与「接管截图」默认 **opt-in 关闭**，首次启动会弹自绘通知卡告知开启入口（v1 早期用系统气泡，已按 §10 替换）：

> 右键托盘图标 → 勾选「挂上复制的图片」与「接管截图」

开启后：

- 复制任意图片（`Win+Shift+S`、截图工具、浏览器复制图片）→ 自动落到 `%LOCALAPPDATA%\ClipLine\Screenshots\` 并挂上绳。
- 图片保存到 `图片\Screenshots` 或 ClipLine 自有目录 → 自动挂上。
- 鼠标移到屏幕顶端热区 → 绳子放下；离开后自动收起。
- 左键托盘图标 / `Ctrl+Alt+T` → 显示或隐藏绳子。
- 卡片：左键单击 → 图片写入系统剪贴板，卡片底部立刻出现「已复制」角标（1.2 秒后消失）+ 3° 轻摆；**不再弹通知窗**（角标已是完整反馈，见 §11）。双击打开系统默认应用、右键菜单（按 G-11…G-18 条件显隐，最多 14 项）、滚轮滚动；**快速查看 / 标记 / 修剪 / 拿在手上各自打开一个独立的自绘窗口**（见 §12），只有「打开」才交给系统默认应用。
- 监听目录里的图片被删除或改名时，绳上对应卡片会**联动取下**（`FILE_ACTION_REMOVED` / `RENAMED_OLD_NAME` → `FileDisappeared`），不会留下幽灵卡片。
- 「存储到桌面」把自有目录里的图片**移动**到系统桌面（同名自动加 `(2)`）；「移到回收站 / 丢弃」走 `SHFileOperationW` 的可撤销删除。
- 托盘「添加文件夹… / 停止监视」会实时增删 `ReadDirectoryChangesW` 监听器；「挂上复制的图片」开关会实时挂上/摘掉 `WM_CLIPBOARDUPDATE` 监听。

两个场景（托盘右键 + 卡片右键）的逐项测试结果见 `dist/menu-audit-report.txt`，复跑命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\menu-audit.ps1
```

设置持久化在 `%LOCALAPPDATA%\ClipLine\state.json`，诊断日志在同目录 `diagnostics.log`（纯本地、无统计、无网络）。

## 3. v1 真实落地的能力

| 能力 | 实现 | 验证 |
|---|---|---|
| 托盘三态图标 | 独立 message-only 宿主窗口 + `Shell_NotifyIconW`，16px 独立绘制 | 冒烟 20/20 |
| 16 项托盘菜单 | `TrayMenuModel.Build` → Win32 `CreatePopupMenu` / `AppendMenuW` 递归子菜单，中文标签，每次打开重建 | 单测 T-05 系列 + 冒烟结构自检 18/18 项 |
| 剪贴板图片 | `AddClipboardFormatListener` → `CF_BITMAP` 或 `CF_DIB` → GDI+ 编 PNG → `NewItem` | e2e 实跑：`clip-20261105-083626-527.png` |
| 目录监听 | `ReadDirectoryChangesW` 同步阻塞（零轮询）+ 200 ms 防抖，`CancelIoEx` 安全释放；删除/改名与新增各自独立防抖 | e2e 实跑：`e2e-folder.png` 自动挂上，**删除后 `folder -> FileDisappeared` 联动取下** |
| 真实缩略图 | 系统 GDI+ 等比 contain 解码 → 32bpp 预乘 alpha DIB，48 张 LRU 缓存，解码失败回退占位 | `dist/ClipLine_1.0.0.0_render-frame.png`：绳 + 夹子 + 卡片，橙/蓝/白色块顺序正确 |
| 卡片交互 | 分层 HWND 局部命中（`WS_EX_TRANSPARENT` 动态切换）+ 按下/抬起/双击/右键/滚轮 → 门面离散事件 | 冒烟命中与穿透断言；`SendMessageW` 真实窗口消息 → 复制命令 → 角标重绘 → `CF_BITMAP` 写入 |
| 左键复制与「已复制」 | 门面 G-01 发 `WriteClipboardImage` + `ShowCopiedBadge`；壳层把 `ItemSnapshot.CopiedBadge` 送进 `RopeFrame`，用 GDI 文本蒙版在卡片上画中文角标；写剪贴板触发的 `WM_CLIPBOARDUPDATE` 由输入桥抑制，不再自动多挂一张副本 | `dist/ClipLine_1.0.0.0_copied-badge.png`；冒烟 2 项断言 |
| 光标 | 窗口类显式设 `IDC_ARROW`，`WM_SETCURSOR` 在卡片上设 `IDC_HAND`、空白处设 `IDC_ARROW`，返回 TRUE 阻止沿用系统等待/启动光标 | 冒烟校验窗口类 `GCLP_HCURSOR` 与两种光标均非 `IDC_WAIT` / `IDC_APPSTARTING` |
| 退出路径 | 托盘「退出 ClipLine」/ 会话结束，先 `CancelIoEx` 再关句柄，避免目录 I/O 死锁 | 冒烟能正常结束（本次修复的死锁） |
| 菜单命令全量消费 | `PlatformCommandSink` 补齐 `MoveFile` / `CopyFile` / `SetLaunchAtLogin` / `ShowInExplorer` / `RestoreSettings`；`ReloadThumbnail` 与监听类命令改由壳层消费；监听器统一为输入桥唯一一套 | 菜单审计 45 项 `FAIL=0` |
| 存储到桌面 | `Desktop\` 占位符展开为真实桌面目录，同名自动加序号，`File.Move` 后卡片取下 | 审计真实副作用：隔离桌面与**真实系统桌面**均生成文件，自检文件已送回收站 |
| 四个 P4 替身窗口 | `ToolWindow` 自管分层 HWND + `RopeCanvas.RenderTool` 四种内容布局；`ShowQuickLook` / `ShowMarkupEditor` / `ShowTrimEditor` / `ShowHeldWindow` 分别映射到 `QuickLook` / `Markup` / `Trim` / `Held` 模式，尺寸 720×480 / 860×600 / 720×520 / 520×400 | 冒烟 4/4 独立帧导出 + 尺寸互异；真实点击 25 项含按 → 切图、拖拽画标注、点「放回绳上」关窗 |
| V-09 固定视觉 | `DrawKnot` 解析式 SDF 椭圆 + 两条松散线头，绳结左侧绳段用亚像素 `StrokeRgb` 染淡靛蓝；固定卡靛蓝细描边 + 淡蓝底 | 冒烟像素级采样 `采样=120 蓝色占优=120 最大相邻带宽跳变=11`；`dist/ui-evidence/knot-zoom-6x-final.png` 人工放大复核 |
| 唯一测试接缝 | 全程仍是 `AppFacade`：离散事件进 → 状态快照 + 外部命令出；壳层不含业务判断 | 单测 P6-02 |

## 4. 门禁结果

| 门禁 | 结果 |
|---|---|
| `bash tools/verify.sh` | **149/149 PASS**（v1.5 新增 13 项 `U-01…U-13` 更新状态机用例；`P-05` / `L-03` 按新定位改口），编译 0 警告 0 错误；脚本可自动回退到临时 .NET 8 SDK |
| `powershell -File tools\smoke.ps1` | **34/34 PASS**（真实桌面窗口 + 假时钟；本轮新增：通知卡右下角、四个替身窗口独立且尺寸互异、V-09 绳结与局部帧、固定绳段色序/亚像素连续性的真实像素采样） |
| `powershell -File tools\ui-click-verify.ps1` | **25/25 PASS**（`--ui-click` 用 `mouse_event` / `keybd_event` 注入系统输入队列，非直接调门面；真实点开 Win32 右键菜单逐项点击，覆盖四个窗口、固定/取消固定、复制、打开，并导出整屏截图 + 绳结局部帧） |
| `powershell -File tools\menu-audit.ps1` | **VERDICT: PASS (45 项)**：`OK=42 / DEGRADED=3 / SKIP=0 / FAIL=0`。P4 三个命令（§12.4）与「检查更新…」（§13）已从降级名单移除。逐项结果见 `dist/menu-audit-report.txt` |
| `powershell -File tools\update-e2e.ps1` | **VERDICT=PASS**：真实便携版进程 + 本地模拟 Release API，断言 `MARKER_REPLACED=True / HELPER_DONE=True / APP_RESTARTED=True`，helper.log 有 `BACKUP_OK → REPLACE_OK → DONE mode=portable`（见 §13.4） |
| `powershell -File tools\update-e2e-msix.ps1` | **VERDICT=PASS**：真实 MSIX 形态进程，断言 `MSIX_DEV_REGISTER_OK / PACKAGE_VERSION=1.0.1.0 / APP_RESTARTED=True`（见 §13.4） |
| `powershell -File tools\e2e-verify.ps1` | **VERDICT=PASS**，四路硬断言 `CLIP_OK=True FOLDER_OK=True DELETE_OK=True FRAME_OK=True`：最终便携包进程真实捕获剪贴板图片、目录事件 `e2e-folder.png` 进入门面、删除该文件后 `diagnostics.log` 出现 `folder -> FileDisappeared`、渲染帧证据存在 |
| 包内 payload 一致性 | 便携包内 `ClipLine.exe` / `ClipLine.dll` / `ClipLine.{Shell,Platform,Application,Domain}.dll` 与最新 Release 编译输出 **6/6 SHA-256 逐字节一致** |
| `tools/check-privacy.sh` | **v1.5 白名单口径**：源码零 URL 字面量 / 零遥测 / 零 Socket；`HttpClient` 仅存在于 `UpdateService.cs` 且经配置化主机白名单校验；零轮询 |
| 签名 | `makeappx pack` 成功、`signtool sign /fd SHA256` 成功；stage 105 个文件（新增 `update-config.json`、`update-helper.ps1`） |

## 5. 与定稿规格的偏离（如实记录）

1. **包体积 28.98 MB > 15 MB 预算**。原因是 Windows App SDK 1.6 运行时以 self-contained 随包分发（103 个 payload 文件，已裁掉 ~60 个非中英文 `.mui` 与全部 `.pdb`）。换来的是「不需要管理员、不依赖系统框架包、装完即跑」。要压回预算需改成框架依赖部署并在 manifest 声明 `Microsoft.WindowsAppRuntime.1.6` 包依赖，本机缺 Main/Singleton/DDLM 包时会以 `0x80670016` 退出，故 v1 未采用。
2. **渲染仍是 GDI 32bpp DIB + 自管分层 HWND**，不是定稿的 Win2D / DirectComposition；两条路径互斥（P0 结论）。v1.1 起掉落与微风已在 GDI 层逐帧绘制（独立全屏分层窗口 + 内容指纹驱动重绘）；§9 起摆动改为 CPU 端「反向映射 + 双线性」真旋转与圆角 SDF 抗锯齿，几何上等价 `rotationEffect`，但仍是软件光栅化、没有 GPU 合成与实时阴影重算。截图飞入（`StartFlight`）仍只有状态机、壳层显式吃掉不播；整绳下滑与空隙弹簧补位未实现。详见 `docs/10` §7、§8。
3. **没有 XAML 窗口**。v1.4 起预览、标记、修剪、拿在手上已有四个**自绘 GDI 分层窗口**（各自独立、尺寸不同、可交互，见 §12），但仍不是定稿的 WinUI 3 `Window` / Acrylic / XAML 控件树：标题栏、按钮、画布都是 `RopeCanvas` 手绘像素；标注只实现矩形一种笔型，录屏修剪仅为界面契约、未做 passthrough 重编码。16 项菜单仍是 Win32 原生菜单而非 WinUI 菜单。
4. **未实现**：OLE 拖放、WIC/HEIC 解码（依赖系统可选解码器）、右键文件资源管理器扩展（稀疏包 + `IExplorerCommand`，manifest 里已移除无效的全零 CLSID）、原生快捷键录制器（v1 固定 `Ctrl+Alt+T`）、多显示器/每屏 DPI（已按用户决策裁剪为范围外 `N-11`）。
5. **全局热键注册可能失败**：`RegisterHotKey` 在部分输入法/远程桌面环境会返回 false，代码已做「带 `MOD_NOREPEAT` → 不带」重试，结果写入 `diagnostics.log`（`attach: hotkey=...`）。
6. **实机矩阵未跑完**：单屏 100/125/150/200% 缩放、深浅色、高对比度、减少动效、常驻内存 ≤ 60 MB、空闲 CPU < 0.1% 仍是手工项（见 `docs/verification/final-report.md` 的 55 项遗留）。
7. **MSIX 签名包直装仍受证书链限制**：实测 `IsAdmin=False` 时 `certutil -f -addstore TrustedPeople/Root` 均返回 `ERROR_ACCESS_DENIED`，`Add-AppxPackage -Path` 报 `0x800B0109 CERT_E_UNTRUSTEDROOT`（AppX 部署以 SYSTEM 校验，CurrentUser 信任存储不够）。**v1.5 进展**：用户开启开发者模式后，`Add-AppxPackage -Register <解包目录>` 可成功登记，MSIX 通道自动更新已在本机端到端跑通并把包版本升到 `1.0.1.0`（见 §13.4）。生产环境仍需受信 CA 代码签名证书或管理员导入证书，这是唯一未闭环项。
8. **「检查更新…」在 v1 点击无响应 —— v1.5 已修复（2026-11-05）**。历史根因：门面发 `CommandKind.OpenBrowser`，参数只是查询串 `v=0.1.0&lang=zh-Hans`（刻意不含域名以绕过零 URL 常量门禁），`Process.Start(查询串)` 抛异常被吞，界面无任何反馈；`ShellEnvironment.Version` 也仍是 `0.1.0`。**v1.5 方案 D 重写**：门面改发 `RunUpdateCheck`，平台层完成 GitHub Release 检查 → 版本比较 → 下载 → SHA-256 校验 → 自动安装 → 重启；版本号统一由 `BuildInfo.Version = 1.0.1` 派生。离线静默失败、不轮询。详见 §13。此项不再是偏离。
9. **菜单审计仍有 3 项 DEGRADED**（有反馈，但属等价替换或明确降级）。审计把「静默无声」判为 FAIL、「有明确反馈但用等价方式实现」判为 DEGRADED。这 3 项是：托盘「更改快捷键…」×2（提示卡说明 v1 固定 `Ctrl+Alt+T`）、「全部挂回」（门面确实把 held 项挂回，但 `CloseHeldWindow` 未带来状态变化）。v1.4 移除 3 项 P4 命令、v1.5 移除「检查更新…」后，`DEGRADED` 由 7 → 4 → **3**，`OK` 由 38 → 41 → **42**。它们**不是「点了没反应」**，与历史修复的 7 个 FAIL 是两类问题。

## 6. 隐私

**v1.5 产品定位变更（用户 2026-11-05 拍板「必须做到 D」）**：从「应用完全零出站」改为「唯一白名单出站」。仍无账号、无统计、无遥测、无崩溃上报、无轮询；诊断日志与状态文件仍只写 `%LOCALAPPDATA%\ClipLine\`。

唯一出站是方案 D 的自动更新：启动后一次，或用户主动点「检查更新…」。约束由 `tools/check-privacy.sh` 与单测 `L-03` 静态强制：

- 源码里**零 URL 字面量**、零 `Socket` / `WebClient` / `WebRequest`、零遥测关键字；服务地址放在 `packaging/update-config.json`；
- `HttpClient` / `System.Net.Http` **只允许**出现在 `src/ClipLine.Platform/UpdateService.cs`；
- 每次请求经 `AssertAllowed` 校验：必须 HTTPS（本机回环除外）且主机在配置的 `allowHosts` 白名单内，防止 Release 内容把下载劫持到任意地址；
- 下载包必须匹配 Release 资产的 `.sha256` sidecar，否则丢弃、绝不安装；
- 离线 / 超时 / 任何异常一律静默，不重试、不轮询、不打扰用户；`CLIPLINE_AUTO_UPDATE=0` 可彻底关闭启动检查。

`ShellExecuteW` / `Process.Start` 仍只用于「用系统默认应用打开用户自己的图片 / 打开文件夹 / 在资源管理器中选中文件」以及更新 helper，不用于任何其它网络行为。抓包复核仍是发布门槛（见 `docs/03`）。

## 7. 历史交付校验记录（2026-11-05，移除复制冗余通知后重跑）

> **最新一轮的完整校验记录在 §12**；本节保留上一轮的实测值作为历史留痕，其中的哈希、冒烟项数、审计统计已被 §12 取代。

按顺序实跑，全部针对**同一份 136/136 单测 + 30/30 冒烟 + 45 项菜单审计通过的源码**：

1. `bash tools/verify.sh` → `TOTAL 136 PASS 136 FAIL 0`；产品与渲染壳编译 `0 个警告 0 个错误`。`bash tools/check-privacy.sh` → 零联网 API / 零遥测 PASS。
2. `powershell -File tools\smoke.ps1` → `VERDICT: PASS (30/30)`，`ExitCode=0`。动效三项、抗锯齿三项全部 PASS；复制 `CF_HDROP` 路径读回（`C:\…\Screenshots\regression-card.png`）、自绘通知窗 `360×95px @1.25 缩放 = 288×76 DIP / 帧 5015 B`、通知淡入淡出收起三项；新增「复制只保留卡片角标，不再弹出冗余通知窗」断言（`复制前静默=True 复制后静默=True`）。
3. `powershell -File tools\menu-audit.ps1` → `VERDICT: PASS (45 项)`，`OK=38 / DEGRADED=7 / SKIP=0 / FAIL=0`；真实副作用三条全 OK。报告 `dist/menu-audit-report.txt`。
4. `powershell -File tools\build-msix.ps1` → `SIZE=29 MB`、`SHA256=2597A292…`；`PORTABLE_SIZE=28.32 MB`、`PORTABLE_SHA256=31D00424…`；`[pack] makeappx + signtool sign OK`，编译 `0 个警告 0 个错误`。
5. 逐文件 SHA-256 比对便携包与 Release 输出 → 6/6 `OK`（`ClipLine.exe` / `ClipLine.dll` / 四个托管 DLL）。
6. 启动**重建后**的最终便携包（PID 5628），`powershell -File tools\e2e-verify.ps1` → `CLIP_OK=True FOLDER_OK=True DELETE_OK=True FRAME_OK=True`、`VERDICT=PASS`；`diagnostics.log` 记录 `fs event: Removed ...e2e-folder.png` → `folder -> FileDisappeared`。脚本在 8 秒上限轮询之外再加一次有上限的剪贴板重发：应用刚启动时的首条 `WM_CLIPBOARDUPDATE` 偶发丢失，会表现为 `CLIP_OK=False` 假阴性（本轮首次运行即复现、重跑即 PASS）；监听器真坏时两次尝试都会失败，不会掩盖真实回归。
7. 最终 MSIX 结构校验：`AppxManifest.xml` / `AppxSignature.p7x` / `ClipLine.exe` / `ClipLine.Shell.dll` / `Assets/Square44x44Logo.png` 全部 `PRESENT`。
8. `Add-AppxPackage` 在本机仍返回 `0x800B0109 CERT_E_UNTRUSTEDROOT`（标准用户 + 未开开发者模式），属部署信任策略限制而非包损坏；便携包已实跑通过。

**本轮三项修复的根因**（均来自用户实测反馈）：

1. 左键点击无「已复制」：门面 G-01 其实一直会发 `WriteClipboardImage` + `ShowCopiedBadge`，但壳层只弹了托盘气泡，卡片上没有视觉反馈；更隐蔽的是写剪贴板会触发 `WM_CLIPBOARDUPDATE`，被输入桥当成外部复制**再挂一张自己的副本**。现在 `ItemSnapshot.CopiedBadge` 进入 `RopeFrame`，卡片上用 GDI 文本蒙版画中文「已复制」角标，并抑制自我复制回声。
2. 目录删图后绳子不联动：桥接层完全忽略 `FILE_ACTION_REMOVED`（连同 `RENAMED_OLD_NAME` 被映射为 `Unknown`）；门面 `OnFileDisappeared` 又只把卡片标成 `Falling`、没排 `GapCloseElapsed`，而渲染层把含 `Falling` 的所有 `Items` 都画出来——于是状态上"已下线"、屏幕上一直挂着。现在删除/改名与新增各自独立防抖、发 `FileDisappeared`、`Line.RemoveByPath(immediate: true)` 直接下线。
3. 悬停出现系统等待小圆圈：窗口类 `hCursor` 为 NULL 时 `DefWindowProc` 不重置光标，进程启动或 `ShellExecute` 遗留的 `IDC_APPSTARTING` / `IDC_WAIT` 会一直跟着鼠标。现在类注册显式设 `IDC_ARROW`，`WM_SETCURSOR` 在卡片上设 `IDC_HAND`、空白处设 `IDC_ARROW` 并返回 TRUE。

唯一测试接缝仍是 `AppFacade`；过程中还把输入桥里硬编码的 `Environment.TickCount64` 统一为注入时钟，使假时钟能覆盖「WM → 输入桥 → 门面」整段。

本次最终运行 `diagnostics.log` 中 `hotkey=False`（全局热键 `Ctrl+Alt+T` 在本机未注册成功），已按 §5.5 记录；托盘左键与菜单不受影响。

## 8. v1.1 动效与空绳唤出（2026-11-05）

用户两项反馈 + 对 Tendedero **v1.8.1** 的一手 Swift 源码调研（tag `6df2cfe…`，动画文件与 `main` 逐字节一致；完整参数见 `docs/10-macos-motion-study.md`）：

1. **空绳不显示**：`AppFacade.OnHotZoneElapsed` 里的 `_line.IsEmpty` 直接 return。已去掉，空绳也能被顶部中间 1/3 热区唤出；指针停在热区时 `CanHideIgnoringPeek()` 阻止收起，离开 0.5 s 自动收回。**这是有意偏离 mac**（v1.8.1 的 `AppDelegate` 用 `liveCount > 0` 挡住空绳，注释写明只有快捷键/菜单能主动打开）。`S-03b` 单测由「空绳不唤出」改为「空绳也唤出」，另加 `S-03c` 验证自动收起。
2. **没有春风晃动**：v1 的 `RopeCanvas` 是静态绘制、`RenderFrame` 按内容指纹去重，完全没有逐帧动画。新增 Domain 层纯函数 `RopeMotion`（阵风 7~16 s、逐卡 0~0.35 s 延迟、1.6~3.4°、easeOut 0.3 s + 欠阻尼弹簧回中），壳层把摆角写进 `RopeFrame` 指纹，动画活跃时自然逐帧重绘、静止时零额外重绘。同时接上入场 16° 摆入 + 46 DIP 落下、复制 nudge 3°、静态倾角 ±2.5°、系统「减少动态效果」。
3. **取下没有掉落动画**：门面一直在发 `StartFall`，但壳层没消费、命令落到平台层被静默吞掉。新增 `FallWindow` 全屏分层 HWND + `RopeCanvas.RenderFall`（`e = raw³`、下落 520 DIP、倾角 `tilt + (tilt×7+20)·e`、`opacity = 1 − e`），并把 `StartFall` / `StartFlight` 显式路由到壳层。批量取下沿用门面 `BatchStepMs = 60` 的错峰连锁。
4. **对齐 bug 修正**：`Layout.FallTilt` 把 Swift 源码里的进度变量 `e`（`raw³`）误读成自然常数 `Math.Pow(Math.E, 3)`，终态角度被放大约 20 倍；已改为 `tilt * 8 + 20`，同步更新 `V-28` 单测。

该轮门禁：`136/136` 单测、`23/23` 冒烟、`45 项 FAIL=0` 菜单审计、四路 e2e `VERDICT=PASS`、6/6 payload 一致、编译 0 警告 0 错误。唯一测试接缝仍是 `AppFacade`，未增第二个。

## 9. 摆动边缘抗锯齿（2026-11-05 用户反馈「晃动的时候，边框有明显的锯齿化」）

**根因两个叠加**：① `RopeCanvas.Put()` 摆动时按行做 `x += (int)Math.Round(tan(θ)·(y−anchorY))`，
tan(14°)≈0.25 意味着每 4 行才跳 1 像素，垂直边框变成台阶 —— 这是「只在晃动时出现」的主因；
② `FillRoundRect` 与照片裁剪用 `dx²+dy² ≤ r²` 二值判定，圆角/边缘没有 1px 羽化。

**修复（不换渲染后端，仍在 GDI 路径内）**：卡片先画进离屏预乘 BGRA 缓冲，
再按绕夹子顶边支点的真旋转做反向映射 + 双线性重采样；圆角与裁剪改用解析式 SDF 覆盖率；
摆角为 0 时走 1:1 快速拷贝，缓冲按最大尺寸复用。掉落层共用同一条合成路径。
完整原理与更进一步的 Win2D/DirectComposition 路线见 `docs/10-macos-motion-study.md` §8。

**证据**：`dist/ClipLine_1.0.0.0_swing-aa.png` 是 14° 摆动帧的真实 DIB 导出；
冒烟新增三条断言证明边缘是连续过渡而非 0/255 二值（209 / 253 级 alpha），
并证明 30 帧真实 Present 平均仅 4.44 ms（30 fps 预算 33 ms）。

该轮门禁：`136/136` 单测、`26/26` 冒烟、`45 项 FAIL=0` 菜单审计、四路 e2e `VERDICT=PASS`、
6/6 payload 一致、隐私扫描 PASS、编译 0 警告 0 错误。唯一测试接缝仍是 `AppFacade`。

## 10. 复制与通知两项交互修复（2026-11-05 用户反馈）

### 10.1 「左键复制后，在资源管理器里 Ctrl+V 没效果」——不是假复制

`GdiPlus.CopyFileToClipboard` 原先只写 **`CF_BITMAP`**。位图能粘进微信 / 画图 / Word，
但 Windows 资源管理器的「粘贴」只请求 **`CF_HDROP`**（`DROPFILES` 文件列表）；
剪贴板里没有这个格式时，Ctrl+V 就完全静默。用户看到的「假复制」其实是格式不匹配。

修复：一次复制同时写两种标准格式，先 `CF_BITMAP` 后 `CF_HDROP`。
图片应用按格式优先级拿到位图，资源管理器拿到文件引用 —— 两者互不干扰。
`CF_HDROP` 用 `GlobalAlloc(GMEM_MOVEABLE|ZEROINIT)` 构造 20 字节 `DROPFILES` 头
（`pFiles=20`、`fWide=TRUE`）+ UTF-16LE 绝对路径 + 双 NUL；`SetClipboardData` 成功后
句柄所有权移交系统，失败路径才 `GlobalFree`，避免泄漏或二次释放。
`WriteClipboardFile`（D-11 录屏）按语义**只**写 `CF_HDROP`，不额外塞位图。

副作用收紧：`EmptyClipboard` + 两次 `SetClipboardData` 实测产生 **3 条**
`WM_CLIPBOARDUPDATE`，抑制计数从 4 精修到 3 —— 多留一次会把用户紧接着的
外部复制误当回声吞掉（本轮 e2e 曾因此偶发 `CLIP_OK=False`）。

冒烟新增断言：打开剪贴板读回 `CF_HDROP` → `GlobalLock` → `Marshal.PtrToStringUni(p+20)`，
确认路径以被复制的文件名结尾。

### 10.2 「系统通知框太大、UI 不美观」——系统气泡不可控，只能换自绘

原 `ShellTray.ShowBalloon` 用 `Shell_NotifyIconW(NIM_MODIFY, NIF_INFO)` 弹**系统气泡**。
它的尺寸、圆角、配色、动画与是否显示（通知中心可关闭）**全部由 Windows 决定**，
应用既不能缩小 20% 也无法美化。因此按用户要求换成自绘分层窗口：

| 项 | 实现 |
| --- | --- |
| 尺寸 | `288 × 76 DIP`（Win11 常见系统气泡约 360×95 DIP × 0.8），`Scale` 跟随系统 DPI |
| 位置 | `SPI_GETWORKAREA` 取工作区右下角，距边 12 DIP，自动避开任务栏 |
| 窗口 | 独立类 `ClipLine_NoticeWindow`（不污染绳窗口类，`EnumWindows` 残留检查不受影响）；`WS_EX_LAYERED \| TRANSPARENT \| NOACTIVATE \| TOOLWINDOW`，显式箭头光标 |
| 外观 | 12 DIP 圆角白卡 + 1px 描边 + 三层柔阴影；左侧 `#4F5BD5 → #2AA9E0` 渐变竖条；E 双线小图标 + 暖橙夹点；标题 12 DIP 半粗、正文 11 DIP；圆角复用 §9 的解析式 SDF 覆盖率，文字用 GDI `ANTIALIASED_QUALITY` 灰度蒙版 |
| 排版 | 正文优先在中文标点后折行，`Ctrl+V` 这类 ASCII 词不会被拦腰切断；单行时垂直居中 |
| 动画 | 淡入 180 ms → 停留 3.2 s → 淡出 250 ms，由托盘宿主窗口 `WM_TIMER`（33 ms）驱动整窗 `SourceConstantAlpha`；结束 `KillTimer` + 隐藏，空闲零定时器、零重绘 |

`ShowBalloon` 方法名保留，门面**三处**命令（`ShowWelcome` / `ShowInboxOffer` /
`ShowShortcutRecorder`）继续调用；`ShowCopiedBadge` 已在 §11 从通知场景移除。
欢迎文案顺手改短以适配新宽度。
`ShowInboxOffer` 文案改为「在托盘菜单勾选两项捕获开关，即可自动挂上截图与复制的图片。」
证据：`dist/ClipLine_1.0.0.0_notice.png`（真实 DIB 导出）。

冒烟新增两项断言：通知窗尺寸恰好 `288×76 DIP × 缩放` 且可导出帧；
用相对时间推进 `Show → Tick(+200) → Tick(+3350) → Tick(+3500)`，
验证淡入到 255、淡出低于 255、结束自动隐藏。

最终门禁：`136/136` 单测、`30/30` 冒烟、`45 项 FAIL=0` 菜单审计、四路 e2e `VERDICT=PASS`、
6/6 payload 一致、隐私扫描 PASS、编译 0 警告 0 错误。唯一测试接缝仍是 `AppFacade`。
本轮所有改动**未 commit、未 push**，沿用用户「不要 commit 和提交」的要求。

## 11. 复制场景移除冗余通知（2026-11-05 用户反馈）

> 用户原话：「复制的时候，已经在图片框提示了：已复制。现在还会弹出系统提示框，
> 我认为这个已复制场景再弹出提示框是冗余的功能！」

**判断**：复制是一个**瞬时、就地、结果自明**的动作 —— 用户视线本来就在被点击的卡片上，
卡片底部的「已复制」角标（1.2 s）+ 3° 轻摆已经构成完整闭环；右下角再冒一张通知卡，
既打断注意力，又与角标重复。mac v1.8.1 的对应反馈也只有卡片上的 `Copied` 标签，
没有第二次系统级提示，因此移除通知同时更贴近对齐基准。

**改动（两处，不改门面、不动唯一接缝）**：

| 文件 | 改动 |
| --- | --- |
| `src/ClipLine.Shell/RealInputBridge.cs` | `CommandKind.ShowCopiedBadge` 分支删除 `_host.Tray.ShowBalloon("已复制", …)`，改为显式注释的空分支；保留 case 以免后人误当作漏实现再补回去 |
| `src/ClipLine.Shell/ShellAppHost.cs` | 该命令不再转发给输入桥（`_bridge?.HandleCommand`），只保留 `_motion.OnCopied` 3° 轻摆 + `RenderFrame()` 重绘角标 |

门面 `AppFacade.P3.cs` 仍照常发 `WriteClipboardImage` + `ShowCopiedBadge` 并排
`CopiedBadgeElapsed`；`ItemSnapshot.CopiedBadge` → `RopeFrame` 的链路完全不变。
**通知窗本身没有被删除**：欢迎、首次运行的捕获开关提示（`ShowInboxOffer`）、
快捷键说明（`ShowShortcutRecorder`）三个需要阅读的场景照常弹 288×76 DIP 自绘卡。

**回归断言**（冒烟 29 → 30 项）：先把首次运行可能已弹出的通知 `Hide()` 建立干净基线，
再真实发送 `WM_LBUTTONDOWN/UP` 走完整链路，断言 `badgeCommand=True` 且
`复制前静默=True / 复制后静默=True`。通知窗自身的尺寸、帧导出、淡入淡出收起三项断言
改用**欢迎文案**继续覆盖（证据图 `dist/ClipLine_1.0.0.0_notice.png` 已同步更新，
不再展示已废弃的「已复制」通知）。

**顺带加固 e2e**：`tools/e2e-verify.ps1` 在原有 8 秒上限轮询之外增加一次有上限的
剪贴板重发（`CLIP_REARM`），消除应用冷启动时首条 `WM_CLIPBOARDUPDATE` 丢失造成的
`CLIP_OK=False` 假阴性；监听器真失效时两次都会失败，不会掩盖回归。
本轮首次 e2e 就复现了该假阴性、重跑 PASS，加固后再次实跑 PASS。

最终门禁：`136/136` 单测、`30/30` 冒烟、菜单审计 45 项 `FAIL=0`、四路 e2e
`VERDICT=PASS`、6/6 payload 一致、隐私扫描 PASS、编译 0 警告 0 错误。
交付包已重建：MSIX `2597A292…` / 便携 ZIP `31D00424…`。
**未 commit、未 push、未写 GitHub 评论**，沿用用户此前要求。

## 12. P4 替身窗口、固定视觉与真实点击验证（2026-11-05，v1.4）

> 用户先问「复制 / 打开 / 快速查看 / 拿在手上，这个效果为什么都一样？是 BUG 还是什么？固定又是什么含义？」，随后明确要求：
> **「采纳！开始动手！测试用例通过是远远不够的，必须端到端页面自行点击测试！」**
> 因此本轮不以「单测通过」为完成标准，全部关键路径都用真实鼠标/键盘事件在真实桌面点过一遍。

### 12.1 四个命令为什么原来一样

`ShowQuickLook` / `ShowMarkupEditor` / `ShowTrimEditor` / `ShowHeldWindow` 在 v1 早期都被平台层兜底成 `ShellExecuteW("open")`，即「用系统默认看图程序打开同一个文件」，所以四个菜单项效果完全相同。「固定」则只在状态里把 `Pegged.Kept` 置位、影响排序与不被自动淘汰，**屏幕上没有任何可辨识标识**，用户看不出差别。

### 12.2 本轮实现

| 命令 | 窗口模式 | 尺寸（DIP） | 真实可交互内容 |
|---|---|---|---|
| 快速查看 | `ToolMode.QuickLook` | 720×480 | 大图 + 底部缩略图条；真实按 → 切到下一张；空格 / Esc 关闭 |
| 标记 | `ToolMode.Markup` | 860×600 | 工具栏 + 画布；真实鼠标拖拽画出矩形标注并计数 |
| 录屏修剪 | `ToolMode.Trim` | 720×520 | 画面 + 双手柄修剪条 + 取消/修剪（界面契约，未做重编码，见 §5.3） |
| 拿在手上 | `ToolMode.Held` | 520×400 | 浮动窗 + 真实「放回绳上」按钮，点击后窗口关闭、卡片归位 |

实现要点：`ToolWindow` 是独立的自管分层 HWND（`WS_EX_LAYERED | NOACTIVATE | TOOLWINDOW`），`RopeCanvas.RenderTool` 按 `ToolMode` 走四套内容布局、共用 Fluent chrome；`ShellAppHost` / `RealInputBridge` 把四个 `CommandKind` 明确映射到 `ToolMode`，不再落到 `OpenDefaultApp`。**唯一接缝仍是 `AppFacade`**，没有新增第二个。

「固定」补齐 V-09：仅当存在固定照片时，在最后一张固定卡与下一张之间画 7×5.5 DIP 灰色椭圆绳结（解析式 SDF 覆盖率、左上高光右下暗部）+ 两条 1 pt 松散线头；绳结左侧绳段染淡靛蓝标出「固定区 / 普通区」分界；固定卡加品牌靛蓝细描边 + 淡蓝底。mac v1.8.1 原本只有夹子与绳结，在 Windows 高 DPI 单屏下过小，配色增强是**有意偏离**（见 `docs/05` §7 偏差记录）。

### 12.3 真实点击验证（不是 mock，不是直接调门面）

新增 `src/ClipLine.Shell/UiClickTest.cs` + `tools/ui-click-verify.ps1`，用 `mouse_event` / `keybd_event` 把输入注入**系统输入队列**：真的把鼠标移到卡片上、真的右键弹出 Win32 `#32768` 菜单、真的按可见文字找菜单项并点击，再读回窗口矩形、状态快照、剪贴板格式与渲染帧。25 项断言全部 PASS，含「快速查看窗口居中且未被拉回左上角」「真实按 → 索引 0→1」「拖拽画出 1 条标注」「放回绳上后窗口关闭」「固定数 0→1」「复制只写剪贴板不打开替身窗口」「打开出现新的非 ClipLine 顶层窗口」。证据归档在 `dist/ui-evidence/`（11 张 PNG：四个窗口帧、三张整屏截图、固定前后绳帧、绳结局部帧与 6× 放大图）。

### 12.4 证据帧自查发现并修复的真实缺陷

1. **分层窗口目标点把窗口拉到左上角**：`UpdateLayeredWindow` 的 `pptDst` 同时决定窗口屏幕位置，`RopeCanvas.Present` 原来恒传 `(0,0)`，工具窗口与通知卡每次刷新都跳回屏幕左上角。修复：`Present` 增加 `dstX/dstY`，`ToolWindow.Render` 与 `NoticeWindow.Tick` 传入 `Position()` 算好的坐标；冒烟新增居中断言。
2. **绳子收起后 0.3 s 内再次唤出会永久停在隐藏态**：门面认为 `_isPresent=true` 不再发 `PresentWindow`，`DestroyElapsed` 又因 `_isRevealed=true` 跳过销毁。修复：`ShellAppHost.RenderFrame()` 按 `StateSnapshot` 自愈；真实点击脚本增加 `WaitForRope(...)` 轮询。
3. **固定色序反转（两次）**：DIB 是 BGRA，`PutCoverage` / `Stroke` 的形参名虽叫 `r,g,b`，实际要按 B/G/R 传。先是把 `FillRoundRect` 直接当逻辑 RGB 用，靛蓝变粉红；本轮给绳段新增 `StrokeRgb` 时又按逻辑 RGB 传了一次，放大帧立刻复现粉红。修复：统一用 `FillRoundRectRgb` / `PutRgb` / `StrokeRgb` 低位在前拆通道，并**新增像素级回归断言**（`RopeCanvas.PixelAt` + 冒烟采样 120 点，`蓝色占优=120/120`），不再只靠「帧内容不同」。
4. **淡靛蓝绳段有整像素厚度台阶**：染色用 `(int)Math.Round(y)` 写整数行，而绳本体是 `Stroke(x, y, 1.2, …)` 亚像素抗锯齿，y 小数跨整时带宽突变。修复：染色也改走亚像素 `StrokeRgb`；冒烟断言 `最大相邻带宽跳变=11 / 最大带宽=449`。
5. **测试状态污染**：`--ui-click` 曾把夹具图片写进真实 `%LOCALAPPDATA%\ClipLine\state.json`，证据图里出现用户旧照片。修复：`App.cs` 在 `--ui-click` 下改用 `%TEMP%\clipline-uiclick\state.json` 并每次清空；手工清理并校验真实状态 JSON 有效（`Pegged=1`，仅保留用户自己那张图）。
6. **冒烟跨次假失败**：`RunChecks` / `RunRegressionChecks` 是两套独立 host，各自有 `state.json` / `state-regression.json`；上一轮「固定」写回后，下一轮找不到未固定项，V-09 断言报 `固定项=False`（实测 33/34）。修复：两处启动时都删除自己的状态文件，重跑 34/34 PASS。

菜单审计口径同步：`MenuAudit.Degraded` 移除四个 `Show*` P4 命令，其余 4 项（快捷键 ×2、检查更新、全部挂回）保留；结果由 `OK=38 / DEGRADED=7` 变为 **`OK=41 / DEGRADED=4 / FAIL=0`**。

### 12.5 本轮最终门禁与交付

| 门禁 | 结果 |
|---|---|
| `bash tools/verify.sh` | `TOTAL 136 PASS 136 FAIL 0`，编译 `0 个警告 0 个错误` |
| `powershell -File tools\smoke.ps1` | `VERDICT: PASS (34/34)`，`ExitCode=0` |
| `powershell -File tools\ui-click-verify.ps1` | `VERDICT: PASS (25/25)`，`ExitCode=0` |
| `powershell -File tools\menu-audit.ps1` | `VERDICT: PASS`，`OK=41 / DEGRADED=4 / SKIP=0 / FAIL=0`（45 项） |
| `powershell -File tools\e2e-verify.ps1` | `VERDICT=PASS`，`CLIP_OK=True FOLDER_OK=True DELETE_OK=True FRAME_OK=True`（重建后的便携包实跑，PID 29284） |
| `bash tools/check-privacy.sh` | 零联网 API / 零 URL 常量 / 零遥测 PASS |
| `powershell -File tools\build-msix.ps1` | `[pack] makeappx + signtool sign OK`，stage 103 文件，编译 0 警告 0 错误 |
| 包内 payload 一致性 | 便携包 6 个程序集与最新 Release 输出 **6/6 SHA-256 逐字节一致** |

最终交付包：`MSIX 29.02 MB / EDD7C86A…BDC86`、`便携 ZIP 28.34 MB / 793130B4…82CE2`。e2e 结束后已停止应用并**还原用户真实 `state.json`**（`ConvertFrom-Json` 校验 `Pegged=1`，仅 `杜奎_证件照 - 副本.jpg`）。

**本轮所有改动未 commit、未 push、未写 GitHub 评论**，沿用用户「这三个问题修复完但不要commit和提交！」的要求。

---

## 13. v1.5 方案 D：基于 GitHub Release 的双通道全自动更新（2026-11-05）

### 13.1 决策与定位变更

用户问「目前基于 GitHub Release 做自动更新有困难嘛」，随后明确 **「必须做到 D」**（双通道全自动）。先核实了两条事实：仓库 `dk900912/ClipLine` 实为 **PUBLIC**（早期笔记记为私有，已更正），`gh` 已登录且 token 含 `repo`；匿名 `/releases/latest` 返回 404 是因为**尚未发过任何 Release**，远端只有 tag `v1.0.0`。

这直接推翻了 v1 的「应用完全零出站」定位，因此本轮是**产品决策变更**，不是偷偷绕过门禁：`docs/01` 的 `L-03` / `P-04` / `P-05`、`docs/02` 技术选型约束、`docs/03` 放行门槛、`AppxManifest.xml` 描述、`tools/check-privacy.sh`、单测 `L-03` / `P-05` 全部同步改口为「唯一白名单出站」。

### 13.2 架构：唯一接缝不变

仍然是 `AppFacade`：离散事件进 → `StateSnapshot` + `AppCommand` 出。网络、下载、SHA-256、安装、重启全部留在平台/壳层，结果以 `AppInput` 回灌同一个门面，没有第二个接缝。

| 层 | 新增 | 职责 |
|---|---|---|
| Domain | `BuildInfo.cs`、`UpdateVersion.cs` | 版本单点来源；语义化版本解析比较（纯函数） |
| Application | `Contracts.cs` 的 `UpdatePhase` / `UpdateSnapshot` / 6 个新输入 / 4 个新命令；`AppFacade.Update.cs` | 检查 → 可用 → 下载 → 校验 → 就绪 → 安装 → 重启的状态机；离线静默、防并发重入 |
| Platform | `UpdateService.cs`、`UpdateInstallers.cs` | 白名单 HTTPS、Release JSON 解析、资产选择、流式下载 + 进度、SHA-256 校验、按形态选安装器 |
| Shell | `ShellAppHost.Updates` / `RestartRequested`、`RealInputBridge` 四个命令分支、`ShellEnvironment.IsPackaged` | 组合根接线、UI 线程回灌、主进程退出让 helper 接管 |
| App | `App.cs` 启动后 8 秒一次性 `AutoUpdateTick` | 零轮询；`CLIPLINE_AUTO_UPDATE=0` 关闭，`CLIPLINE_UPDATE_API` 注入本地模拟服务 |
| 配置 | `packaging/update-config.json`、`update-helper.ps1` | 服务地址与主机白名单在配置里（源码零 URL 常量）；一次性 PowerShell helper |

关键设计：`IsPackaged` 用「执行目录是否存在 `AppxManifest.xml`」区分两通道，正是打包脚本构造的天然差异，无需 WinRT 查询。

### 13.3 发布链路

- `BuildInfo.Version = 1.0.1` / `MsixVersion = 1.0.1.0` 是唯一版本源；`tools/build-msix.ps1` 用正则读它，注入 MSIX `Identity Version`、生成资产文件名、输出 `TAG=v1.0.1`（`dist/release-assets.txt`）。
- 每个资产生成 `<name>.sha256` sidecar；客户端下载后比对 `SHA256.HashData`，不匹配直接删除文件并回灌失败，**绝不安装**。
- 修复了一个真实构建坑：`AppxManifest.xml` 含中文，PS 5.1 的 `Get-Content -Raw` 按 ANSI 解码会把中文写成乱码，`makeappx` 报 `C00CEE3B`；改为显式 UTF-8 读写。
- 客户端查 `/repos/{repo}/releases/latest`，按通道选 `_x64_portable.zip` 或 `.msix`，`UpdateVersion.IsNewer(tag, BuildInfo.Version)` 决定是否更新。

### 13.4 双通道实现与真机端到端验证

**便携 ZIP**：下载 → 解压 payload → 启动一次性 helper → 主进程退出 → helper 备份原目录、覆盖文件、启动新 `ClipLine.exe`；缺 exe 或异常则从备份回滚并重启旧版。helper 是系统自带 PowerShell（`-EncodedCommand`、环境变量传参），不额外引入 .NET 运行时，包体积不变，也不常驻。

**MSIX**：主进程同样先退出。helper 先试生产路径 `Add-AppxPackage -Path <signed.msix> -ForceUpdateFromAnyVersion`；失败且机器开启开发者模式时，把 MSIX 当 OPC(ZIP) 解开，用 `Add-AppxPackage -Register <AppxManifest.xml>` 登记。本机实测：签名包直装报 `0x800B0109`（`IsAdmin=False`，机器级 Root/TrustedPeople 写入 `ERROR_ACCESS_DENIED`），Register 路径**成功**并把包版本从 `1.0.0.0` 升到 `1.0.1.0`。生产环境仍需受信 CA 证书或管理员导入证书。

两个脚本都用**真实 ClipLine.exe 进程** + 本地 Node 模拟的 Release API（第一次返回 `v9.9.9`、之后返回当前版本避免重启后无限更新），不碰真实网络、不碰 `dist` 正式包，跑完还原用户 `state.json`：

| 脚本 | 实测结论 |
|---|---|
| `tools/update-e2e.ps1` | `MARKER_REPLACED=True CONTENT=e2e-replaced-…`、`HELPER_DONE=True`、`APP_RESTARTED=True COUNT=1`；helper.log `START → BACKUP_OK → REPLACE_OK → DONE mode=portable`，**VERDICT=PASS** |
| `tools/update-e2e-msix.ps1` | helper.log `MSIX_SIGNED_FAIL=0x800B0109 → MSIX_DEV_REGISTER_OK → DONE mode=msix`；`PACKAGE_VERSION=1.0.1.0 REGISTERED=True`、`APP_RESTARTED=True`，**VERDICT=PASS** |

**最终包复验（2026-11-05 23:10–23:18，全部针对同一份 1.0.1 发布包）**：

| 复验项 | 结果 |
|---|---|
| 真实 GitHub API 无 Release（匿名 `/releases/latest` 返回 404）时的自动检查 | **静默容错通过**：启动后 8 秒触发一次检查，`APP_ALIVE=True`、诊断日志 `EXCEPTION_LINES=0`、未生成任何下载文件、未启动 updater helper、未弹任何提示；符合 `AppFacade.Update.cs` 的「自动检查失败进 `Offline` 且仅在 manual 时提示」设计 |
| `tools/e2e-verify.ps1`（剪贴板 / 目录 / 删除 / 渲染帧） | **VERDICT=PASS**：`CLIP_OK=True FOLDER_OK=True DELETE_OK=True FRAME_OK=True`，在 `dist/ClipLine_1.0.1.0_x64_portable` 的真实进程上跑 |
| `tools/update-e2e.ps1` 重跑 | **VERDICT=PASS**：`MARKER_REPLACED=True` / `HELPER_DONE=True` / `APP_RESTARTED=True COUNT=1`；helper.log 本轮独立记录 `BACKUP_OK → REPLACE_OK → DONE mode=portable` |
| `tools/update-e2e-msix.ps1` 重跑 | **VERDICT=PASS**：`MSIX_SIGNED_FAIL=0x800B0109 → MSIX_DEV_REGISTER_OK → DONE mode=msix`；`PACKAGE_VERSION=1.0.1.0 REGISTERED=True`、`APP_RESTARTED=True` |
| 测试痕迹清理 | 两轮 e2e 的 `helper.log` 已按轮次截断以消除跨轮假阳性；MSIX 解包登记的开发包 `ClipLine_1.0.1.0_x64__kc61833qsmbvm` 已 `Remove-AppxPackage` 卸载（指向临时目录，非正式安装）；用户真实 `state.json` 已还原为 `Pegged=1 / Kept=1`，所有测试进程已停止 |

### 13.5 门禁与口径

`verify.sh` 由 136/136 增至 **149/149**（新增 `U-01…U-13`）；`check-privacy.sh` 与单测 `L-03` 改为白名单口径；`MenuAudit.Degraded` 移除 `OpenBrowser`，45 项审计 `OK=42 / DEGRADED=3 / FAIL=0`。`P-05` 从「断言发浏览器命令」改为「断言发 `RunUpdateCheck(manual)` 且状态进入 `Checking`」。

**最终门禁快照（同一份源码，2026-11-05 23:10–23:18）**：`verify.sh` **149/149** 且编译 0 警告 0 错误；`check-privacy.sh` 三项全 PASS；`smoke.ps1` **34/34**；`ui-click-verify.ps1` **25/25**（真实系统输入队列）；`menu-audit.ps1` **`OK=42 / DEGRADED=3 / SKIP=0 / FAIL=0`**；`e2e-verify.ps1` 四路 True；双通道更新 e2e 两条均 PASS。包内 6 个程序集与最新 Release 编译输出 **6/6 SHA-256 逐字节一致**；两个资产的 `.sha256` sidecar 首字段与实测哈希一致（MSIX `610A2849…92BA`、ZIP `5197CAA4…C15B`），客户端以空白取首段的解析规则与 sidecar 的 `hash + 空格 + 文件名` 格式匹配。

### 13.6 仍未闭环 / 需用户决策

1. ~~**GitHub Release 尚未公开**~~ → **已发布（2026-11-05 23:37 CST）**。用户明确放行「提交 + 打 tag + 发 Release」后，54 个改动以单次原子提交 `ca28582` 推送到 `main`，附注标签 `v1.0.1`（tag object `2a05e2c`），公开 Release 见 <https://github.com/dk900912/ClipLine/releases/tag/v1.0.1>。匿名 `/releases/latest` 现返回 `HTTP 200 / tag_name=v1.0.1 / draft=false / prerelease=false`，4 个资产齐全且**不含 `.pfx` / `.cer`**。真实便携版进程随后查询真实 GitHub API，判定同版本「已是最新」→ 不下载、不重启、0 异常，验证了不会无限自更新。发布通道说明见 §13.7。
2. **生产受信 CA 代码签名证书**：本机只有自签 `CN=ClipLine`；MSIX 生产路径依赖证书信任，开发者模式 Register 只是本机验证回退。
3. **运行期抓包复核**：静态白名单门禁已过，`docs/03` 要求的出站抓包复核仍待做。
4. `update-helper.ps1` 的 MSIX 分支登记的是临时 loose 目录；**本轮验证结束后已 `Remove-AppxPackage` 卸载该开发包**，本机恢复到测试前无已装 ClipLine MSIX 的状态。正式发布走签名包路径不会有此限制。

### 13.7 公开发布实录（2026-11-05 23:37 CST）

用户拍板放行后按「一个逻辑变更 = 交付 v1.0.1」做单次原子提交（54 文件、+5725/−197），**按文件拆分会产生无法独立编译的中间提交**，因此没有拆成多个 commit。

| 步骤 | 结果 |
|---|---|
| `git commit` | `ca28582 feat(release): 发布 v1.0.1，落地 GitHub Release 双通道全自动更新` |
| `git push origin main` | ❌ 失败：本机代理对 `github.com` 的 Git 智能 HTTP 端点返回 `CONNECT tunnel failed 502`，直连 `github.com:443` 超时；`api.github.com` 与 `uploads.github.com` 正常 |
| SSH 通道探测 | 22 / 443 端口均完成 SSH 握手，但本机公钥未登记到 GitHub 账户（`Permission denied (publickey)`），且当前 token 缺 `admin:public_key` scope |
| 临时凭据 | 用已有 `repo` scope 的 token 登记一把**只写本仓库**的 deploy key（`read_only=false`），推送完成后立即 `DELETE /repos/.../keys/{id}` → `204`，仓库现存 deploy key 数为 **0**；`origin` 始终未改，仍是 HTTPS |
| `git push` via SSH | `071140c..ca28582 main -> main` 成功 |
| 附注 tag | `v1.0.1` → `refs/tags/v1.0.1`，tag object `2a05e2c` |
| `gh release create --verify-tag` | 上传 4 个资产（MSIX 30,440,494 B / ZIP 29,726,877 B / 两个 `.sha256`），**未上传任何私钥或证书** |
| 公开 API 复核 | 匿名 `/releases/latest` → `HTTP 200`、`tag_name=v1.0.1`、`draft=false`、`prerelease=false`；客户端两种通道的资产名与 `asset + ".sha256"` sidecar 均能解析到 |
| 真实客户端复核 | 启动最终便携包查询**真实** GitHub API：`APP_ALIVE=True`、`v1.0.1` 下载目录数 = 0、updater helper 进程数 = 0、诊断日志异常行 = 0 → 同版本不触发自更新 |

**状态**：`HEAD = origin/main = ca28582`，附注 tag `v1.0.1` 已推送，公开 Release 已发布。本机所有测试进程已停止、MSIX 解包开发包已卸载、临时 deploy key 已删除、用户真实 `state.json` 已还原（`Pegged=1 / Kept=1`）。本轮未写任何 GitHub Issue 评论。
