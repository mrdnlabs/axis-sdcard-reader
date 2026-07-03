# Axis SD card on-disk format (as observed)

Observed 2026-07-02 on a card written by an **AXIS P3288-LVE Dome Camera** (AXIS OS 12.x era,
AV1 recording). Cross-checked against the VAPIX Edge Storage documentation.

## Disk layout

- MBR partition table, one Linux partition starting at 4 MiB.
- ext4 filesystem, volume label `Axis`.
- Filesystem features are readable by LTRData.DiscUtils (verified on real hardware).

## Directory layout (modern, AXIS OS 10+)

```
/<YYYYMMDD>/                                  date, camera-local time
    /<HH>/                                    hour, camera-local time
        /<recordingId>/                       e.g. 20260702_190233_3CEA_E827251FFB8D
            recording.xml                     ONVIF-style recording metadata
            /<YYYYMMDD_HH>/                   chunk bucket (hour)
                <YYYYMMDD_HHMMSS_XXXX>.mkv    ~5-minute chunk (timestamp + 4 hex)
                <YYYYMMDD_HHMMSS_XXXX>.xml    per-chunk sidecar
/index.db                                     SQLite index (schema below)
/recording_groups/recording_groups.conf       empty unless recording groups are used
/areas/, /ws/, /music/, /osr/, ...            ACAP application + system data (not recordings)
```

`recordingId` = `YYYYMMDD_HHMMSS_<4 hex>_<camera serial/MAC>`; the timestamp is **camera-local
time**, while all XML/DB timestamps are true UTC.

An older/legacy layout places `<recordingId>/` directories directly at the card root with
numeric chunk names (`0.mkv`, `1.mkv`, ...). The indexer supports both.

## recording.xml

```xml
<Recording RecordingToken="20260702_190233_3CEA_E827251FFB8D">
  <RecordingGroup/> <SourceToken>1</SourceToken>
  <StartTime>2026-07-03T00:02:33.283555Z</StartTime>
  <Track TrackToken="Video"><VideoAttributes>
    <Width>3840</Width> <Height>2160</Height>
    <Framerate>30.00000</Framerate> <Framerate_fraction>30:1</Framerate_fraction>
    <Encoding>video/x-av1</Encoding> <Bitrate>0</Bitrate>
  </VideoAttributes></Track>
  <Application>AxisCamera</Application>
  <CustomAttributes>
    <TriggerTrigger>continuous</TriggerTrigger>
    <TriggerName>continuous</TriggerName>
    <TriggerType>continuous</TriggerType>
  </CustomAttributes>
</Recording>
```

## Chunk sidecar XML

```xml
<RecordingBlock RecordingBlockToken="20260702_190233_5B35">
  <RecordingToken>20260702_190233_3CEA_E827251FFB8D</RecordingToken>
  <StartTime>2026-07-03T00:02:33.283555Z</StartTime>
  <StopTime>2026-07-03T00:07:34.882442Z</StopTime>   <!-- absent while recording -->
  <Status>Complete</Status>                          <!-- or "Recording" -->
</RecordingBlock>
```

A chunk with `Status=Recording` (no `StopTime`) was being written when the card was removed;
its MKV is truncated and its header `Duration` element is a `0` placeholder — the app derives
the real duration by scanning cluster/block timestamps.

## MKV chunks

- Matroska, `WritingApp` = camera model string (e.g. `AXIS P3288-LVE Dome Camera`).
- `DateUTC` present and matches the sidecar `StartTime` exactly.
- Codec observed: `V_AV1` (3840×2160@30). H.264/H.265 expected from older firmware.
- Completed chunks have a proper header `Duration`; ~5-minute chunk cadence.

## index.db (SQLite, version table says 3.0)

Key tables (all timestamps ISO-8601 UTC):

- `recordings(id, filename=recordingId, path='YYYYMMDD/HH', starttime, stoptime NULL while
  active, recording_type_id, recording_source_id, recording_event_id, recording_action_id,
  video_id, ...)`
- `blocks(id, filename=chunk basename, path='YYYYMMDD_HH', starttime, stoptime NULL while
  active, recording_id, filesize)` — filesize 0 while the block is being written
- `videos(videos_type='video/x-av1', videos_properties='"width"="3840"...')`
- `recording_types/sources/actions/events` — e.g. `continuous`
- `remove_recordings`/`remove_blocks` — pending deletions

The app treats folder names + XML sidecars as ground truth and does not depend on index.db.
