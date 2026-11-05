# ClipLine 打包与分发（v1.0.0 已放行）

> 状态：**已构建、已签名**。完整发布记录见 `docs/09-release-v1.md`。本目录只保留清单与图标规则。

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-msix.ps1
```

一次产出：

- `dist/ClipLine_1.0.0.0_x64.msix`：用 `CN=ClipLine` 自签名证书签名的正式 MSIX，28.97 MB，`SHA256=83D1D36CA6DC0B245E913D5FA962EC7028D477C188DB427355C564B5129EFCC9`。
- `dist/ClipLine_1.0.0.0_x64_portable.zip`：同 payload、免签名的便携包，28.29 MB，`SHA256=4E9731ED46880DADD10CAF5086DEEA31B22DAE475473B2C849992EC8BF170595`。
- `dist/ClipLine.cer` / `ClipLine.pfx`：本地开发签名证书。
- `dist/ClipLine_1.0.0.0_render-frame.png`：应用内导出的确定性渲染证据（真实缩略图）。

构建脚本零删除、零联网；stage 只复制需要的 payload（保留 `zh-CN` / `en-US`，排除其余 `.mui` 与全部 `.pdb`），最终 103 个文件。`dist/` 不入 git，哈希为 2026-11-05 最终构建实测值，重建会因签名时间戳变化。

## 定稿口径与 v1 实况

| 项 | 定稿 | v1 实况 |
|---|---|---|
| 分发形态 | MSIX（`runFullTrust`）+ 便携 zip 双轨 | 一致 |
| Windows App SDK | 框架依赖 | **self-contained**（本机缺 Main/Singleton/DDLM 包） |
| 安装包预算 | ≤ 15 MB | **28.97 MB**，超预算，理由见 `docs/09` §5.1 |
| 目标系统 | Windows 10 19041+ / Windows 11 x64 | 一致（`MinVersion 10.0.19041.0`，`MaxVersionTested 10.0.26100.0`） |
| 开机自启 | MSIX `windows.startupTask` | 一致，由系统设置管理 |
| 右键扩展 | 稀疏包 + `IExplorerCommand` | **v1 未实现**，manifest 中不声明无效 CLSID |
| 签名 | Authenticode 代码签名 | 本地 `CN=ClipLine` 自签名 + `signtool /fd SHA256`；生产发布需换成受信 CA 证书 |
| 网络 | 零出站 | 一致 |

## 文件

- `AppxManifest.xml`：正式清单。`Identity Publisher="CN=ClipLine"` 必须与证书 Subject 完全一致；`Executable="ClipLine.exe"`；`EntryPoint="Windows.FullTrustApplication"`。
- `icon-manifest.json`：E「双线」定稿资产到 MSIX 图标的映射与生成规则。
- `Assets/*.png`：由 `tools/build-msix.ps1` 用系统 `System.Drawing` 现场栅格化（50 / 44 / 71 / 150 px），不依赖 SVG 转换工具。

## 安装 MSIX

```powershell
# 管理员执行一次，让签名链被部署服务信任
certutil -f -addstore "TrustedPeople" dist\ClipLine.cer
Add-AppxPackage -Path dist\ClipLine_1.0.0.0_x64.msix
```

非管理员环境请用 `tools/trust-cert.ps1` 查看手动导入步骤，或直接使用便携 zip。本机因标准用户 + 开发者模式未开启，`Add-AppxPackage` 返回 `0x800B0109`，详见 `docs/09` §2。

## 放行门槛状态

| 门槛 | v1 状态 |
|---|---|
| `verify.sh` 全绿 | ✅ 128/128，编译 0 警告 0 错误 |
| 真实桌面冒烟 | ✅ 16/16（含渲染帧 PNG 导出） |
| 真机 e2e（剪贴板 + 目录 + 缩略图） | ✅ `CLIP_OK=True FOLDER_OK=True FRAME_OK=True` / `VERDICT=PASS`；`dist/ClipLine_1.0.0.0_render-frame.png` 为渲染证据，`e2e-desktop.png` 仅作环境记录 |
| 包内 payload 与最新编译一致 | ✅ 6/6 SHA-256 逐字节一致 |
| 零出站 / 隐私扫描 | ✅ 静态扫描零联网 API |
| 包 ≤ 15 MB / 内存 ≤ 60 MB / 空闲 CPU < 0.1% | ❌ 包超预算；内存与 CPU 未实测 |
| 100/125/150/200% 缩放、深浅色、高对比度、减少动效矩阵 | ❌ 手工矩阵未跑完 |
| 生产受信 CA 签名 + 时间戳 | ❌ 当前为本地自签名 |
