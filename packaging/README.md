# ClipLine 打包与分发（v1.5 / 1.0.1 已构建）

> 状态：**已构建、已签名**。完整发布记录见 `docs/09-release-v1.md`。本目录只保留清单与图标规则。

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-msix.ps1
```

一次产出：

- `dist/ClipLine_1.0.1.0_x64.msix`：用 `CN=ClipLine` 自签名证书签名的正式 MSIX（Identity 版本 `1.0.1.0`），29.03 MB，`SHA256=610A28491B333D66A93C1B66B3E67AB5D92FB6F445E66B28C9C319A1A88692BA`。
- `dist/ClipLine_1.0.1.0_x64_portable.zip`：同 payload、免签名的便携包，28.35 MB，`SHA256=5197CAA4EDA43ADA163DCE963D26139C348E8867B22856BD963A3818BC28C15B`。
- `dist/*.sha256` / `dist/release-assets.txt`：GitHub Release 资产清单与逐文件校验和，客户端校验不通过即丢弃下载。
- `dist/ClipLine.cer` / `ClipLine.pfx`：本地开发签名证书。
- `dist/ClipLine_1.0.0.0_render-frame.png`：应用内导出的确定性渲染证据（真实缩略图）。
- `dist/ClipLine_1.0.0.0_copied-badge.png`：左键单击后卡片「已复制」角标的渲染证据。
- `dist/ClipLine_1.0.0.0_notice.png` / `_swing-aa.png`：自绘通知卡与摆动抗锯齿的渲染证据。
- `dist/ui-evidence/`：真实鼠标点击端到端验证的 11 张证据图（四个替身窗口、三张整屏截图、V-09 绳结局部与 6× 放大、固定前后绳帧），由 `tools/ui-click-verify.ps1` 生成。
- `dist/menu-audit-report.txt`：两个场景 45 项菜单功能逐项审计结果，由 `tools/menu-audit.ps1` 生成。

构建脚本零删除、零联网；stage 只复制需要的 payload（保留 `zh-CN` / `en-US`，排除其余 `.mui` 与全部 `.pdb`），最终 105 个文件（含 `update-config.json` / `update-helper.ps1`）。版本号由 `src/ClipLine.Domain/BuildInfo.cs` 单点派生，脚本据此注入 MSIX Identity 版本、生成资产名与 `.sha256`。`dist/` 不入 git，哈希为 2026-11-05 最终构建实测值，重建会因签名时间戳变化。

注意 `AppxManifest.xml` 含中文：Windows PowerShell 5.1 下必须用显式 UTF-8 读写，否则 `makeappx` 报 `C00CEE3B`。

## 定稿口径与 v1 实况

| 项 | 定稿 | v1 实况 |
|---|---|---|
| 分发形态 | MSIX（`runFullTrust`）+ 便携 zip 双轨 | 一致 |
| Windows App SDK | 框架依赖 | **self-contained**（本机缺 Main/Singleton/DDLM 包） |
| 安装包预算 | ≤ 15 MB | **29 MB**，超预算，理由见 `docs/09` §5.1 |
| 目标系统 | Windows 10 19041+ / Windows 11 x64 | 一致（`MinVersion 10.0.19041.0`，`MaxVersionTested 10.0.26100.0`） |
| 开机自启 | MSIX `windows.startupTask` | MSIX 由系统设置管理；便携版在用户勾选时写 `HKCU\...\Run\ClipLine`，取消即删 |
| 右键扩展 | 稀疏包 + `IExplorerCommand` | **v1 未实现**，manifest 中不声明无效 CLSID |
| 签名 | Authenticode 代码签名 | 本地 `CN=ClipLine` 自签名 + `signtool /fd SHA256`；生产发布需换成受信 CA 证书 |
| 网络 | 零出站 | **v1.5 改口**：唯一白名单出站 = GitHub Release 检查/下载；源码零 URL 常量、零遥测，`HttpClient` 仅限 `UpdateService.cs`，详见 `docs/09` §6 / §13 |

## 文件

- `AppxManifest.xml`：正式清单。`Identity Publisher="CN=ClipLine"` 必须与证书 Subject 完全一致；`Executable="ClipLine.exe"`；`EntryPoint="Windows.FullTrustApplication"`。
- `update-config.json`：自动更新的 API 基址、仓库与主机白名单；随 payload 分发，保证源码零 URL 常量。
- `update-helper.ps1`：一次性更新 helper（等待主进程退出 → 备份 / 替换 / 注册 → 重启 / 回滚），由应用以 `-EncodedCommand` 启动，不常驻。
- `icon-manifest.json`：E「双线」定稿资产到 MSIX 图标的映射与生成规则。
- `Assets/*.png`：由 `tools/build-msix.ps1` 用系统 `System.Drawing` 现场栅格化（50 / 44 / 71 / 150 px），不依赖 SVG 转换工具。

## 安装 MSIX

```powershell
# 管理员执行一次，让签名链被部署服务信任
certutil -f -addstore "TrustedPeople" dist\ClipLine.cer
Add-AppxPackage -Path dist\ClipLine_1.0.1.0_x64.msix
```

非管理员环境请用 `tools/trust-cert.ps1` 查看手动导入步骤，或直接使用便携 zip。本机 `IsAdmin=False`，机器级证书写入返回 `ERROR_ACCESS_DENIED`，签名包直装返回 `0x800B0109`；**开发者模式已开启**，`Add-AppxPackage -Register <解包目录>` 可成功登记，MSIX 自动更新通道已用该路径端到端跑通（见 `docs/09` §13.4）。生产环境仍需受信 CA 证书。

## 放行门槛状态

| 门槛 | v1 状态 |
|---|---|
| `verify.sh` 全绿 | ✅ 149/149（v1.5 新增 13 项 `U-01…U-13` 更新状态机用例），编译 0 警告 0 错误 |
| 真实桌面冒烟 | ✅ 34/34（v1.4 新增：通知卡右下角、四个替身窗口独立且尺寸互异、V-09 绳结与局部帧、固定绳段色序 / 亚像素连续性像素采样） |
| 真实鼠标点击端到端验证 | ✅ `tools/ui-click-verify.ps1` 25/25（`--ui-click` 注入系统输入队列，非直接调门面；证据见 `dist/ui-evidence/`） |
| 菜单全量审计（托盘 + 卡片右键） | ✅ `tools/menu-audit.ps1` 45 项：`OK=42 / DEGRADED=3 / SKIP=0 / FAIL=0`，含真实桌面写入与回收站清理 |
| 自动更新端到端（便携 ZIP） | ✅ `tools/update-e2e.ps1`：`MARKER_REPLACED=True / HELPER_DONE=True / APP_RESTARTED=True`，helper.log 有 `BACKUP_OK → REPLACE_OK → DONE` |
| 自动更新端到端（MSIX） | ✅ `tools/update-e2e-msix.ps1`：`MSIX_DEV_REGISTER_OK / PACKAGE_VERSION=1.0.1.0 / APP_RESTARTED=True` |
| 真机 e2e（剪贴板 + 目录新增/删除 + 缩略图） | ✅ `CLIP_OK=True FOLDER_OK=True DELETE_OK=True FRAME_OK=True` / `VERDICT=PASS`；`dist/ClipLine_1.0.0.0_render-frame.png` 与 `ClipLine_1.0.0.0_copied-badge.png` 为渲染证据，`e2e-desktop.png` 仅作环境记录 |
| 包内 payload 与最新编译一致 | ✅ 6/6 SHA-256 逐字节一致 |
| 隐私扫描（v1.5 白名单口径） | ✅ 源码零 URL 字面量 / 零遥测 / 零 Socket；网络 API 仅限 `UpdateService.cs` 且经配置化主机白名单校验；零轮询 |
| 包 ≤ 15 MB / 内存 ≤ 60 MB / 空闲 CPU < 0.1% | ❌ 包超预算；内存与 CPU 未实测 |
| 100/125/150/200% 缩放、深浅色、高对比度、减少动效矩阵 | ❌ 手工矩阵未跑完 |
| 生产受信 CA 签名 + 时间戳 | ❌ 当前为本地自签名 |
| 托盘「检查更新…」 | ✅ v1.5 已修成真实自动更新链路（`RunUpdateCheck` → 下载 → SHA-256 → 安装 → 重启），详见 `docs/09` §13 |
| GitHub Release 正式发布 | ⏸ 资产与校验和已生成（`dist/release-assets.txt`）；受「不要 commit 和提交」约束，正式公开发布待用户放行 |
| 运行期出站抓包复核 | ❌ 静态白名单门禁已过，抓包复核待做 |
