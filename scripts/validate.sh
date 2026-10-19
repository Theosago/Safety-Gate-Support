#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
source scripts/dotnet-env.sh
dotnet build SafetyGateSupport/SafetyGateSupport.csproj \
    --configuration Release -p:EnableWindowsTargeting=true --nologo
dotnet build tools/SiemensUsbCheck/SiemensUsbCheck.csproj \
    --configuration Release --nologo
dotnet run --project tests/SafetyGateSupport.SiemensValidation/SafetyGateSupport.SiemensValidation.csproj \
    --configuration Release
dotnet run --project tests/SafetyGateSupport.Validation/SafetyGateSupport.Validation.csproj \
    --configuration Release -- "$@"
