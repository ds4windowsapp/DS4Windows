// SPDX-License-Identifier: GPL-3.0-or-later

namespace VirtualDualSenseUsbip.Device;

/// <summary>
/// EP0 control-transfer state machine for the virtual wired DualSense.
///
/// Treats EP0 as a stateful device (current configuration, per-interface alt
/// settings, idle rates) rather than a descriptor lookup table. Standard
/// descriptor requests are served byte-exact from <see cref="DescriptorSet"/>;
/// unsupported or malformed requests stall rather than fabricate success.
/// </summary>
public sealed class ControlEndpoint
{
    private readonly DescriptorSet descriptors;
    private readonly FeatureReportSet featureReports;
    private readonly Dictionary<byte, byte> interfaceAltSettings = new();
    private readonly HashSet<byte> advertisedInterfaces = new();
    private readonly HashSet<(byte InterfaceNumber, byte AlternateSetting)> advertisedAltSettings = new();
    private readonly HashSet<byte> audioControlInterfaces = new();
    private readonly Dictionary<byte, byte> idleRates = new();
    private readonly Dictionary<byte, byte> audioMuteStates = new();
    private readonly Dictionary<byte, short> audioVolumeStates = new();

    public byte ConfigurationValue { get; private set; }
    public bool SelfPowered { get; init; } = true; // config bmAttributes 0xC0

    /// <summary>Raised on SET_INTERFACE so the audio layer can (de)activate an
    /// isochronous streaming alt setting. (interface, altSetting).</summary>
    public event Action<byte, byte>? InterfaceAltChanged;

    /// <summary>Raised when the host changes an audio feature unit's mute or
    /// volume. Reports the combined result as (entityId, linearScale) where
    /// linearScale is 0 while muted and otherwise 10^(dB/20) for the
    /// UAC1 volume in 1/256 dB units.</summary>
    public event Action<byte, float>? AudioScaleChanged;

    public ControlEndpoint(DescriptorSet descriptors, FeatureReportSet? featureReports = null)
    {
        this.descriptors = descriptors;
        this.featureReports = featureReports ?? FeatureReportSet.CreateVirtualDefaults();

        foreach (UsbInterfaceDescriptorInfo descriptor in descriptors.Interfaces)
        {
            advertisedInterfaces.Add(descriptor.Number);
            advertisedAltSettings.Add((descriptor.Number, 0));
            interfaceAltSettings[descriptor.Number] = 0;

            if (descriptor.Class == 0x01 && descriptor.SubClass == 0x01)
            {
                audioControlInterfaces.Add(descriptor.Number);
            }
        }

        // DescriptorSet exposes one class record per interface. In the shipped
        // descriptors every non-zero alternate setting owns at least one
        // endpoint, so endpoint topology supplies the remaining valid alts.
        foreach (UsbEndpointDescriptorInfo endpoint in descriptors.Endpoints)
        {
            advertisedAltSettings.Add((endpoint.InterfaceNumber, endpoint.AlternateSetting));
        }
    }

    public byte GetAltSetting(byte interfaceNumber) =>
        interfaceAltSettings.GetValueOrDefault(interfaceNumber);

    internal void ResetInterfaceAltSettings()
    {
        foreach (byte interfaceNumber in interfaceAltSettings
            .Where(setting => setting.Value != 0)
            .Select(setting => setting.Key)
            .ToArray())
        {
            interfaceAltSettings[interfaceNumber] = 0;
            InterfaceAltChanged?.Invoke(interfaceNumber, 0);
        }
    }

    /// <summary>Handles one EP0 SETUP transaction. <paramref name="outData"/> is
    /// the host-to-device data stage payload (empty for IN transfers).</summary>
    public ControlResult Handle(UsbSetupPacket setup, ReadOnlySpan<byte> outData)
    {
        return setup.Type switch
        {
            UsbSetupPacket.TypeStandard => HandleStandard(setup),
            UsbSetupPacket.TypeClass => HandleClass(setup, outData),
            _ => ControlResult.Stalled(),
        };
    }

