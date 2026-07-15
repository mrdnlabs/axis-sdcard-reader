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
- unlocks **encrypted** cards (LUKS) with the camera's passphrase and reads them
  the same read-only way (see *Encrypted cards* below);
- plays them in-app (H.264/H.265 via LibVLC — no paid Windows HEVC codec
  needed) and exports them to local folders.

## Status

Validated on real Axis cards (plain **and** LUKS-encrypted); ext4 and decryption
paths both proven against hardware.

- [x] Phase 1 — ext4 reading (image-based) with tests
- [x] Phase 2 — raw device access + volume protection + CardProbe harness
- [x] Phase 3 — Axis recording model (sessions, MKV metadata)
- [x] Phase 4 — WPF UI: browse + play (chunk-spanning timeline, speed control)
- [x] Phase 5 — export with progress and verification
- [x] Encrypted-card support — LUKS1 unlock (read-only) with a passphrase prompt
- [x] Validation against real Axis-written SD cards (`CardProbe --disk N`)
- [x] Packaging — self-contained win-x64 zip via `tools\publish.ps1`; CI runs build + tests

## Encrypted cards

Newer Axis firmware can encrypt the SD card (System → Storage). Encrypted cards
are **LUKS** containers (dm-crypt, `aes-xts-plain64`) wrapping the same ext4
filesystem. When the app detects one it prompts for the camera's SD-card
passphrase, derives the key (PBKDF2), and decrypts sectors **in memory** as it
reads — the card is still never written, and the passphrase is never stored.
Only **LUKS1** is supported today; a LUKS2 card is detected and reported as an
unsupported format (Argon2id support is future work). The crypto uses .NET's
built-in AES/PBKDF2 (AES-XTS layered on AES) — no external crypto dependency.

## Trying it without hardware

```
dotnet run --project tools\CardProbe -- --image tests\fixtures\axis-card-v4.img
dotnet build src\AxisSdReader.App -p:DevManifest=true && src\AxisSdReader.App\bin\Debug\net8.0-windows\AxisSdReader.App.exe tests\fixtures\axis-card-v4.img
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
lossless.

**The released standalone exe bundles FFmpeg** (an LGPL v3 build with no GPL
components — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)), so export works
out of the box with nothing to install.

When building from source, the app locates `ffmpeg.exe` in this order:

1. an `ffmpeg` folder next to the app executable,
2. next to the app executable directly,
3. on the system `PATH` — **only when not running elevated** (see below).

Because the app runs as administrator, it deliberately does **not** search the
`PATH`: a `PATH` directory writable by a standard user could let that user plant a
binary that then executes with administrator rights. `tools\publish.ps1` fetches the
LGPL FFmpeg into `src\AxisSdReader.App\ffmpeg\` (gitignored) so it gets bundled into
the exe; for a dev build you can drop any `ffmpeg.exe` there yourself. If FFmpeg is
missing, the app disables trimmed export and says so. (MP4 keeps the original codec —
H.264/H.265/AV1 — so very old players may still need a modern build; a true
re-encode-to-H.264 path and timestamp burn-in are future work.)

## Installing & verifying your download

Each release ships **two packages** — same app, same bundled libVLC and FFmpeg, no
prerequisites and no installer either way. Pick whichever suits the machine:

| Package | What it is | Best for |
|---|---|---|
| `…-win-x64-standalone.zip` | a **single** `AxisSdReader.App.exe` (~240 MB), everything inside it | any modern SSD — one file, nothing to deploy |
| `…-win-x64-folder.zip` | the classic folder deploy | **hard disks and IT rollouts** — always launches instantly |

Run `AxisSdReader.App.exe` and accept the UAC prompt (raw disk access requires
administrator rights). Keep it somewhere standard users can't write to (e.g.
`Program Files`).

The **standalone** exe unpacks its payload to a per-version cache on first launch —
around 6 seconds on an SSD, but appreciably longer on a hard disk, and with no
progress shown (the unpacking happens in the .NET host *before* the app itself
starts, so nothing can draw a splash). Later launches are instant. If that matters,
or you'd rather not leave ~529 MB per version in `%TEMP%`, take the **folder**
package — it never extracts anything.

Both zips carry the licence texts that must accompany the bundled components.

The executable is **not code-signed**, so on first run expect:

- a SmartScreen *"Windows protected your PC"* notice → **More info → Run anyway**;
- a UAC prompt showing **Publisher: Unknown**.

To verify integrity out-of-band, compare the release's published **SHA-256**
against your download:

```
Get-FileHash .\AxisSdReader-vX.Y.Z-win-x64-standalone.zip -Algorithm SHA256
```

## Key dependencies

| Component | Library | License |
|---|---|---|
| ext4 (read-only) | [LTRData.DiscUtils](https://github.com/LTRData/DiscUtils) | MIT |
| Video playback | [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | LGPL 2.1 |
| Trimmed export | [FFmpeg](https://ffmpeg.org) (`ffmpeg.exe`, external) | LGPL 2.1+ build |
| LUKS decryption | .NET `System.Security.Cryptography` (built-in) | — |

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE).
Third-party components it uses or bundles (libVLC / LibVLCSharp under LGPL-2.1,
DiscUtils and CommunityToolkit.Mvvm under MIT) are attributed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which is also included in the
release package.
