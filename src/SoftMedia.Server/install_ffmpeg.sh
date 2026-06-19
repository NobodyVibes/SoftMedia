#!/usr/bin/env bash
# Fetch the official jellyfin-ffmpeg portable build (macOS) into ./ffmpeg-bin.
#
# SoftMedia REQUIRES jellyfin-ffmpeg: it ships the `chromaprint` muxer used by intro/credits
# detection (`ffmpeg -f chromaprint`). Core Homebrew ffmpeg lacks chromaprint, so do NOT use
# `brew install ffmpeg`. The binary is fetched from Jellyfin's official server, not committed to
# git (see docs/plans/licensing-and-repo-hygiene-plan-2026-06-18.md). jellyfin-ffmpeg is GPL-3.0.
#
# Linux: do NOT use this script — install the apt package instead:
#   add Jellyfin's apt repo (repo.jellyfin.org) and `apt-get install jellyfin-ffmpeg7`
#   (binaries land at /usr/lib/jellyfin-ffmpeg/ffmpeg; set FFmpeg__Path accordingly).
#
# Usage: ./install_ffmpeg.sh [VERSION]   (default 7.1.4-3; stay on the 7.x line)
set -euo pipefail

VERSION="${1:-7.1.4-3}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_DIR="$SCRIPT_DIR/ffmpeg-bin"
BASE="https://repo.jellyfin.org/files/ffmpeg/macos/latest-7.x"

case "$(uname -s)" in
  Darwin) ;;
  *) echo "ERROR: this script is for macOS only. On Linux use the jellyfin-ffmpeg7 apt package." >&2; exit 1 ;;
esac

case "$(uname -m)" in
  arm64)  ARCH="macarm64" ;;
  x86_64) ARCH="macx86_64" ;;
  *) echo "ERROR: unsupported arch $(uname -m)" >&2; exit 1 ;;
esac

verify_chromaprint() {
  local exe="$1"
  [ -x "$exe" ] || return 1
  "$exe" -hide_banner -version 2>/dev/null | grep -q -- "--enable-chromaprint" || return 1
  "$exe" -hide_banner -muxers 2>/dev/null | grep -q "chromaprint" || return 1
}

if verify_chromaprint "$TARGET_DIR/ffmpeg"; then
  echo "[OK] jellyfin-ffmpeg already present with chromaprint at $TARGET_DIR/ffmpeg"
  exit 0
fi

ARCH_DIR="${ARCH#mac}"   # arm64 | x86_64 path segment
URL="$BASE/$ARCH_DIR/jellyfin-ffmpeg_${VERSION}_portable_${ARCH}-gpl.tar.xz"

rm -rf "$TARGET_DIR"; mkdir -p "$TARGET_DIR"
TARBALL="$TARGET_DIR/jellyfin-ffmpeg.tar.xz"
echo "[>] Downloading $URL"
curl -fsSL "$URL" -o "$TARBALL"
echo "[>] Extracting..."
tar -xJf "$TARBALL" -C "$TARGET_DIR"

for name in ffmpeg ffprobe; do
  found="$(find "$TARGET_DIR" -type f -name "$name" | head -n1)"
  [ -n "$found" ] || { echo "ERROR: $name not found in archive" >&2; exit 1; }
  [ "$found" = "$TARGET_DIR/$name" ] || mv -f "$found" "$TARGET_DIR/$name"
  chmod +x "$TARGET_DIR/$name"
done
rm -f "$TARBALL"

if ! verify_chromaprint "$TARGET_DIR/ffmpeg"; then
  echo "ERROR: downloaded ffmpeg lacks the chromaprint muxer — wrong build." >&2
  exit 1
fi
echo "[OK] jellyfin-ffmpeg installed at $TARGET_DIR (chromaprint verified)"
