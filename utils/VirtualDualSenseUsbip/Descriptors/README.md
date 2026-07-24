# DualSense descriptor assets

These six files are the byte-exact public USB descriptors returned by a retail
Sony DualSense with VID `054C` and PID `0CE6`. They are kept as data so the
virtual composite device enumerates with the same HID and USB Audio topology.

The device descriptor sets `iSerialNumber` to zero. The string assets contain
only the standard language, manufacturer (`Sony Interactive Entertainment`),
and product (`DualSense Wireless Controller`) descriptors; no controller
address, serial number, username, capture path, or gameplay data is present.

SHA-256:

```text
A4C5E13FF088B3642DD2CE735EAC5207A84F2B343BB9A41095F1D6FD806DCCBF  configuration.bin
22927B1C25944C109955431846621306A4C91DB7A2AE5D92E740AF5561E18BB6  device.bin
4F48767516627510521512AF13C20A064877465122B7BCC7CAC1F3B608D37994  hid-report.bin
2BF9E7166F6DCE93C04C256E2163F634EE0E7D8E5B8EFB029DF785BAA7E4C2F2  string-0-lang-0000.bin
1E2A8747B339E7B139DA98BAE94358E0BCF4C80F386E4B2AC1042E41EF9BD155  string-1-lang-0409.bin
7202A38073AD3A97FFFFCCD5691FF80FA976E11139DE71A9844DD5C3C2C90C9A  string-2-lang-0409.bin
```

Their inclusion and Sony VID/PID impersonation require maintainer/provenance
review before Native Mode can move beyond an experimental draft.
