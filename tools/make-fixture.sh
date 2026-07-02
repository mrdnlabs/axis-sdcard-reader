#!/usr/bin/env bash
# Generates an Axis-like ext4 SD card image for tests.
# Runs under Linux/WSL; requires sfdisk, mke2fs (e2fsprogs) and ffmpeg, no root needed.
#
# Usage: make-fixture.sh <output.img> [fs_size_mb]
set -euo pipefail

OUT=$1
FS_MB=${2:-64}
PART_OFFSET=$((1024 * 1024)) # partition starts at 1 MiB, the common alignment

command -v ffmpeg >/dev/null || { echo "ffmpeg is required" >&2; exit 1; }

ROOT=$(mktemp -d)
trap 'rm -rf "$ROOT"' EXIT

# Tiny real H.264 MKV chunk. $1=path $2=creation_time (Matroska DateUTC)
mkv() {
  ffmpeg -loglevel error -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=10" \
    -c:v libx264 -preset ultrafast -metadata creation_time="$2" "$1"
}

# Live-muxed variant: piped output means ffmpeg cannot seek back, so the header has
# no Duration element and the segment size is unknown - like camera edge storage.
mkv_live() {
  ffmpeg -loglevel error -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=10" \
    -c:v libx264 -preset ultrafast -metadata creation_time="$2" -f matroska - > "$1"
}

# --- Axis-like content -------------------------------------------------------
# Recording directories: YYYYMMDD_HHMMSS_<4hex>_<cameraMAC>, containing MKV chunks.

R1="$ROOT/20250114_093000_1A2B_ACCC8E123456"
mkdir -p "$R1"
mkv "$R1/0.mkv" "2025-01-14T09:30:00Z"
mkv "$R1/1.mkv" "2025-01-14T09:30:02Z"
mkv "$R1/2.mkv" "2025-01-14T09:30:04Z"

R2="$ROOT/20250114_101500_77F0_ACCC8E123456"
mkdir -p "$R2"
mkv      "$R2/0.mkv" "2025-01-14T10:15:00Z"
mkv_live "$R2/1.mkv" "2025-01-14T10:15:02Z"
# Truncated chunk: the first 60% of a live-muxed file, as after camera power loss.
mkv_live "$R2/tmp_full.mkv" "2025-01-14T10:15:04Z"
FULL_SIZE=$(stat -c %s "$R2/tmp_full.mkv")
head -c $((FULL_SIZE * 60 / 100)) "$R2/tmp_full.mkv" > "$R2/2.mkv"
rm "$R2/tmp_full.mkv"

# A recording whose only chunk is not a valid MKV at all (corrupt data).
R3="$ROOT/20250302_180000_0C3D_B8A44F998877"
mkdir -p "$R3"
head -c 8192 /dev/urandom > "$R3/0.mkv"

# Stand-in for the proprietary index database at the card root.
head -c 4096 /dev/urandom > "$ROOT/index.db"

# Large sparse file (5 GiB logical) with a marker in its final 16 bytes:
# exercises 64-bit file sizes and sparse-extent reads in the ext4 reader.
BIG="$ROOT/bigfile.bin"
BIG_SIZE=$((5 * 1024 * 1024 * 1024))
truncate -s "$BIG_SIZE" "$BIG"
printf 'AXIS-TAIL-MARKER' | dd of="$BIG" bs=1 seek=$((BIG_SIZE - 16)) conv=notrunc status=none

# --- image assembly ----------------------------------------------------------
# Build on the native Linux filesystem: mke2fs -d misbehaves on WSL's /mnt/c (9p) mounts.
IMG=$(mktemp /tmp/axis-fixture-XXXXXX.img)
trap 'rm -rf "$ROOT" "$IMG"' EXIT

IMG_SIZE=$((PART_OFFSET + FS_MB * 1024 * 1024))
truncate -s "$IMG_SIZE" "$IMG"

sfdisk --quiet --force "$IMG" <<EOF
label: dos
start=$((PART_OFFSET / 512)), type=83
EOF

mke2fs -q -F -t ext4 -E offset=$PART_OFFSET -d "$ROOT" -L axis-sd "$IMG" "${FS_MB}m"

cp "$IMG" "$OUT"
echo "created $OUT"
