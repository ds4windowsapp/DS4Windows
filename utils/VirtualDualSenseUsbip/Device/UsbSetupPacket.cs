// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace VirtualDualSenseUsbip.Device;

/// <summary>
/// The 8-byte USB SETUP packet embedded in a control-transfer CMD_SUBMIT.
///
/// Note the endianness split: the surrounding USB/IP PDU fields are big-endian,
/// but this packet is raw USB wire format and therefore little-endian.
/// </summary>
public readonly record struct UsbSetupPacket(
    byte RequestType, byte Request, ushort Value, ushort Index, ushort Length)
{
    public static UsbSetupPacket Parse(ReadOnlySpan<byte> setup)
    {
        if (setup.Length < 8)
        {
            throw new ArgumentException("SETUP packet must be 8 bytes.", nameof(setup));
        }

        return new UsbSetupPacket(
            setup[0],
            setup[1],
            BinaryPrimitives.ReadUInt16LittleEndian(setup.Slice(2, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(setup.Slice(4, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(setup.Slice(6, 2)));
    }

    // bmRequestType decomposition
    public bool DeviceToHost => (RequestType & 0x80) != 0;         // IN when set
    public int Type => (RequestType >> 5) & 0x03;                  // 0 std, 1 class, 2 vendor
    public int Recipient => RequestType & 0x1F;                    // 0 device, 1 interface, 2 endpoint

    public const int TypeStandard = 0;
    public const int TypeClass = 1;
    public const int TypeVendor = 2;

    public const int RecipientDevice = 0;
    public const int RecipientInterface = 1;
    public const int RecipientEndpoint = 2;

    public byte DescriptorType => (byte)(Value >> 8);
    public byte DescriptorIndex => (byte)(Value & 0xFF);
}

public static class UsbStandardRequest
{
    public const byte GetStatus = 0;
    public const byte ClearFeature = 1;
    public const byte SetFeature = 3;
    public const byte SetAddress = 5;
    public const byte GetDescriptor = 6;
    public const byte SetDescriptor = 7;
    public const byte GetConfiguration = 8;
    public const byte SetConfiguration = 9;
    public const byte GetInterface = 10;
    public const byte SetInterface = 11;
    public const byte SynchFrame = 12;
}

public static class UsbHidRequest
{
    public const byte GetReport = 1;
    public const byte GetIdle = 2;
    public const byte GetProtocol = 3;
    public const byte SetReport = 9;
    public const byte SetIdle = 10;
    public const byte SetProtocol = 11;
}

public static class UsbDescriptorType
{
    public const byte Device = 1;
    public const byte Configuration = 2;
    public const byte String = 3;
    public const byte Interface = 4;
    public const byte Endpoint = 5;
    public const byte DeviceQualifier = 6;
    public const byte Hid = 0x21;
    public const byte HidReport = 0x22;
}

/// <summary>
/// Result of an EP0 control request. <see cref="Status"/> 0 is success; a
/// negative Linux errno (e.g. -EPIPE) requests the endpoint be stalled, which
/// the USB/IP layer surfaces as the RET_SUBMIT status.
/// </summary>
public readonly record struct ControlResult(int Status, byte[] Data)
{
    public const int Stall = -32; // -EPIPE

    public static ControlResult Ok(byte[] data) => new(0, data);
    public static ControlResult Ack() => new(0, Array.Empty<byte>());
    public static ControlResult Stalled() => new(Stall, Array.Empty<byte>());
}
