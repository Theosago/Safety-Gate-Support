#!/usr/bin/env bash
# Source this file to activate the local SDK and writable cloud caches.
SAFETYGATE_WORKSPACE="${SAFETYGATE_WORKSPACE:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)}"
export DOTNET_ROOT="${DOTNET_ROOT:-$SAFETYGATE_WORKSPACE/.tools/dotnet}"
if [[ -x "$DOTNET_ROOT/dotnet" ]]; then
    export PATH="$DOTNET_ROOT:$PATH"
fi
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$SAFETYGATE_WORKSPACE/.cache/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$SAFETYGATE_WORKSPACE/.cache/nuget}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-$SAFETYGATE_WORKSPACE/.cache/xdg}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$XDG_DATA_HOME"
