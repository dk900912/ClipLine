# ClipLine v1.0.0 发布说明

> 状态：**已构建、已签名、已在本机实跑验证**。本文档是 v1 的唯一放行记录，取代 `docs/06` / `docs/08` 中关于「尚未打包」的旧表述。
> 构建日期：2026-11-05；目标平台：Windows 11 x64（单显示器 + 单一系统缩放）。

## 1. 交付物

| 文件 | 说明 | 大小 | SHA-256 |
|---|---|---|---|
| `dist/ClipLine_1.0.0.0_x64.msix` | 正式 MSIX，已用 `CN=ClipLine` 代码签名证书签名（`SHA256`） | 28.97 MB | `83D1D36CA6DC0B245E913D5FA962EC7028D477C188DB427355C564B5129EFCC9` |
| `dist/ClipLine_1.0.0.0_x64_portable.zip` | 免签名、免安装的同 payload 便携包，解压后双击 `ClipLine.exe` | 28.29 MB | `4E9731ED46880DADD10CAF5086DEEA31B22DAE475473B2C849992EC8BF170595` |
| `dist/ClipLine.cer` / `ClipLine.pfx` | 本地开发签名证书（公钥 / 带口令私钥，口令见构建脚本） | — | — |
| `dist/ClipLine_1.0.0.0_render-frame.png` | **确定性渲染证据**：应用把当前 32bpp DIB 帧导出的 PNG，绳 + 夹子 + 真实缩略图卡片（橙/蓝/白三色块顺序正确） | 15 KB | — |
| `dist/e2e-desktop.png` | 真机运行时的桌面环境截图，仅作运行记录；**不作为渲染证明**（见下方说明） | — | — |

> 上表哈希是 2026-11-05 最终交付构建的实测值。重新运行 `tools/build-msix.ps1` 会因签名时间戳产生不同哈希；`dist/` 已由 `.gitignore` 排除，产物需从脚本复现。
>
> **关于桌面截图**：`Graphics.CopyFromScreen` 在本机 DWM 下不保证抓到分层窗口，且可能被其他顶层窗口（本次是 WPS Office）遮挡，`CaptureBlt` 又返回全黑。因此渲染正确性以应用内导出的 `ClipLine_1.0.0.0_render-frame.png` 为准；真机窗口状态另由 `diagnostics.log` 的 `window: present=True revealed=True ... cards=N` 佐证。

