# ClipLine v1 · P6 最终验证报告

> 生成时间：2026-11-04 · 验证命令：`bash tools/verify.sh --report docs/verification/final-report.csv`
> 功能基线：`docs/01-feature-alignment.md`（137 个验收编号）· 规格来源：Issue #1

## 1. 结论（先看这里）

| 项 | 结果 |
|---|---|
| 编译 | **0 警告 0 错误**（4 个项目：Domain / Application / Platform / Tests） |
| 自动化测试 | **TOTAL 128 · PASS 128 · FAIL 0**，退出码 0 |
| 功能编号自动化覆盖 | **82 / 137（59.9%）**，见 `traceability.md` |
| 隐私静态检查 | **PASS**：`src/**/*.cs` 零联网 API、零 URL 常量、无遥测 |
| 唯一测试接缝 | **PASS**：`AppFacade` 一个门面，Application 层只有一个命令接收器实现 |
| 放行判定 | **逻辑 / 状态 / 语义层通过；渲染与实机层未放行** |

> **不谎报完成**：本机没有 WinUI 3 / Windows App SDK 工作负载、没有 Visual Studio、没有签名证书，
> 因此 55 个编号（真实窗口绘制、Acrylic/弹簧、OLE 拖放、MediaFoundation、实机 DPI/主题/虚拟桌面矩阵等）
> 只能标注「需实机 / 渲染层」，**不能算作已验收**。逐项边界见 `docs/06` §5。

## 2. 六阶段提交

| 阶段 | 工单 | 提交 | 用例增量 |
|---|---|---|---|
| P0 | #2 | （切片留档 `spikes/`） | 20 项 Win32 自检 |
| P1 | #3 | `648869b` | 46 |
| P2 | #4 | `c2cb488` | +23 → 69 |
| P3 | #5 | `33975c0` | +20 → 89 |
| P4 | #6 | `81687cd` | +15 → 104 |
| P5 | #7 | `bd91b0a` | +15 → 119 |
| P6 | #8 | 本阶段提交 | +9 → **128** |

## 3. 自动化覆盖分布

- **已覆盖（82 项）**：容量与排序、固定 / 撤销 / 持久化、状态机全部时序、托盘 16 项菜单模型、
  三层捕获准入与完整性字节判定、剪贴板过滤、手势时序、右键条件、拖放三语义、
  键盘借用与热键、快速查看 / 标注 / 修剪 / 拿在手上 / 右键扩展的规则、
  11 语言 key 校验、主题令牌、隐私与交付完整性。
- **未覆盖（55 项，分类）**：
  1. **渲染层**（绳三层描边、玻璃框、夹子、绳结、飞入/下落/微风的实际绘制、120 fps）：需要 Win2D + Composition。
  2. **真实系统能力**（OLE 拖放、MSIX 稀疏包 `IExplorerCommand`、WIC/HEIC 解码、MediaFoundation 首帧与修剪、
     系统音色、虚拟桌面与全屏检测、开机自启注册表）。
  3. **实机矩阵**（100/125/150/200% 缩放、深浅色、高对比度、任务栏置顶、全屏应用、贴靠布局、抓包零出站）。
  4. **平台物理差异**（`N-01` 力感应点按、`N-03` 改系统截图位置、`N-11` 多屏主动裁剪）。

## 4. 隐私与性能

- `tools/check-privacy.sh`：排除 SDK 自动生成的 `obj/` 后，`src/**/*.cs` 不含 `HttpClient` / `WebRequest` /
  `Socket` / 遥测类型 / 任何 `http(s)://` 常量。「检查更新」只发 `OpenBrowser` 命令，应用自身不联网（P-05）。
- 性能预算常量已固化并单测：安装 ≤ 15 MB、常驻 ≤ 60 MB、空闲 CPU < 0.1%、截图上线 < 100 ms、
  缩略图 `320 × scale`、最多 2 个并发飞行、剪贴板 64 MB / 8000 万像素、完整性复查 600 s 放弃。
  **这些是预算与代码纪律，不是实测数据**；实测需在真机跑 `packaging/README.md` 的放行门槛。

## 5. 复现

```bash
# 系统已装 .NET 8 SDK
bash tools/verify.sh --report docs/verification/final-report.csv
bash tools/check-privacy.sh
node tools/traceability.mjs docs/verification/final-report.csv

# 仅本机临时 SDK
DOTNET_SDK="/c/Users/.../Temp/clipline-tools/dotnet/dotnet.exe" bash tools/verify.sh
```

## 6. 下一步（真正放行前）

1. 安装 .NET 8 SDK + Windows App SDK / WinUI 3 工作负载，按 `02-tech-stack.md` 建 `Rendering` / `App` / `ShellExtension` 三个项目，把 `AppCommand` 接到 `PlatformCommandSink` 与 CsWin32 生成代码。
2. 用 `assets/ui/prototype.html` + `docs/05-ui-design.md` 作唯一视觉基准实现 Win2D 绘制，逐项补 55 个实机编号。
3. 补齐 9 种语言的母语校对（`docs/07-localization.md`）、生成 `.ico` / MSIX 图标并核对 Win11 圆角遮罩。
4. 跑单屏矩阵与抓包，全部通过后签名打包。
