#!/usr/bin/env bash
# ClipLine 全量验证：编译产品代码 + 跑唯一接缝的测试套件。
# 用法：bash tools/verify.sh [--report <csv路径>]
# 若本机未系统安装 .NET SDK，可用 DOTNET_SDK 指向临时解压的 dotnet.exe。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SDK="${DOTNET_SDK:-dotnet}"
EXE="$ROOT/tests/ClipLine.Tests/bin/Release/net8.0/ClipLine.Tests.exe"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

"$SDK" build "$ROOT/tests/ClipLine.Tests/ClipLine.Tests.csproj" -c Release --nologo -v minimal
echo
"$EXE" "$@"