复现构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-msix.ps1
```

脚本零删除、零联网，只依赖本机已有的：.NET 8 SDK（`%TEMP%\clipline-tools\dotnet`，或系统 `dotnet`）、Windows SDK 10.0.26100 x64 的 `makeappx.exe` / `signtool.exe`、系统 `gdiplus.dll`、Windows App SDK 1.6 NuGet 包（已缓存）。**本机不需要 Visual Studio，也不需要 Windows App SDK 运行时框架包**——运行时以 self-contained 方式随包分发。

## 2. 安装与使用

### 便携版（推荐，零门槛）

解压 `ClipLine_1.0.0.0_x64_portable.zip` 到任意目录，双击 `ClipLine.exe`。无需管理员、无需证书、不写注册表、不联网。

### MSIX 版

MSIX 的签名链必须被目标机器信任。以管理员身份执行一次：

```powershell
certutil -f -addstore "TrustedPeople" ClipLine.cer
# 或双击 ClipLine.cer → 安装证书 → 本地计算机 → 受信任的根证书颁发机构
Add-AppxPackage -Path .\ClipLine_1.0.0.0_x64.msix
```

`tools/trust-cert.ps1` 会自动判断权限：管理员则静默导入机器存储；非管理员则打印手动步骤，不会挂起。

**本机当前限制**：本机账户是标准用户（非管理员）、开发者模式未开启，向受信任根写入会弹交互式确认框。因此 `Add-AppxPackage` 在本机返回 `0x800B0109 CERT_E_UNTRUSTEDROOT`——这是部署策略限制，不是包损坏：`makeappx pack` 与 `signtool sign` 均成功，包内 `AppxSignature.p7x` 完整。本机已改用便携版完成实机验证。

### 首次使用

按 C-01 / F-04 规格，「挂上复制的图片」与「接管截图」默认 **opt-in 关闭**，首次启动会弹系统气泡告知开启入口：

> 右键托盘图标 → 勾选「挂上复制的图片」与「接管截图」

开启后：

- 复制任意图片（`Win+Shift+S`、截图工具、浏览器复制图片）→ 自动落到 `%LOCALAPPDATA%\ClipLine\Screenshots\` 并挂上绳。
- 图片保存到 `图片\Screenshots` 或 ClipLine 自有目录 → 自动挂上。
- 鼠标移到屏幕顶端热区 → 绳子放下；离开后自动收起。
- 左键托盘图标 / `Ctrl+Alt+T` → 显示或隐藏绳子。
- 卡片：左键单击、双击快速查看、右键 14 项菜单、滚轮滚动；快速查看/标记/修剪交给系统默认应用。

设置持久化在 `%LOCALAPPDATA%\ClipLine\state.json`，诊断日志在同目录 `diagnostics.log`（纯本地、无统计、无网络）。

## 3. v1 真实落地的能力

| 能力 | 实现 | 验证 |
|---|---|---|
| 托盘三态图标 | 独立 message-only 宿主窗口 + `Shell_NotifyIconW`，16px 独立绘制 | 冒烟 16/16 |
| 16 项托盘菜单 | `TrayMenuModel.Build` → Win32 `CreatePopupMenu` / `AppendMenuW` 递归子菜单，中文标签，每次打开重建 | 单测 T-05 系列 + 冒烟结构自检 18/18 项 |
| 剪贴板图片 | `AddClipboardFormatListener` → `CF_BITMAP` 或 `CF_DIB` → GDI+ 编 PNG → `NewItem` | e2e 实跑：`clip-20261105-083626-527.png` |
| 目录监听 | `ReadDirectoryChangesW` 同步阻塞（零轮询）+ 200 ms 防抖，`CancelIoEx` 安全释放 | e2e 实跑：`e2e-folder.png` 自动挂上 |
| 真实缩略图 | 系统 GDI+ 等比 contain 解码 → 32bpp 预乘 alpha DIB，48 张 LRU 缓存，解码失败回退占位 | `dist/ClipLine_1.0.0.0_render-frame.png`：绳 + 夹子 + 卡片，橙/蓝/白色块顺序正确 |
| 卡片交互 | 分层 HWND 局部命中（`WS_EX_TRANSPARENT` 动态切换）+ 按下/抬起/双击/右键/滚轮 → 门面离散事件 | 冒烟命中与穿透断言 |
| 退出路径 | 托盘「退出 ClipLine」/ 会话结束，先 `CancelIoEx` 再关句柄，避免目录 I/O 死锁 | 冒烟能正常结束（本次修复的死锁） |
| 唯一测试接缝 | 全程仍是 `AppFacade`：离散事件进 → 状态快照 + 外部命令出；壳层不含业务判断 | 单测 P6-02 |

## 4. 门禁结果

| 门禁 | 结果 |
|---|---|
| `bash tools/verify.sh` | **128/128 PASS**，编译 0 警告 0 错误；脚本可自动回退到临时 .NET 8 SDK |
| `powershell -File tools\smoke.ps1` | **16/16 PASS**（真实桌面窗口 + 假时钟；含渲染帧 PNG 导出与 16 项 Win32 菜单结构自检 18/18） |
| `powershell -File tools\e2e-verify.ps1` | **VERDICT=PASS**，三路硬断言 `CLIP_OK=True FOLDER_OK=True FRAME_OK=True`：最终便携包进程真实捕获剪贴板图片、目录事件 `e2e-folder.png` 进入门面、渲染帧证据存在；`diagnostics.log` 记录 `cards=8→10` |
| 包内 payload 一致性 | 便携包内 `ClipLine.exe` / `ClipLine.dll` / `ClipLine.{Shell,Platform,Application,Domain}.dll` 与最新 Release 编译输出 **6/6 SHA-256 逐字节一致** |
| `tools/check-privacy.sh` | 零联网 API（P6 门禁内含） |
| 签名 | `makeappx pack` 成功、`signtool sign /fd SHA256` 成功；stage 103 个文件 |

## 5. 与定稿规格的偏离（如实记录）

1. **包体积 28.97 MB > 15 MB 预算**。原因是 Windows App SDK 1.6 运行时以 self-contained 随包分发（103 个 payload 文件，已裁掉 ~60 个非中英文 `.mui` 与全部 `.pdb`）。换来的是「不需要管理员、不依赖系统框架包、装完即跑」。要压回预算需改成框架依赖部署并在 manifest 声明 `Microsoft.WindowsAppRuntime.1.6` 包依赖，本机缺 Main/Singleton/DDLM 包时会以 `0x80670016` 退出，故 v1 未采用。
2. **渲染仍是 GDI 32bpp DIB + 自管分层 HWND**，不是定稿的 Win2D / DirectComposition；两条路径互斥（P0 结论）。飞入/下落/微风动画只有状态机，没有合成器动画。
3. **没有 XAML 窗口**。预览、标记、修剪、拿在手上以「系统默认应用打开」或气泡等价替换；16 项菜单是 Win32 原生菜单而非 WinUI 菜单。
4. **未实现**：OLE 拖放、WIC/HEIC 解码（依赖系统可选解码器）、右键文件资源管理器扩展（稀疏包 + `IExplorerCommand`，manifest 里已移除无效的全零 CLSID）、原生快捷键录制器（v1 固定 `Ctrl+Alt+T`）、多显示器/每屏 DPI（已按用户决策裁剪为范围外 `N-11`）。
5. **全局热键注册可能失败**：`RegisterHotKey` 在部分输入法/远程桌面环境会返回 false，代码已做「带 `MOD_NOREPEAT` → 不带」重试，结果写入 `diagnostics.log`（`attach: hotkey=...`）。
6. **实机矩阵未跑完**：单屏 100/125/150/200% 缩放、深浅色、高对比度、减少动效、常驻内存 ≤ 60 MB、空闲 CPU < 0.1% 仍是手工项（见 `docs/verification/final-report.md` 的 55 项遗留）。
7. **MSIX 在本机未能安装**：见 §2 的证书链限制。包本身有效，需在受信任环境或管理员下安装。

## 6. 隐私

无网络请求、无账号、无统计、无更新器、无崩溃上报。诊断日志与状态文件都只写 `%LOCALAPPDATA%\ClipLine\`。`ShellExecuteW` 只用于「用系统默认应用打开用户自己的图片 / 打开文件夹」。

## 7. 最终交付校验记录（2026-11-05）

按顺序实跑，全部针对**同一份 16/16 冒烟通过的源码**：

1. `bash tools/verify.sh` → `TOTAL 128 PASS 128 FAIL 0`；产品与渲染壳编译 `0 个警告 0 个错误`。
2. `powershell -File tools\smoke.ps1` → `VERDICT: PASS (16/16)`，`ExitCode=0`；渲染帧导出 `frame.png (15747 B)`。
3. `powershell -File tools\build-msix.ps1` → `SIZE=28.97 MB`、`SHA256=83D1D36C…`；`PORTABLE_SIZE=28.29 MB`、`PORTABLE_SHA256=4E9731ED…`；`[pack] makeappx + signtool sign OK`。
4. 逐文件 SHA-256 比对便携包与 Release 输出 → 6/6 `OK`。
5. 启动 `dist\ClipLine_1.0.0.0_x64_portable\ClipLine.exe`（PID 6492），`powershell -File tools\e2e-verify.ps1` → `CLIP_OK=True FOLDER_OK=True FRAME_OK=True`、`VERDICT=PASS`；`diagnostics.log` 记录 `watchers=2`、`clipboard=True`、`clipboard -> NewItem`、`folder -> NewItem`、`window: present=True revealed=True ... cards=10`。
6. 复制 `%TEMP%\clipline-smoke\frame.png` 为 `dist\ClipLine_1.0.0.0_render-frame.png`。
7. 最终 MSIX 结构校验：归档 107 个条目，`AppxManifest.xml` / `AppxSignature.p7x` / `ClipLine.exe` / `ClipLine.Shell.dll` / `Assets/Square44x44Logo.png` 全部 `PRESENT`。
8. 对**最终包**再跑 `Add-AppxPackage`：仍返回 `0x800B0109 CERT_E_UNTRUSTEDROOT`。本机条件实测 `IS_ADMIN=False`、开发者模式与旁加载策略均未开启，因此这是部署信任策略限制，而非包损坏；便携包作为本机可运行交付物已跑通（第 5 步）。

本次最终运行 `diagnostics.log` 中 `hotkey=False`（全局热键 `Ctrl+Alt+T` 在本机未注册成功），已按 §5.5 记录；托盘左键与菜单不受影响。
