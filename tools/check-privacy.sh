#!/usr/bin/env bash
# L-03 / P-04：静态检查纯 C# 源码里没有联网能力。
# 运行期零出站还需抓包复核（见 packaging/README.md 放行门槛）。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PATTERN='HttpClient|WebRequest|WebClient|Socket\(|TelemetryClient|ApplicationInsights|HttpClientFactory|https?://|Sockets\.|System\.Net\.Http|System\.Net\.Sockets'

echo "扫描 $ROOT/src 下的 C# 源码…"
if grep -R -n -E "$PATTERN" "$ROOT/src" --include='*.cs' \
  --exclude-dir=obj --exclude-dir=bin > /tmp/clipline-privacy-hits.txt 2>/dev/null; then
  echo "FAIL：发现疑似联网依赖："
  cat /tmp/clipline-privacy-hits.txt
  exit 1
fi

echo "PASS：源码零联网 API / 零 URL 常量"
echo "PASS：无遥测、无崩溃上报、无自动更新探测"
