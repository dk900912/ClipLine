# ClipLine 打包与分发（清单草稿）

> 状态：**草稿**。本机没有 WinUI 3 / Windows App SDK 工作负载与签名证书，尚不能生成可安装的 MSIX；本目录先把定稿的清单、图标规则与放行门槛固化下来。

## 定稿口径

| 项 | 值 |
|---|---|
| 分发形态 | MSIX 框架依赖包（`runFullTrust`）+ 便携 zip 双轨 |
| 安装包预算 | ≤ 15 MB（Windows App SDK 运行时外置并作为前置依赖声明） |
| 目标系统 | Windows 11 22H2+（`MinVersion 10.0.22621.0`），x64 / ARM64 |
| 开机自启 | MSIX `windows.startupTask`，由系统设置管理，用户可关（A-03） |
| 右键扩展 | 稀疏包 + `IExplorerCommand`（N-06 / X-01） |
| 签名 | Authenticode 代码签名，避免 SmartScreen 首次运行拦截 |
| 网络 | **零出站**：不打包更新器、崩溃上报、遥测；「检查更新」只拼 URL 交给浏览器（P-05 / L-03） |

## 文件

- `AppxManifest.xml`：包清单骨架（身份、视觉元素、startupTask、右键扩展声明）。`Clsid` 占位，接入真实 Shell 扩展时替换。
- `icon-manifest.json`：E「双线」定稿资产到 MSIX 图标的映射与生成规则，含 16/20 px 独立稿与圆角遮罩验收提醒。

## 放行门槛

1. `bash tools/verify.sh` 全绿，`docs/verification/` 的追溯报告无 FAIL。
2. `01-feature-alignment.md` 全表复选框勾完；单屏 100/125/150/200% 缩放、深浅色、高对比度、减少动效矩阵无 P0/P1 缺陷。
3. 空绳时三态托盘图标在深浅任务栏下可辨（不达标切 B 回退）。
4. 抓包验证零出站连接；`tools/check-privacy.sh` 静态扫描无联网 API。
5. 安装包 ≤ 15 MB、常驻内存 ≤ 60 MB、空闲 CPU < 0.1%、截图上线 < 100 ms。
6. MSIX 卸载后 `%LOCALAPPDATA%\ClipLine` 之外的系统设置全部还原（A-04 / A-09）。

## 未完成（需正式工具链）

Windows App SDK 引用、Win2D/Composition 渲染程序集、`.resw` 11 语言资源编译、稀疏包签名与 `IExplorerCommand` COM 注册、Authenticode 证书与时间戳。
