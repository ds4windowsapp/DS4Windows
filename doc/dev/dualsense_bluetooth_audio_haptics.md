# DualSense Bluetooth audio and haptics

This note documents the standard Bluetooth streaming path implemented in
DS4Windows. It does not describe virtual USB devices or native-game passthrough.

## User-visible behavior

The DualSense Device Options page can capture a selected Windows playback
endpoint and send:

- audio-derived haptics;
- DS4Windows rumble re-voiced through the haptic actuators;
- a mix of both haptic sources; and
- optional listening audio to the controller speaker or headphone jack.

The feature is off by default. It does not register a Windows audio endpoint and
does not change the Windows default playback device. Audio volume is applied to
the captured PCM before encoding and defaults to 50 percent.

## Transport

Streaming uses the existing Bluetooth HID connection. A self-contained 398-byte
output report (0x36) is emitted every 32 haptic samples, or approximately
10.667 ms:

| Offset | Length | Contents |
| ---: | ---: | --- |
| 0 | 1 | Report ID 0x36 |
| 1 | 1 | Four-bit rolling sequence |
| 2 | 2 | Sized configuration packet (0x91, length 7) |
| 4 | 1 | Configuration flags (0xFE) |
| 5 | 5 | Controller dejitter depth (32, 64, or 120) |
| 10 | 1 | Rolling packet counter |
| 11 | 2 | Sized state packet (0x90, length 63) |
| 13 | 63 | DualSense state payload |
| 76 | 2 | Sized haptic packet (0x92, length 64) |
| 78 | 64 | Signed 8-bit, 3 kHz stereo haptic PCM |
| 142 | 2 | Optional speaker (0x93) or headphone (0x96) packet header, length 200 |
| 144 | 200 | Optional Opus frame |
| 344 | 50 | Reserved zero padding |
| 394 | 4 | Bluetooth CRC-32 over prefix 0xA2 and bytes 0 through 393 |

Listening audio uses 48 kHz stereo Opus frames with 480 samples per channel,
160 kbit/s constant bitrate, and a fixed 200-byte payload. Frames are delivered
at the controller's effective 45 kHz consumption rate.

Before listening audio starts, a 142-byte 0x32 state report enables the
controller amplifier. The UI volume remains independent of that fixed amplifier
setup.

## Timing and recovery

The capture pipeline uses WASAPI loopback and accepts only 32-bit IEEE-float mix
formats. Stereo and common multichannel layouts are downmixed before haptic
filtering or Opus resampling.

An integral rate servo trims the resampler to keep the audio queue near its
target despite clock drift. The three buffering profiles combine host
prebuffering with controller dejitter depths of 32 (Low Latency), 64 (Balanced),
and 120 (Smooth). Underruns temporarily increase the host prebuffer; long clean
runs reduce it toward the selected profile.

Missed audio deadlines are skipped instead of sent as catch-up bursts. Filler
frames decay toward silence through the live Opus encoder, and resumed content
uses a short fade-in. An energy gate sends six tail reports and then stops idle
traffic until audio or haptics becomes active again.

All HID output producers share one serialization lock. Existing controller
output retains its wait-until-complete behavior; only this streaming path uses
bounded writes with cancellation completion draining.

## Controller-state interaction

While advanced haptic streaming is active, the ordinary Bluetooth state report
keeps input, LEDs, and adaptive-trigger flags but suppresses legacy rumble flags
and stale motor bytes. The controller cannot run legacy rumble emulation and
advanced haptic PCM at the same time. Modes containing "Rumble To Haptics"
preserve game rumble by synthesizing it into the haptic stream.

## Validation and limits

Hardware validation on a first-party DualSense covered Low Latency, Balanced,
and Smooth profiles. Audio was clean without clicks or choppiness, and input,
rumble-derived haptics, audio-derived haptics, silence/resume, and routing felt
correct. Telemetry recorded no dropped frames, stall skips, slow HID writes,
write failures, or default-audio-device changes during the validation session.

Current limits:

- microphone transport is not implemented;
- DualSense Edge hardware has not been validated;
- Low Latency depends on Bluetooth link quality; and
- the controller is a relay destination, not a Windows audio endpoint.

Protocol research and dependency licenses are recorded in
THIRD-PARTY-NOTICES.txt.
