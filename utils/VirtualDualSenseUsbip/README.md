# VirtualDualSenseUsbip

Experimental .NET 8 USB/IP device server used by DS4Windows Native Mode. It
exposes a virtual wired DualSense composite device and relays HID input,
adaptive-trigger output, four-channel haptic audio, and game speaker audio to
one explicitly selected physical Bluetooth DualSense.

## Safety status

This helper is an architecture preview, not a production feature. It depends
on the separately installed usbip-win2 virtual host-controller driver; no
driver is bundled here. usbip-win2 0.9.7.8 has a confirmed kernel request-
lifetime race during USB Audio endpoint teardown. DS4Windows and this helper
contain mitigations for the reproduced paths, but those mitigations do not
prove the third-party kernel driver safe.

Do not distribute an end-user Native Mode build or treat this code as
merge-ready until the driver risk and parent-process crash containment called
out in [the Native Mode design note](../../doc/dev/native_dualsense_mode.md)
are resolved.

## Design

- The server listens only on loopback and implements the Linux USB/IP wire
  protocol needed by usbip-win2.
- Six byte-exact USB descriptor assets are shipped under `Descriptors/`.
- DS4Windows passes the selected controller's HID path, VID, and PID. The
  helper validates that exact identity and fails closed; it never chooses the
  first matching controller.
- USB Audio render traffic is relayed to the physical controller over its
  Bluetooth audio/haptics container. Adaptive-trigger output is relayed over
  Bluetooth HID.
- The virtual microphone enumerates but currently returns silence. Enabling
  the physical controller microphone is deliberately blocked in normal use
  because it causes a Bluetooth disconnect on the tested Windows stack.
- DS4Windows invokes usbip.exe only from a canonical Program Files location,
  asks for UAC approval on each fixed attach, and removes the exact reusable
  attach task left by older experimental builds.

## Offline validation

```powershell
dotnet build .\utils\VirtualDualSenseUsbip\VirtualDualSenseUsbip.csproj -c Release
dotnet run --no-build --project .\utils\VirtualDualSenseUsbip -c Release -- selftest
dotnet run --no-build --project .\utils\VirtualDualSenseUsbip -c Release -- devicetest
dotnet run --no-build --project .\utils\VirtualDualSenseUsbip -c Release -- servertest
```

Protocol reference:
[Linux kernel USB/IP protocol documentation](https://docs.kernel.org/usb/usbip_protocol.html).
