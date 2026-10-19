#!/usr/bin/env bash
# Install Microsoft's pinned Linux x64 SDK with its published SHA512 checksum.
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
source scripts/dotnet-env.sh
if command -v dotnet >/dev/null && dotnet --version >/dev/null 2>&1; then
    dotnet --version
    exit 0
fi
if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    echo 'Install the .NET SDK specified in global.json for your operating system.' >&2
    exit 1
fi
sdk_version=10.0.402
sdk_sha512=1904a98dda2db34ba78ba3c4fb1b224455329af91c341d83547843c1839e112b04ae699ee92fa2449a8f62249196a938723078c0c60b9f65c88539d5687729c2
sdk_download_dir="$(mktemp -d)"
trap 'rm -rf -- "$sdk_download_dir"' EXIT
curl --fail --show-error --location --retry 2 \
    "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" \
    -o "$sdk_download_dir/sdk.tar.gz"
printf '%s  %s\n' "$sdk_sha512" "$sdk_download_dir/sdk.tar.gz" | sha512sum --check --status
mkdir -p "$DOTNET_ROOT"
tar -xzf "$sdk_download_dir/sdk.tar.gz" -C "$DOTNET_ROOT"
"$DOTNET_ROOT/dotnet" --version
