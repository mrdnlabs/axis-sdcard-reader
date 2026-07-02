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

Work in progress.

- [x] Phase 1 — ext4 reading (image-based) with tests
- [ ] Phase 2 — raw device access + volume protection + CardProbe harness
- [ ] Phase 3 — Axis recording model (sessions, MKV metadata)
- [ ] Phase 4 — WPF UI: browse + play
- [ ] Phase 5 — export, error paths, packaging

## Building

Requires the .NET 8 SDK (or later) on Windows. WSL (with `e2fsprogs`) is used
to generate the ext4 test fixture image on first test run.

```
dotnet build
dotnet test
```

The app itself requires administrator elevation at runtime (raw disk access on
Windows is admin-only).

## Key dependencies

| Component | Library | License |
|---|---|---|
| ext4 (read-only) | [LTRData.DiscUtils](https://github.com/LTRData/DiscUtils) | MIT |
| Video playback | [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | LGPL 2.1 |
