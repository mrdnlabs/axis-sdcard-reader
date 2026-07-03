# Axis SD Card Reader — UI/UX design brief

## What the app is

A Windows desktop application that lets a user view and export video recordings from a
micro SD card taken out of an **Axis security camera**. These cards are formatted ext4
(a Linux filesystem Windows can't read), so normally Windows just offers to *format* the
card — destroying the evidence. This app reads the card directly and safely: the moment
it opens a card it locks it **read-only**, so nothing on the card can ever be modified.
Think "evidence viewer": simple, trustworthy, calm.

## Who uses it

- Security/facilities staff or small-business owners pulling footage after an incident
- Occasionally law-enforcement or insurance users handling someone else's card
- Not video professionals — they know "camera, date, time, save the clip", not codecs

## The core user journey

1. **Insert** the SD card (via a USB card reader) → app detects it.
2. **Open** the card → app locks it read-only and indexes it (a few seconds).
3. **Browse** recordings, organized by camera and date/time. A card can hold one
   recording or thousands (weeks of footage, multiple cameras' history).
4. **Play** a recording: video player with a timeline, scrubbing, pause, playback speed
   (0.5×–8×). A "recording" is one continuous session (minutes to many hours), stored
   internally as ~5-minute chunks — the player must present it as ONE seamless timeline;
   chunks are an implementation detail users never see.
5. **Export** selected recordings (or a whole day/camera) to a local folder, with
   progress. Exported files are standard MKV videos.

## Information available per recording

- Camera (serial/MAC — users may want to assign friendly names)
- Start date/time, duration, trigger ("continuous" schedule vs. event/motion-triggered)
- Resolution/codec (e.g. 4K AV1), total size
- Status flags worth surfacing gently: "interrupted" (camera lost power / card was
  pulled mid-recording — the tail of the video plays but may end abruptly)

## Key states to design

- **Empty/waiting**: no card inserted — invite the user to insert one (auto-detected)
- **Device choice**: multi-slot readers show several slots; only one has a card
- **Opening/indexing**: brief busy state
- **Protection status**: always-visible reassurance that the card is locked & read-only
  ("Windows cannot write to or format this card while the app is open") — this is the
  app's central trust promise; make it feel like a seal, not a warning
- **Browsing**: camera → day → recording hierarchy; must stay usable at 1,000+ recordings
  (think: date navigation, maybe a calendar or timeline density view)
- **Playback**: video dominates; timeline with time-of-day labels; interrupted-recording
  indicator near the end of the timeline
- **Export**: destination picker, progress (files + %), success confirmation
- **Error states**: card is encrypted by the camera (unreadable — explain kindly),
  card unreadable/corrupt, card removed while in use, no recordings found
- **Elevation**: app requires a Windows admin (UAC) prompt at launch — set expectation

## Platform & constraints

- Windows 10/11 desktop, WPF (so: standard desktop idioms, resizable window,
  min ~900×560, mouse-first but keyboard-friendly)
- Video is rendered by an embedded LibVLC surface — a rectangular video region; overlays
  ON TOP of the video are technically awkward (airspace), so prefer controls around/below
  the video rather than floating over it
- Current palette is a dark theme (#1E1E24 background); dark feels right for a video
  app but the designer may propose otherwise
- Single window; no accounts, no network, no settings beyond trivial preferences

## Tone

Calm, factual, reassuring. The user may be stressed (something happened — that's why
they're pulling footage). Zero jargon: never say "ext4", "mount", "chunk", "MKV muxing";
say "card", "recording", "save".
