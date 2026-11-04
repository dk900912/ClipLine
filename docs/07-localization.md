# ClipLine · 本地化与文案

> P-03：对齐 v1.8.1 的 11 种语言，源码以**英文原文作 key**（对应 `L("...")`），并有等价的 key 齐全校验。

## 语言清单

`en / es / gl / fr / pt-BR / ja / ko / zh-Hans / tr / az / hi`，默认 `zh-Hans`。

| 语言 | 状态 |
|---|---|
| `en` | 源语言，key 即英文原文 |
| `zh-Hans` | 全量中文翻译，采用 `01-feature-alignment.md` 中的译法 |
| 其余 9 种 | **key 全部齐全，值暂回落英文**，待母语校对后替换 |

> 这是一处明确的未完工项，不冒充「11 语言全部译完」。`Localization.ValidateAll()` 等价于原项目的 `check-strings.swift`：任何语言缺 key 即返回 false，测试 `P5 / P-03` 会拦住。正式版把 `.resw` 接进 WinUI 3 时，仍以同一份 key 集合为准。

## 符号映射（N-10 / K-06）

菜单里 macOS 的 `⌘ → Ctrl`、`⌥ → Alt`、`⌃ → Ctrl`，由 `Localization.DisplayShortcut` 统一转换；默认全局快捷键 `⌃⌥T` 显示为 `Ctrl+Alt+T`，退出快捷键显示为 `Ctrl+Q`。

## 关键文案

| Key | 简体中文 |
|---|---|
| `Screenshot hangs here` | 截个图，它就会挂在这里 |
| `Pause and it will hang here` | 截图即刻挂上，不再经过桌面 |
| `Copied` | 已复制 |
| `Back on the Line` | 放回绳上 |
| `Empty Line` | 晾衣绳上是空的 |
| `N Items on Line` | 晾衣绳上有 N 张 |
| `Quit ClipLine` | 退出 ClipLine |

所有面向用户的文案一律为 **ClipLine** 品牌，不出现原项目名称（L-02；来源说明与 MIT 授权文件除外）。

## 无障碍（P-02）

`ThemeTokens.AccessibilityLabels` 固定四个 UIA 标签：托盘图标、卡片 image 角色、角标 button 角色、放回按钮「放回绳上」。`docs/05` §10 给出了高对比度与减少动态效果的视觉规则。
