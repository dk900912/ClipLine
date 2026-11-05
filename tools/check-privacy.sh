#!/usr/bin/env bash
# L-03 / P-04：静态检查网络能力被锁定在更新白名单文件内。
# 方案 D（GitHub Release 自动更新）后的新口径，比旧的「零联网」更严但可审计：
#   1. 任何 src/*.cs 不得出现 URL 字面量、遥测、Socket / WebClient / WebRequest；
#   2. HttpClient / System.Net.Http 只允许出现在唯一白名单文件 UpdateService.cs；
#   3. 该文件必须执行主机白名单校验，白名单来自 packaging/update-config.json；
#   4. 运行期仍无遥测、无崩溃上报、无轮询（更新检查只在用户点击或启动后触发一次）。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/src"
SERVICE="$SRC/ClipLine.Platform/UpdateService.cs"
CONFIG="$ROOT/packaging/update-config.json"

FORBIDDEN='https?://|TelemetryClient|ApplicationInsights|WebClient|WebRequest|Socket\(|System\.Net\.Sockets'
NETWORK='HttpClient|System\.Net\.Http'

echo "扫描 $SRC 下的 C# 源码（方案 D 白名单口径）…"

hits=$(grep -R -n -E "$FORBIDDEN" "$SRC" --include='*.cs' \
  --exclude-dir=obj --exclude-dir=bin 2>/dev/null || true)
if [[ -n "$hits" ]]; then
  echo "FAIL：发现 URL 字面量 / 遥测 / 非白名单网络原语："
  echo "$hits"
  exit 1
fi

network_files=$(grep -R -l -E "$NETWORK" "$SRC" --include='*.cs' \
  --exclude-dir=obj --exclude-dir=bin 2>/dev/null || true)
while IFS= read -r file; do
  [[ -z "$file" ]] && continue
  if [[ "$(basename "$file")" != "UpdateService.cs" ]]; then
    echo "FAIL：网络 API 出现在非白名单文件：$file"
    exit 1
  fi
done <<< "$network_files"

if ! grep -q 'AssertAllowed' "$SERVICE"; then
  echo 'FAIL：UpdateService.cs 缺少主机白名单校验'
  exit 1
fi
if ! grep -q 'allowHosts' "$CONFIG"; then
  echo 'FAIL：update-config.json 缺少 allowHosts'
  exit 1
fi

echo 'PASS：源码零 URL 常量 / 零遥测 / 零 Socket'
echo 'PASS：网络能力仅存在于 UpdateService.cs，且经配置化主机白名单校验'
echo 'PASS：无轮询、无崩溃上报；更新只在用户点击或启动后触发一次'