    private ControlResult HandleStandard(UsbSetupPacket setup)
    {
        switch (setup.Request)
        {
            case UsbStandardRequest.GetDescriptor:
                return descriptors.GetDescriptor(setup);

            case UsbStandardRequest.SetConfiguration:
                if (setup.DeviceToHost ||
                    setup.Recipient != UsbSetupPacket.RecipientDevice ||
                    setup.Index != 0 || setup.Length != 0 ||
                    setup.Value > byte.MaxValue ||
                    (setup.Value != 0 &&
                     setup.Value != descriptors.ConfigurationDescriptorValue))
                {
                    return ControlResult.Stalled();
                }

                ConfigurationValue = (byte)setup.Value;
                ResetInterfaceAltSettings();
                return ControlResult.Ack();

            case UsbStandardRequest.GetConfiguration:
                return ControlResult.Ok(new[] { ConfigurationValue });

            case UsbStandardRequest.SetInterface:
            {
                if (setup.DeviceToHost ||
                    setup.Recipient != UsbSetupPacket.RecipientInterface ||
                    setup.Index > byte.MaxValue || setup.Value > byte.MaxValue ||
                    setup.Length != 0 ||
                    !advertisedAltSettings.Contains(((byte)setup.Index, (byte)setup.Value)))
                {
                    return ControlResult.Stalled();
                }

                byte interfaceNumber = (byte)setup.Index;
                byte alt = (byte)setup.Value;
                interfaceAltSettings[interfaceNumber] = alt;
                InterfaceAltChanged?.Invoke(interfaceNumber, alt);
                return ControlResult.Ack();
            }

            case UsbStandardRequest.GetInterface:
                if (!setup.DeviceToHost ||
                    setup.Recipient != UsbSetupPacket.RecipientInterface ||
                    setup.Value != 0 || setup.Index > byte.MaxValue ||
                    setup.Length != 1 ||
                    !advertisedInterfaces.Contains((byte)setup.Index))
                {
                    return ControlResult.Stalled();
                }

                return ControlResult.Ok(new[] { GetAltSetting((byte)setup.Index) });

            case UsbStandardRequest.GetStatus:
                return HandleGetStatus(setup);

            case UsbStandardRequest.SetAddress:
                // The virtual host controller owns addressing; accept harmlessly.
                return ControlResult.Ack();

            case UsbStandardRequest.ClearFeature:
            case UsbStandardRequest.SetFeature:
                // Accept ENDPOINT_HALT / remote-wake toggles; nothing to persist
                // beyond acknowledging them.
                return ControlResult.Ack();

            default:
                return ControlResult.Stalled();
        }
    }

    private ControlResult HandleGetStatus(UsbSetupPacket setup)
    {
        ushort status = setup.Recipient switch
        {
            // D0 self-powered, D1 remote-wake (report self-powered per config 0xC0).
            UsbSetupPacket.RecipientDevice => (ushort)(SelfPowered ? 0x0001 : 0x0000),
            _ => 0x0000, // interface reserved-zero; endpoint halt is not persisted
        };

        return ControlResult.Ok(new[] { (byte)(status & 0xFF), (byte)(status >> 8) });
    }

    private ControlResult HandleClass(UsbSetupPacket setup, ReadOnlySpan<byte> outData)
    {
        if (setup.Recipient != UsbSetupPacket.RecipientInterface)
        {
            return ControlResult.Stalled();
        }

        // HID wIndex is exactly the interface number. UAC1 uses the low byte
        // for the interface and the high byte for the entity ID.
        if (setup.Index == descriptors.HidInterfaceNumber)
        {
            return HandleHidClass(setup);
        }

        if (audioControlInterfaces.Contains((byte)setup.Index))
        {
            return HandleAudioClass(setup, outData);
        }

        return ControlResult.Stalled();
    }

