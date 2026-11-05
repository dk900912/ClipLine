# Tendedero v1.8.1 动效调研与 ClipLine 对齐

> 状态：2026-11-05 调研完成，实测源码一手证据；本文是 v1.1 动效实现的唯一依据。
> 调研对象：`alejandrobujan/tendedero` tag **v1.8.1**，commit `6df2cfe6455b39c0356c606992361b08ae6b8941`。
> 仓库 `main` 分支的 `PeggedView.swift` / `LineView.swift` / `Line.swift` / `CaptureFlight.swift`
> 与该 tag **逐字节一致**（`AppDelegate.swift` 仅差 3 处 Share 相关逻辑，与动效无关），
> 因此下文参数对 v1.8.1 与最新源码同时成立。

代码授权为 MIT，但名称与图标未授权；本文只引用运动参数与实现思路，不复制任何素材。

---

## 1. 春风：不是持续吹，是随机阵风（gust）

这是「绳子像实物而不是控件」的核心细节，**不是常驻循环动画，也不是每张卡各自匀速正弦摆动**。

| 环节 | 源码 | 精确参数 |
| --- | --- | --- |
| 阵风调度 | `Line.swift:553-558 scheduleGust()` | 应用启动时递归排程，每 `Double.random(in: 7...16)` 秒一次 |
| 触发条件 | `Line.swift:556` | `!items.isEmpty && draggingID == nil` 才 `gust += 1`；绳上无图、或正在拖拽时不吹 |
| 广播 | `Line.swift:39` / `PeggedView.swift` | `@Published var gust`，每张卡 `.onChange(of: line.gust) { breeze() }` |
| 逐卡错峰 | `PeggedView.swift breeze()` | 每张卡**各自**随机延迟 `0...0.35 s`，再 `nudge(Double.random(in: 1.6...3.4))` 度 |
| 摆出去 | `PeggedView.swift nudge(_:)` | `withAnimation(.easeOut(duration: 0.3)) { swing = degrees }` |
| 摆回来 | 同上，0.3 s 后 | `.interpolatingSpring(stiffness: 38, damping: 2.4)` 回到 0（欠阻尼，会过冲来回几次） |
| 旋转支点 | `PeggedView.swift:35` | `.rotationEffect(.degrees(swing + item.tilt), anchor: .top)` —— 绕**夹子所在顶边中点** |
| 静态倾角 | `Line.swift:14` | `let tilt = Double.random(in: -2.5...2.5)`，每张卡天生微歪，静止时不归零 |

因为每张卡的延迟与角度都独立随机，视觉是「风从一头扫过去，卡片先后轻摆、各自衰减」，
而不是整排整齐晃动。绳子本体（`LineView.swift:213+ Rope`）是**完全静止**的抛物线，不参与摆动。

## 2. 挂上一张（arrive / land）

`PeggedView.swift arrive()`：

- 起始 `swing = 16` 度，卡片 `.offset(y: arrived ? 0 : -46)` 从屏幕上方 46 pt 处落下；
- 落下用 `.spring(response: 0.42, dampingFraction: 0.72)`，摆动用
  `.interpolatingSpring(stiffness: 46, damping: 2.6)` 从 16° 回 0；
- 同时 `opacity` 从 0 淡入；`.animation(.easeOut(duration: 0.16), value: item.flying)`。

若这张图是**截图飞入**（`item.flying`），`arrive()` 直接置位不摆；飞行结束由
`.onChange(of: item.flying)` → `land()` → `nudge(2.2)` 补一个很轻的落地摆。

飞入本体 `CaptureFlight.swift`：`duration = 0.65 s`，`easeInOutCubic`，
抛物线弧高 `arc = 30 pt`，120 fps `Timer`，从截图区域缩小、转正、长出玻璃框与夹子，
落地时以 0.16 s 淡出交回真实卡片。`AppDelegate.swift:328` 限制并发 `flightsInProgress < 2`，
且系统开启「减少动态效果」时整个跳过。

**复制**另有独立反馈：`PeggedView.swift .onChange(of: copied)` → `nudge(3)`，
并弹出 `Label("Copied", systemImage: "checkmark")` 胶囊，
`transition(.opacity.combined(with: .offset(y: -4)))` + `.easeOut(duration: 0.2)`。

## 3. 单张取下

分两层配合，做法反直觉但很关键：

