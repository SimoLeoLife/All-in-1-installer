#!/usr/bin/env bash
# Publish Linux x64 single-file self-contained binary.
# Output: publish/linux-x64/All-in-1 Installer

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CONFIG=Release
RID=linux-x64
OUT="$SCRIPT_DIR/publish/$RID"
DOTNET_EXE="$SCRIPT_DIR/.dotnet/dotnet"

if [[ ! -x "$DOTNET_EXE" ]]; then
    DOTNET_EXE=dotnet
fi

rm -rf "$OUT"

cd "$SCRIPT_DIR/SourceCode"
"$DOTNET_EXE" publish "All-in-1 Installer.csproj" \
    -c "$CONFIG" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$OUT"

chmod +x "$OUT/All-in-1 Installer" || true

echo
echo "Published Linux build at: $OUT"
echo "Run: \"$OUT/All-in-1 Installer\""
