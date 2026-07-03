# Axis SD Card Reader

A Windows desktop app for browsing, playing, and exporting video recordings from
micro SD cards used in Axis network cameras ("edge storage"), read directly from
a USB card reader — no ext4 driver installation, no risk of Windows formatting
the card.

## Why

Axis cameras format SD cards as **ext4**, which Windows cannot read. Worse,
Windows offers to *format* the "unreadable" card the moment it is inserted.
This app:

- detects the inserted card and immediately **locks the volume and removes its
  drive letter**, so Explorer's format prompt cannot appear;
- reads the ext4 filesystem **strictly read-only** from the raw device
  (`\\.\PhysicalDriveN` opened with `GENERIC_READ` only — the OS itself
  guarantees no writes on that handle);
- indexes recordings from Axis's on-card layout
  (`YYYYMMDD_HHMMSS_xxxx_<cameraMAC>` directories of MKV chunks);
- plays them in-app (H.264/H.265 via LibVLC — no paid Windows HEVC codec
  needed) and exports them to local folders.

## Status

Feature-complete against card images; awaiting validation on real Axis cards.

- [x] Phase 1 — ext4 reading (image-based) with tests
- [x] Phase 2 — raw device access + volume protection + CardProbe harness
- [x] Phase 3 — Axis recording model (sessions, MKV metadata)
- [x] Phase 4 — WPF UI: browse + play (chunk-spanning timeline, speed control)
- [x] Phase 5 — export with progress and verification
- [ ] Validation against real Axis-written SD cards (`CardProbe --disk N`)
- [ ] Installer/packaging

## Trying it without hardware

```
dotnet run --project tools\CardProbe -- --image tests\fixtures\axis-card-v2.img
dotnet build src\AxisSdReader.App -p:DevManifest=true && src\AxisSdReader.App\bin\Debug\net8.0-windows\AxisSdReader.App.exe tests\fixtures\axis-card-v2.img
```

(The fixture image is generated on first `dotnet test` run via WSL.)

## Validating a real card

1. Insert the SD card via the USB reader.
2. From an **elevated** terminal: `dotnet run --project tools\CardProbe` to find the disk number,
   then `dotnet run --project tools\CardProbe -- --disk N`.
3. If the probe reports `Status: Ok` and lists recordings, run the app and open the card.

## Building

Requires the .NET 8 SDK (or later) on Windows. WSL (with `e2fsprogs`) is used
to generate the ext4 test fixture image on first test run.

```
dotnet build
dotnet test
```

The app itself requires administrator elevation at runtime (raw disk access on
Windows is admin-only).

## Export & FFmpeg

Trimmed export (MP4 remux / MKV, cut to the marked in/out range) shells out to
**FFmpeg** using stream copy (`-c copy`) — no re-encode, so it is fast and
lossless. The app locates `ffmpeg.exe` in this order:

1. an `ffmpeg` folder next to the app executable,
2. next to the app executable directly,
3. on the system `PATH`.

For distribution, bundle an **LGPL** FFmpeg build in the `ffmpeg` folder. If
FFmpeg is missing, the app disables trimmed export and says so. (MP4 keeps the
original codec — H.264/H.265/AV1 — so very old players may still need a modern
build; a true re-encode-to-H.264 path and timestamp burn-in are future work.)

## Key dependencies

| Component | Library | License |
|---|---|---|
| ext4 (read-only) | [LTRData.DiscUtils](https://github.com/LTRData/DiscUtils) | MIT |
| Video playback | [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | LGPL 2.1 |
| Trimmed export | [FFmpeg](https://ffmpeg.org) (`ffmpeg.exe`, external) | LGPL 2.1+ build |