1. **绳上的卡片瞬间消失，不做动画。**
   `Line.swift:133 drop()` 先回调 `onFall?` 再置 `falling = true`；
   `PeggedView.swift` 用 `.opacity(item.falling ? 0 : ...)` 加
   `.transaction { t in if item.falling { t.animation = nil } }` 关掉事务动画。
   源码注释直说：*"the card here just steps aside at once"*。
2. **另起一个全屏窗口画掉落。**
   `CaptureFlight.swift:76 fall()` 建独立 `NSPanel`（全屏、`ignoresMouseEvents`、
   层级 `floating + 1`），所以卡片能掉出绳子那条窄 strip 而不被裁剪。

掉落曲线（`updateFall`，0.55 s、`e = raw³` 三次 ease-in）：

| 量 | 公式 |
| --- | --- |
| 下落距离 | `520 * e` pt |
| 倾角 | `angle = tilt + (tilt * 7 + 20) * e`（越掉越歪，终值 `tilt*8 + 20`） |
| 透明度 | `1 - e` |
| 驱动 | `Timer` 1/120 s |
| 固定卡 | 用深空灰夹子颜色跟随，银色卡则用银色 |

> **对齐修正**：本项目 `Layout.FallTilt` 原先把源码里的进度变量 `e` 误读成自然常数，
> 写成了 `Math.Pow(Math.E, 3)`，导致终态角度被放大约 20 倍。已按 `raw³` 终值 `e=1` 修正为
> `tilt * 8 + 20`，并同步更新 V-28 单测。

**空隙不立刻闭合**：`Line.swift:150 closeGaps()` 要求「最后一次 fall 之后 ≥ 0.6 s」且指针已离开绳区，
才 `sweepFallen()` 真正移除占位；其余卡片用 `.spring(response: 0.55, dampingFraction: 0.78)`
补位（`LineView.swift:125`）。所以连续点叉时，空位会先留着，最后一起合拢。

## 4. 全部取下

`Line.swift:163 clear()` → `takeDown(items.filter { !falling && !kept && !held })`
—— **固定（pinned）与「拿在手上」的不取**。

`Line.swift:183 takeDown(_:)` 是错峰连锁，不是一起掉：

```swift
let step = UUID()
for (n, item) in live.enumerated() {
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.06 * Double(n)) {
        self?.discard(item.id, quietly: n > 0, step: step)
    }
}
```

- 每张间隔 **60 ms** 依次 `discard` → 逐张触发自己的 `CaptureFlight.fall`，视觉上像被风一串带下去；
- 共用一个 `step` UUID 记作**一步撤销**，`Put back` 一次全挂回；
- 只有第一张响 `Pop` 音效（`quietly: n > 0`）。

清空后 `LineView.swift:85-89` 淡入 `Hint()` 胶囊「Take a screenshot and it will hang here」，
`.animation(.easeInOut(duration: 0.3), value: line.items.isEmpty)`。

## 5. 整绳显隐与空绳热区

- `LineView.swift:130-132`：整条绳 `.offset(y: revealed ? 0 : -(panelHeight + 12))` 停在屏幕上方，
  展开 `.spring(response: 0.42, dampingFraction: 0.82)`、收起 `.easeIn(duration: 0.22)`，
  像自动隐藏 Dock 从菜单栏下滑。
- `AppDelegate.swift:567-570`：热区唤出条件写死 `line.liveCount > 0`，注释明确
  *"An empty line stays up there: only the shortcut and the menu bring it down on purpose"*
  —— **mac 原版故意不让空绳被指针唤出**，只有快捷键 / 菜单能主动打开。
- 热区停留 0.25 s（`revealDelay`）；空绳被主动打开时显示上面的 `Hint()`。

## 6. 与 ClipLine v1 的差距及本次处理

| 动效 | v1 发布包现状 | 本次处理 |
| --- | --- | --- |
| 空绳顶部热区唤出 | 与 mac 一致，被 `_line.IsEmpty` 挡住 | **按用户要求放宽**（有意偏离 mac，见 §7） |
| 静态倾角 ±2.5° | `Pegged.Tilt` 有字段，渲染层未使用 | 接入 `RopeCanvas` |
| 春风阵风 / 逐卡错峰 | 完全没有；`RenderFrame` 按内容指纹去重，没有逐帧动画 | 新增 `RopeMotion` 纯函数模型 + 帧驱动 |
| 入场 16° 摆入 / 落地 2.2° / 复制 3° | 完全没有 | 同上 |
| 单张取下掉落 | 门面已发 `StartFall`，壳层未消费，卡片直接消失 | 新增全屏掉落层，按 `raw³` 曲线绘制 |
| 全部取下 60 ms 错峰 | 门面 `BatchStepMs = 60` 已排好，`StartFall` 未消费 | 由掉落层自然承接 |
| 整绳下滑 / 收起滑动 | `Hide` + `DestroyWindow` 直接销毁窗口 | **仍为已知偏差**，见 §7 |
| 飞入（截图从捕获区飞到绳上） | 门面发 `StartFlight`，壳层未消费 | **仍为已知偏差**，见 §7 |
| 减少动态效果 | 门面 `_env.ReduceMotion` 已有，渲染层未接 | 接入：开启时跳过摆动与掉落 |
| 摆动时边框锯齿 | 逐行整数位移 + 圆角二值覆盖，边框呈阶梯 | **本次修复**，见 §8 |

