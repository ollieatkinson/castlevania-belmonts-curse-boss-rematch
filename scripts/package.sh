#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
: "${GamePath:?Set GamePath to a BepInEx-enabled game installation.}"
dotnet_command="${DOTNET_COMMAND:-${DOTNET:-dotnet}}"
if [[ "$dotnet_command" == dotnet ]] && ! command -v dotnet >/dev/null 2>&1 && [[ -x .build/dotnet/dotnet ]]; then
  dotnet_command="$PWD/.build/dotnet/dotnet"
fi
"$dotnet_command" msbuild -restore -t:Package -p:Configuration=Release -p:GamePath="$GamePath" -nologo