    private ControlResult HandleHidClass(UsbSetupPacket setup)
    {
        switch (setup.Request)
        {
            case UsbHidRequest.SetIdle:
                idleRates[(byte)(setup.Value & 0xFF)] = (byte)(setup.Value >> 8);
                return ControlResult.Ack();

            case UsbHidRequest.GetIdle:
                return ControlResult.Ok(new[] { idleRates.GetValueOrDefault((byte)(setup.Value & 0xFF)) });

            case UsbHidRequest.SetProtocol:
            case UsbHidRequest.SetReport:
                // Output reports received through the interrupt endpoint are
                // relayed separately; acknowledge class writes with no data stage.
                return ControlResult.Ack();

            case UsbHidRequest.GetProtocol:
                return ControlResult.Ok(new byte[] { 1 }); // report protocol

            case UsbHidRequest.GetReport:
                // Only the captured/sanitized feature reports in FeatureReportSet
                // are served. Calibration (0x05) is available only when supplied
                // from a real pad at runtime; never invent sensor calibration.
                return featureReports.Get(setup);

            default:
                return ControlResult.Stalled();
        }
    }

    private ControlResult HandleAudioClass(UsbSetupPacket setup, ReadOnlySpan<byte> outData)
    {
        const byte SetCurrent = 0x01;
        const byte GetCurrent = 0x81;
        const byte GetMinimum = 0x82;
        const byte GetMaximum = 0x83;
        const byte GetResolution = 0x84;
        const byte MuteControlSelector = 0x01;
        const byte VolumeControlSelector = 0x02;
        const short VolumeMinimum = -100 * 256;
        const short VolumeMaximum = 0;
        const short VolumeResolution = 1 * 256;

        byte interfaceNumber = (byte)setup.Index;
        byte entityId = (byte)(setup.Index >> 8);
        byte controlSelector = (byte)(setup.Value >> 8);
        bool knownFeatureUnit = interfaceNumber == 0 && entityId is 2 or 5;
        if (!knownFeatureUnit)
        {
            return ControlResult.Stalled();
        }

        if (controlSelector == MuteControlSelector && setup.Length == 1)
        {
            if (setup.Request == GetCurrent && setup.DeviceToHost)
            {
                return ControlResult.Ok(new[] { audioMuteStates.GetValueOrDefault(entityId) });
            }
            if (setup.Request == SetCurrent && !setup.DeviceToHost && outData.Length >= 1)
            {
                audioMuteStates[entityId] = (byte)(outData[0] & 0x01);
                NotifyAudioScale(entityId);
                return ControlResult.Ack();
            }
        }

        if (controlSelector == VolumeControlSelector && setup.Length == 2)
        {
            if (setup.Request == GetCurrent && setup.DeviceToHost)
            {
                return AudioVolumeResult(audioVolumeStates.GetValueOrDefault(entityId));
            }
            if (setup.DeviceToHost)
            {
                short? value = setup.Request switch
                {
                    GetMinimum => VolumeMinimum,
                    GetMaximum => VolumeMaximum,
                    GetResolution => VolumeResolution,
                    _ => null,
                };
                if (value.HasValue)
                {
                    return AudioVolumeResult(value.Value);
                }
            }
            if (setup.Request == SetCurrent && !setup.DeviceToHost && outData.Length >= 2)
            {
                audioVolumeStates[entityId] = unchecked((short)(outData[0] | (outData[1] << 8)));
                NotifyAudioScale(entityId);
                return ControlResult.Ack();
            }
        }

        return ControlResult.Stalled();
    }

    private void NotifyAudioScale(byte entityId)
    {
        float scale;
        if (audioMuteStates.GetValueOrDefault(entityId) != 0)
        {
            scale = 0f;
        }
        else
        {
            double decibels = audioVolumeStates.GetValueOrDefault(entityId) / 256.0;
            scale = (float)Math.Clamp(Math.Pow(10.0, decibels / 20.0), 0.0, 1.0);
        }
        AudioScaleChanged?.Invoke(entityId, scale);
    }

    private static ControlResult AudioVolumeResult(short volume) =>
        ControlResult.Ok(new[]
        {
            (byte)(volume & 0xFF),
            (byte)((volume >> 8) & 0xFF),
        });
}
