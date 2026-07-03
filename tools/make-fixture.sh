#!/usr/bin/env bash
# Generates an Axis-like ext4 SD card image for tests, mirroring the layout observed on a
# real card written by an AXIS P3288-LVE (AXIS OS 12.x, July 2026):
#
#   /<YYYYMMDD>/<HH>/<recordingId>/recording.xml
#   /<YYYYMMDD>/<HH>/<recordingId>/<YYYYMMDD_HH>/<YYYYMMDD_HHMMSS_XXXX>.mkv (+ .xml sidecar)
#   /index.db, /recording_groups/recording_groups.conf, ACAP dirs (areas/, ws/, ...)
#
# plus one legacy flat-layout recording (recording dir at card root).
#
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

# Tiny real MKV chunk. $1=path $2=creation_time $3=codec (libx264|libaom-av1)
mkv() {
  local codec_args=(-c:v libx264 -preset ultrafast)
  if [ "${3:-}" = av1 ]; then
    codec_args=(-c:v libaom-av1 -crf 50 -b:v 0 -cpu-used 8 -row-mt 1)
  fi
  ffmpeg -loglevel error -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=10" \
    "${codec_args[@]}" -metadata creation_time="$2" "$1"
}

# Live-muxed variant: piped output means ffmpeg cannot seek back, so the header has
# no (or a zero) Duration element and the segment size is unknown - like camera output.
mkv_live() {
  ffmpeg -loglevel error -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=10" \
    -c:v libx264 -preset ultrafast -metadata creation_time="$2" -f matroska - > "$1"
}

# Sidecar RecordingBlock XML. $1=path $2=token $3=recToken $4=start $5=stop(empty=Recording)
block_xml() {
  if [ -n "$5" ]; then
    printf '<RecordingBlock RecordingBlockToken="%s" ><RecordingToken>%s</RecordingToken><StartTime>%s</StartTime><StopTime>%s</StopTime><Status>Complete</Status></RecordingBlock>' \
      "$2" "$3" "$4" "$5" > "$1"
  else
    printf '<RecordingBlock RecordingBlockToken="%s" ><RecordingToken>%s</RecordingToken><StartTime>%s</StartTime><Status>Recording</Status></RecordingBlock>' \
      "$2" "$3" "$4" > "$1"
  fi
}

# recording.xml. $1=path $2=token $3=start $4=encoding $5=trigger $6=sourceToken(default 1)
recording_xml() {
  local src="${6:-1}"
  printf '<Recording RecordingToken="%s" ><RecordingGroup> </RecordingGroup><SourceToken>%s</SourceToken><StartTime>%s</StartTime><Content></Content><Track TrackToken="Video"><VideoAttributes>  <Width>320</Width>  <Height>240</Height>  <Framerate>10.00000</Framerate>  <Framerate_fraction>10:1</Framerate_fraction>  <Encoding>%s</Encoding>  <Bitrate>0</Bitrate></VideoAttributes></Track><Application>AxisCamera</Application><CustomAttributes>  <TriggerTrigger>%s</TriggerTrigger>  <TriggerName>%s</TriggerName>  <TriggerType>%s</TriggerType></CustomAttributes></Recording>' \
    "$2" "$src" "$3" "$4" "$5" "$5" "$5" > "$1"
}

# --- modern nested layout (as on real cards) ----------------------------------

# Recording 1: H.264, three complete chunks with sidecars. Folder names are camera-local
# time (09:30), XML times are UTC (14:30, camera at UTC-5).
T1="20250114_093000_1A2B_ACCC8E123456"
D1="$ROOT/20250114/09/$T1/20250114_09"
mkdir -p "$D1"
recording_xml "$ROOT/20250114/09/$T1/recording.xml" "$T1" "2025-01-14T14:30:00.000000Z" "video/x-h264" "continuous"
mkv "$D1/20250114_093000_5B35.mkv" "2025-01-14T14:30:00Z"
block_xml "$D1/20250114_093000_5B35.xml" "20250114_093000_5B35" "$T1" "2025-01-14T14:30:00.000000Z" "2025-01-14T14:30:02.000000Z"
mkv "$D1/20250114_093002_9D2B.mkv" "2025-01-14T14:30:02Z"
block_xml "$D1/20250114_093002_9D2B.xml" "20250114_093002_9D2B" "$T1" "2025-01-14T14:30:02.000000Z" "2025-01-14T14:30:04.000000Z"
mkv "$D1/20250114_093004_84BA.mkv" "2025-01-14T14:30:04Z"
block_xml "$D1/20250114_093004_84BA.xml" "20250114_093004_84BA" "$T1" "2025-01-14T14:30:04.000000Z" "2025-01-14T14:30:06.000000Z"

