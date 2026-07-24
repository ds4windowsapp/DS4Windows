# Native Mode descriptor assets: provenance and review

Status: **maintainer review dossier, not legal approval.** This document provides
the technical inventory for production gate 4 in
[the Native Mode design note](native_dualsense_mode.md). It records what can be
verified from the repository and retained public source fixture, and explicitly
identifies the remaining maintainer policy decisions.

## 1. Asset inventory

Native Mode packages six USB descriptor responses under
utils/VirtualDualSenseUsbip/Descriptors. The helper uses them to present the
composite HID and USB Audio topology observed from a retail Sony DualSense with
VID 054C and PID 0CE6.

The following values were independently rechecked against the files on
2026-07-21:

| Asset | Bytes | SHA-256 | Contents |
|---|---:|---|---|
| device.bin | 18 | 22927B1C25944C109955431846621306A4C91DB7A2AE5D92E740AF5561E18BB6 | USB device descriptor; iSerialNumber is zero |
| configuration.bin | 227 | A4C5E13FF088B3642DD2CE735EAC5207A84F2B343BB9A41095F1D6FD806DCCBF | Audio Control, playback/capture Audio Streaming, and HID interfaces |
| hid-report.bin | 289 | 4F48767516627510521512AF13C20A064877465122B7BCC7CAC1F3B608D37994 | HID report descriptor |
| string-0-lang-0000.bin | 4 | 2BF9E7166F6DCE93C04C256E2163F634EE0E7D8E5B8EFB029DF785BAA7E4C2F2 | Supported language descriptor containing LANGID 0409 |
| string-1-lang-0409.bin | 62 | 1E2A8747B339E7B139DA98BAE94358E0BCF4C80F386E4B2AC1042E41EF9BD155 | Manufacturer string: Sony Interactive Entertainment |
| string-2-lang-0409.bin | 60 | 7202A38073AD3A97FFFFCCD5691FF80FA976E11139DE71A9844DD5C3C2C90C9A | Product string: DualSense Wireless Controller |

The canonical hash list also appears in the descriptor directory README.

## 2. Verified technical properties

- device.bin declares VID 054C, PID 0CE6, and iSerialNumber = 0.
- No per-unit USB serial descriptor is included.
- The three string files contain only a supported-language list, the
  manufacturer name, and the product name listed above.
- Byte inspection found no Bluetooth address, pairing key, account name,
  username, capture path, gameplay data, or other unit/user identifier.
- The composite configuration file is byte-identical to the configuration used
  in the controlled enumeration and Native Mode tests.
- Four retained capture samples of the 227-byte configuration descriptor match
  the packaged file exactly. This means four samples were compared; it does not
  assert that they came from four different controllers.

DS4WSPKCOMP001 is a DS4Windows-created attach token that becomes part of the
Windows PnP instance identity. It is not a Sony serial descriptor, controller
serial, or USB/IP bus handle.

## 3. Recorded capture and transformation chain

