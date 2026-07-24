// SPDX-License-Identifier: GPL-3.0-or-later

namespace VirtualDualSenseUsbip.Live;

/// <summary>
/// Optional audio coupling between the USB/IP session and an input source that
/// can also carry audio (the Bluetooth DualSense bridge). The session reports
/// interface/endpoint lifecycle and host volume state, and drains microphone
/// PCM for isochronous IN completions.
/// </summary>
public interface IUsbAudioRelay
{
    /// <summary>The host opened (alt 1) or closed (alt 0) the playback
    /// streaming interface. Governs the continuous 0x36 audio stream.</summary>
    void SetPlaybackInterfaceActive(bool active);

    /// <summary>The host opened (alt 1) or closed (alt 0) the capture
    /// streaming interface. Gates the physical controller microphone.</summary>
    void SetCaptureInterfaceActive(bool active);

    /// <summary>Host-set playback volume as a linear factor (0..1); 0 when muted.</summary>
    void SetPlaybackVolume(float linear);

    /// <summary>Host-set capture volume as a linear factor (0..1); 0 when muted.</summary>
    void SetCaptureVolume(float linear);

    /// <summary>
    /// Fills one isochronous IN packet with interleaved stereo signed 16-bit
    /// little-endian 48 kHz microphone PCM. The destination length is the
    /// endpoint's packet capacity; <paramref name="nominalBytes"/> is one
    /// USB frame of audio at the nominal rate. The implementation returns the
    /// byte count it chose (it may deliver slightly more or less than nominal
    /// to keep its ring centered — the endpoint is asynchronous, so the device
    /// legitimately owns the sample clock). Returns 0 to deliver silence.
    /// </summary>
    int FillMicrophonePacket(Span<byte> destination, int nominalBytes);
}