**实现验证（2026-11-05 本机实跑）**：

- `bash tools/verify.sh` → `136/136 PASS`。新增 `M-01…M-07` 七项摆动模型用例（阵风调度与收敛、入场摆入、nudge 欠阻尼过冲、空绳/拖拽不吹、减少动效、状态清理）；`V-28` 与 `S-03b/S-03c` 同步改写。
- `powershell -File tools\smoke.ps1` → 当轮 `26/26 PASS`，后续复制/通知修复后为 `29/29 PASS`，移除复制冗余通知后为 `30/30 PASS`，**v1.4（P4 替身窗口 + V-09 绳结）后为 `34/34 PASS`**。动效三项真实 HWND 断言（空绳热区唤出、入场摆入收敛、掉落层收起）；抗锯齿三项新增断言见 §8。注：跑冒烟前须确保没有其他 ClipLine 进程占着同类窗口，否则 `EnumWindows 无残留绳窗口` 会误报。冒烟的两套 host 各自使用独立状态文件且启动时清空，否则上一轮「固定」会让下一轮 V-09 断言假失败。
- 菜单审计 45 项 `FAIL=0`；四路 e2e `VERDICT=PASS`（最终针对 PID 20104 的重建便携包）；6/6 payload 逐字节一致；编译 0 警告 0 错误。
- 所有改动**未 `git commit`、未 `git push`**，沿用用户上一轮「不要 commit 和提交」的要求。

## 7. 明确记录的有意偏离

1. **空绳唤出**：用户明确要求「即使是空的绳子，鼠标到达屏幕顶部中间也应显示绳子」，
   与 mac `liveCount > 0` 相反。`AppFacade.OnHotZoneElapsed` 去掉空绳判断；
   指针停在热区时 `CanHideIgnoringPeek()` 会阻止收起，离开 0.5 s 由 `RetractElapsed` 自动收回。
2. **摆动几何**：v1 渲染是 32bpp GDI DIB 逐像素合成，没有 SwiftUI 的 layer 仿射变换。
   现在用 CPU 端「反向映射 + 双线性」实现绕夹子顶边的真旋转（不再是逐行位移近似），
   几何上等价于 `rotationEffect`，但仍是软件光栅化、没有 GPU 合成与透视/阴影的实时重算。
   升级 Win2D/DirectComposition 后可直接换成硬件变换矩阵。
3. **下滑 / 飞入 / 空隙弹簧补位**：仍属渲染壳阶段遗留项，本次不实现，
   见 `docs/08-renderer-shell.md` 与 `docs/09-release-v1.md` 的已知偏差清单。

## 8. 摆动边缘抗锯齿（2026-11-05 用户反馈「晃动的时候边框有明显的锯齿化」）

### 根因（两个叠加）

1. **整行整数位移**：旧 `RopeCanvas.Put()` 在卡片摆动时执行
   `x += (int)Math.Round(tan(θ) × (y − anchorY))`。每一行只有一个整数 x 偏移，
   tan(14°) ≈ 0.25 意味着每 4 行才跳 1 像素 —— 垂直边框变成可见的**台阶**。
   这是「只在晃动时出现」的主因。
2. **二值覆盖率**：`FillRoundRect` 与照片裁剪用 `dx² + dy² ≤ r²` 判断归属，
   像素非 0 即 255，圆角和照片边缘没有 1px 羽化；静止帧本来也有硬边，
   旋转后与台阶叠加更明显。

### 修复（全部在 GDI 路径内，不换渲染后端）

