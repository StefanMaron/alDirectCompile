#!/bin/bash
# Download BC artifacts (platform + country) to a target directory.
# Usage: download-artifacts.sh <type> <version> <country> <dest>
set -e

BC_TYPE="${1:?Usage: $0 <type> <version> <country> <dest>}"
BC_VERSION="${2:?}"
BC_COUNTRY="${3:?}"
DEST="${4:?}"

BASE_URL="https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net"

echo "[artifacts] Downloading $BC_TYPE/$BC_VERSION/$BC_COUNTRY..."

# Download and extract country artifact
mkdir -p "$DEST/app"
echo "[artifacts] Downloading app artifact..."
curl -sSL "$BASE_URL/$BC_TYPE/$BC_VERSION/$BC_COUNTRY" -o "$DEST/bc-app.zip"
echo "[artifacts] Extracting app artifact ($(du -h "$DEST/bc-app.zip" | cut -f1))..."
unzip -qo "$DEST/bc-app.zip" -d "$DEST/app"
rm -f "$DEST/bc-app.zip"

# Read platform version from manifest
PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$DEST/app/manifest.json'))['platform'])")
echo "[artifacts] Platform version: $PLATFORM_VERSION"

# Platform artifact is at the same version path as the app, with "platform" as country
mkdir -p "$DEST/platform"
echo "[artifacts] Downloading platform artifact..."
curl -sSL "$BASE_URL/$BC_TYPE/$BC_VERSION/platform" -o "$DEST/bc-platform.zip"
echo "[artifacts] Extracting platform artifact ($(du -h "$DEST/bc-platform.zip" | cut -f1))..."
unzip -qo "$DEST/bc-platform.zip" -d "$DEST/platform"
rm -f "$DEST/bc-platform.zip"

echo "[artifacts] Done. Artifacts at $DEST"