# Recording 2: AV1, one complete chunk and one mid-write chunk (truncated MKV, sidecar
# without StopTime, Status=Recording) - as left behind when the card is pulled.
T2="20250114_101500_77F0_ACCC8E123456"
D2="$ROOT/20250114/10/$T2/20250114_10"
mkdir -p "$D2"
recording_xml "$ROOT/20250114/10/$T2/recording.xml" "$T2" "2025-01-14T15:15:00.000000Z" "video/x-av1" "continuous"
mkv "$D2/20250114_101500_0001.mkv" "2025-01-14T15:15:00Z" av1
block_xml "$D2/20250114_101500_0001.xml" "20250114_101500_0001" "$T2" "2025-01-14T15:15:00.000000Z" "2025-01-14T15:15:02.000000Z"
mkv_live "$D2/tmp_full.mkv" "2025-01-14T15:15:02Z"
FULL_SIZE=$(stat -c %s "$D2/tmp_full.mkv")
head -c $((FULL_SIZE * 60 / 100)) "$D2/tmp_full.mkv" > "$D2/20250114_101502_0002.mkv"
rm "$D2/tmp_full.mkv"
block_xml "$D2/20250114_101502_0002.xml" "20250114_101502_0002" "$T2" "2025-01-14T15:15:02.000000Z" ""

# --- legacy flat layout (recording dir at card root, numeric chunks, no sidecars) ----
T3="20250302_180000_0C3D_B8A44F998877"
mkdir -p "$ROOT/$T3"
mkv "$ROOT/$T3/0.mkv" "2025-03-02T18:00:00Z"
mkv "$ROOT/$T3/1.mkv" "2025-03-02T18:00:02Z"

# --- multi-sensor camera (as a 4-lens unit records: one recording per VAPIX source,
#     all starting together, distinguished by SourceToken). Serial DD11EE22FF33.
multi_rec() {  # $1=recToken $2=sourceToken
  local dir="$ROOT/20250115/12/$1/20250115_12"
  mkdir -p "$dir"
  recording_xml "$ROOT/20250115/12/$1/recording.xml" "$1" "2025-01-15T17:00:00.000000Z" "video/x-h264" "continuous" "$2"
  mkv "$dir/$1_C0.mkv" "2025-01-15T17:00:00Z"
  block_xml "$dir/$1_C0.xml" "${1}_C0" "$1" "2025-01-15T17:00:00.000000Z" "2025-01-15T17:00:02.000000Z"
}
multi_rec "20250115_120000_A1_DD11EE22FF33" "1"
multi_rec "20250115_120000_A2_DD11EE22FF33" "3"
multi_rec "20250115_120000_A3_DD11EE22FF33" "4"
multi_rec "20250115_120000_A4_DD11EE22FF33" "5"

# --- other real-card furniture ------------------------------------------------
mkdir -p "$ROOT/recording_groups" "$ROOT/areas/player" "$ROOT/ws/onvif/recording" "$ROOT/music" "$ROOT/osr"
: > "$ROOT/recording_groups/recording_groups.conf"
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

mke2fs -q -F -t ext4 -E offset=$PART_OFFSET -d "$ROOT" -L Axis "$IMG" "${FS_MB}m"

# Copy to a temp name first, then rename: concurrent invocations never see a partial file.
cp "$IMG" "$OUT.tmp.$$"
mv -f "$OUT.tmp.$$" "$OUT"
echo "created $OUT"