| 层 | 做法 |
| --- | --- |
| 卡片合成 | 先画进离屏 `uint[]` 缓冲（与主画布同为预乘 BGRA），再按摆角做**反向映射 + 双线性重采样**到主画布；源坐标带小数，边缘得到连续 0..255 覆盖率 |
| 几何 | 由「逐行水平位移近似」升级为绕夹子顶边支点的**真旋转矩阵**（R(a) 求包围盒、R(−a) 反推源坐标），顺带消除剪切形变 |
| 圆角 / 裁剪 | 二值判定换成解析式圆角矩形 **SDF**（`sd = length(max(q,0)) + min(max(q.x,q.y),0) − r`，覆盖率 `clamp(0.5 − sd, 0, 1)`），直边与圆角都有 1px 羽化 |
| 静止帧 | 摆角 < 0.0002 rad 时跳过插值，走 1:1 拷贝；卡片缓冲按最大尺寸复用，不逐帧分配 |
| 掉落层 | 与主画布共用同一条合成路径，翻落时的大倾角同样无锯齿 |

预乘 alpha 是双线性可以直接按通道加权的前提；`Over()` 统一做 src-over，
阴影、白底、照片、夹子、角标仍保持原有叠加顺序。「已复制」角标的 GDI 文字蒙版
也随卡片一起被重采样，文字边缘同样变平滑。

### 实测证据

- `dist/ClipLine_1.0.0.0_swing-aa.png`：14° 摆动帧（冒烟导出的真实渲染帧，26675 B），
  可直接放大查看边框与圆角。
- 冒烟新增三条确定性断言（假时钟 + 真实 HWND）：
  - 摆动边缘抗锯齿 → `半透明像素=3243 / alpha级数=209`（二值硬边只会有 0、255 两级）；
  - 静止帧圆角 1px 羽化 → `半透明像素=5606 / alpha级数=253`；
  - 30 帧真实 `Present`（含 `UpdateLayeredWindow`）→ `平均 4.44 ms/帧`，
    远低于 30 fps 的 33 ms 预算，抗锯齿没有拖垮帧驱动。
- 完整门禁：`136/136` 单测、`26/26` 冒烟、菜单审计 45 项 `FAIL=0`、四路 e2e `PASS`、
  隐私扫描 PASS、编译 0 警告 0 错误。

### 更进一步的路线（未实施）

Win2D / DirectComposition 的硬件仿射变换 + MSAA 是定稿方案，能同时拿到 GPU 抗锯齿、
真 layer 旋转与更低 CPU 占用；但 P0 结论是它与当前 GDI `WS_EX_LAYERED` 路径互斥，
需整体替换渲染后端。本次修复在不动架构的前提下把可见锯齿消掉，
升级后端时 `RopeMotion` 的摆动状态与 `RopeFrame` 契约可原样复用。

## 9. 固定（pinned）视觉标识：mac 依据与 Windows 增强（v1.4）

`LineView.swift` 的 V-09 只做两件事：有固定照片时，在**最后一张固定卡与下一张之间**画一个
7×5.5 pt 灰色椭圆绳结（两条 1 pt 松散线头），并让固定卡的夹子换成深空灰；
固定项的真实语义是「不参与自动淘汰、排在绳左侧、整绳收起/取下全部时保留」，
**没有任何动画**（§3、§4 的掉落逻辑也明确跳过 `kept` / `byHand` 项）。

ClipLine v1 在 125% DPI 单屏下实测这个绳结只有约 9×7 物理像素，肉眼极难察觉，
用户因此问「固定又是什么含义，有什么效果？」。v1.4 在保留 mac 绳结几何的前提下做了三处
**有意偏离**（`docs/05` §7 偏差记录、`docs/09` §12）：

1. 绳结左侧绳段染淡靛蓝，把「固定区 / 普通区」的分界扩成一条可扫描的长标识；
2. 固定卡加品牌靛蓝细描边 + 淡靛蓝底（mac 只有夹子颜色差异）；
3. 绳结用解析式 SDF 覆盖率画（与 §8 同一套 1px 羽化），小尺寸也有体积感而非硬边方块。

两个渲染层陷阱已用真实放大帧定位并加断言固化：染色必须与绳本体一样走**亚像素**
`StrokeRgb`（整数行会在 y 小数跨整处产生整像素厚度台阶）；且 DIB 是 BGRA，
`Stroke` / `PutCoverage` 的形参名虽叫 `r,g,b` 却要按 B/G/R 传，否则靛蓝变粉红。
冒烟 `采样=120 蓝色占优=120 最大相邻带宽跳变=11` 即为这两条的回归证据。
证据图见 `dist/ui-evidence/knot-zoom-6x-final.png`，真实点击验证见 `docs/09` §12.3。
