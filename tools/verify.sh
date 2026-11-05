#!/usr/bin/env bash
# ClipLine 全量验证：编译产品代码 + 跑唯一接缝的测试套件。
# 用法：bash tools/verify.sh [--report <csv路径>]
# 若本机未系统安装 .NET SDK，可用 DOTNET_SDK 指向临时解压的 dotnet.exe。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EXE="$ROOT/tests/ClipLine.Tests/bin/Release/net8.0/ClipLine.Tests.exe"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# 本机只有 .NET 8 运行时、无系统级 SDK。优先用显式 DOTNET_SDK，
# 其次是真正带 SDK 的 dotnet，最后回退到临时解压的 .NET 8 SDK。
SDK="${DOTNET_SDK:-}"
if [[ -z "$SDK" ]]; then
  if command -v dotnet >/dev/null 2>&1 && [[ -n "$(dotnet --list-sdks 2>/dev/null)" ]]; then
    SDK="dotnet"
  else
    for candidate in \
      "${TEMP:-}/clipline-tools/dotnet/dotnet.exe" \
      "${LOCALAPPDATA:-}/Temp/clipline-tools/dotnet/dotnet.exe" \
      "$ROOT/.tools/dotnet/dotnet.exe"; do
      if [[ -f "$candidate" ]]; then SDK="$candidate"; break; fi
    done
  fi
fi
if [[ -z "$SDK" ]]; then
  echo "ERROR: no .NET SDK found. Set DOTNET_SDK=/path/to/dotnet.exe" >&2
  exit 1
fi
echo "SDK=$SDK"

"$SDK" build "$ROOT/tests/ClipLine.Tests/ClipLine.Tests.csproj" -c Release --nologo -v minimal
# 渲染壳（WinUI 3 / WindowsAppSDK self-contained）同样纳入编译门禁；
# 真实桌面会话的 --smoke 由 tools/smoke.ps1 单独执行，不放进无桌面的 CI。
"$SDK" build "$ROOT/src/ClipLine.App/ClipLine.App.csproj" -c Release --nologo -v minimal
echo
"$EXE" "$@"
