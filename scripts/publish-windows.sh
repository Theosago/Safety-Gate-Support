#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
source scripts/dotnet-env.sh
dotnet publish SafetyGateSupport/SafetyGateSupport.csproj \
    --configuration Release --runtime win-x64 --self-contained true \
    -p:EnableWindowsTargeting=true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    --output artifacts/win-x64