The retained source fixture and the code that produced it are public in
[potpiemuncher/DS4Windows commit 6c0d39a39976e78e3ddf29b4b321d2a4599c9962](https://github.com/potpiemuncher/DS4Windows/commit/6c0d39a39976e78e3ddf29b4b321d2a4599c9962).
That commit is on the `feature/bt-audio-haptics` branch; it is retained source
evidence, not part of this pull request's ancestry and not a claim that
DSCompatProbe is included in this pull request.

The non-sensitive chain recorded by that fixture is:

1. On 2026-07-18, the project owner captured ordinary wired DualSense USB
   enumeration with USBPcap descriptor injection. The retained manifest names
   the source `hub3.pcap` and records its last-write timestamp as
   `2026-07-18T16:15:25.8988574Z`. The timestamp is a source-file timestamp, not
   an assertion about the exact instant at which enumeration completed.
2. DSCompatProbe's `freezeusb` command, as implemented at the exact source commit
   linked above, parsed the USBPcap control traffic. It wrote `device.bin`,
   `configuration.bin`, and the three `string-*.bin` files from the injected or
   captured descriptor responses rather than manually reconstructing them.
3. The 289-byte wire HID report descriptor was read from the controller through
   its parent hub, port 2, with
   `IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION`. Its length matches the HID
   descriptor embedded in the captured configuration.
4. DSCompatProbe's `verifyusb` command checked every manifest length and SHA-256,
   decoded the device and configuration descriptors, concatenated each decoded
   descriptor's raw bytes, and required that round trip to reproduce the
   original input. It also checked that the configuration-advertised HID report
   length matched `hid-report.bin`.
5. All six binaries currently packaged under
   `utils/VirtualDualSenseUsbip/Descriptors` match the retained public fixture's
   lengths and SHA-256 values exactly. They were copied byte-for-byte into the
   helper in
   [commit c11d7b6f729a172ac0d3f1d8d491381be462a92a](https://github.com/potpiemuncher/DS4Windows/commit/c11d7b6f729a172ac0d3f1d8d491381be462a92a).

The source fixture also contains a 489-byte
`hid-report-windows-reconstructed.bin`. DSCompatProbe labels it as a
HidSharp/Windows reconstruction that is not wire-exact and explicitly marks it
diagnostic-only. It is not one of the six hashes above, was not copied into the
helper, and must not be supplied to the USB emulator.

The retained manifest records a transient USB device address only to select the
captured traffic. That address is not present in any of the six packaged binary
assets and is unnecessary for review or reproduction. A raw USB capture, device
path, Windows username, machine log, serial, or controller address should not be
added to this pull request.

The production composite assets are consumed byte-for-byte. The helper also has
an offline HID-only scaffold that derives a 41-byte configuration in memory by
selecting and renumbering the HID interface. That derived self-test topology is
not one of the six captured assets and must be documented separately if it ever
becomes a user-facing mode.

## 4. Interoperability and policy questions

This dossier does not make a legal conclusion about copyright, trademark,
reverse engineering, or use of USB-IF identifiers. Maintainers should obtain
legal advice if their policy requires it.

### 4.1 Sony VID/PID

The virtual device presents Sony's USB-IF-assigned VID 054C and DualSense PID
0CE6 so games enter the native DualSense path. Current implementation and
validation depend on that identity. Using unrelated identifiers would require a
new compatibility study and is not assumed to work.

Maintainers must decide whether presenting another vendor's assigned identifiers
for interoperability fits project policy and what disclosure is required.

### 4.2 Manufacturer and product strings

The exact Sony manufacturer and DualSense product strings are retained for
byte-exact enumeration, and the product name also affects Windows endpoint
display names. The project has not established that a game requires either
string for native recognition.

Do not claim that changing the product string necessarily breaks games. If a
maintainer proposes changing it, run an offline enumeration comparison and a
separately approved compatibility test before drawing that conclusion.

### 4.3 Redistribution form

Maintainers must decide whether six checked-in binary descriptor responses are
an acceptable source and review format or whether a readable generated form plus
verified binary output is preferred. Generation must not silently change bytes
or conceal the captured origin.

### 4.4 Related protocol attribution

Descriptor capture is distinct from the surrounding DualSense protocol work.
Shipping notices should preserve the project's documented credits for relevant
public reverse-engineering efforts and the usbip-win2 license. The attribution
review should verify what was copied, adapted, or learned rather than treating
all protocol facts as descriptor provenance.

## 5. Maintainer checklist

- [ ] Recompute all six byte counts and full SHA-256 hashes.
- [ ] Independently decode device.bin and confirm iSerialNumber = 0.
- [ ] Independently decode all three string files.
- [ ] Confirm no address, serial, account, path, or gameplay data is present.
- [ ] Verify the six current files against the retained public fixture at commit
      6c0d39a39976e78e3ddf29b4b321d2a4599c9962.
- [ ] Review the `freezeusb`/`verifyusb` chain and confirm the 489-byte
      HidSharp/Windows reconstruction remains excluded.
- [ ] Record the project's VID/PID interoperability decision.
- [ ] Record the manufacturer/product-string decision without asserting
      unsupported game requirements.
- [ ] Approve the binary redistribution/review form.
- [ ] Verify all surrounding protocol and dependency attribution.
- [ ] Record sign-off or required changes in the pull-request review.

## 6. Current conclusion

The repository and retained public source commit establish the assets' capture
and transformation chain, contents, sizes, hashes, lack of a USB serial, and
absence of apparent personal or per-unit data. The six production assets are
byte-for-byte captured responses, not the excluded Windows reconstruction. The
remaining gate is maintainer policy review of Sony identity, strings, binary
redistribution form, and attribution; it is no longer a missing technical
chain-of-custody record.
