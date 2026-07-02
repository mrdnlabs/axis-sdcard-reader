#!/usr/bin/env bash
# Generates an Axis-like ext4 SD card image for tests.
# Runs under Linux/WSL; requires sfdisk and mke2fs (e2fsprogs), no root needed.
#
# Usage: make-fixture.sh <output.img> [fs_size_mb]
set -euo pipefail

OUT=$1
FS_MB=${2:-64}
PART_OFFSET=$((1024 * 1024)) # partition starts at 1 MiB, the common alignment

ROOT=$(mktemp -d)
trap 'rm -rf "$ROOT"' EXIT

# --- Axis-like content -------------------------------------------------------
# Recording directories: YYYYMMDD_HHMMSS_<4hex>_<cameraMAC>, containing MKV chunks.
mk_recording() {
  local dir="$ROOT/$1"
  shift
  mkdir -p "$dir"
  local i
  for i in "$@"; do
    # Dummy chunk: EBML magic followed by random payload (8 KiB total).
    { printf '\x1a\x45\xdf\xa3'; head -c 8188 /dev/urandom; } > "$dir/${i}.mkv"
  done
}

mk_recording "20250114_093000_1A2B_ACCC8E123456" 0 1 2
mk_recording "20250114_101500_77F0_ACCC8E123456" 0 1
mk_recording "20250302_180000_0C3D_B8A44F998877" 0

# Stand-in for the proprietary index database at the card root.
head -c 4096 /dev/urandom > "$ROOT/index.db"

# Large sparse file (5 GiB logical) with a marker in its final 16 bytes:
# exercises 64-bit file sizes and sparse-extent reads in the ext4 reader.
BIG="$ROOT/bigfile.bin"
BIG_SIZE=$((5 * 1024 * 1024 * 1024))
truncate -s "$BIG_SIZE" "$BIG"
printf 'AXIS-TAIL-MARKER' | dd of="$BIG" bs=1 seek=$((BIG_SIZE - 16)) conv=notrunc status=none

# --- image assembly ----------------------------------------------------------
IMG_SIZE=$((PART_OFFSET + FS_MB * 1024 * 1024))
rm -f "$OUT"
truncate -s "$IMG_SIZE" "$OUT"

sfdisk --quiet --force "$OUT" <<EOF
label: dos
start=$((PART_OFFSET / 512)), type=83
EOF

mke2fs -q -F -t ext4 -E offset=$PART_OFFSET -d "$ROOT" -L axis-sd "$OUT" "${FS_MB}m"

echo "created $OUT"
