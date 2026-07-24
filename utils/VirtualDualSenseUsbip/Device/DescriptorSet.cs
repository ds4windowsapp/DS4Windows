// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace VirtualDualSenseUsbip.Device;

public sealed record UsbInterfaceDescriptorInfo(
    byte Number, byte Class, byte SubClass, byte Protocol);

public sealed record UsbEndpointDescriptorInfo(
    byte InterfaceNumber, byte AlternateSetting, byte Address,
    byte Attributes, ushort MaxPacketSize, byte Interval);

/// <summary>
/// Byte-exact descriptor store for the virtual wired DualSense, loaded from the
/// six reviewed descriptor assets shipped with this helper.
///
/// Serving raw captured bytes — rather than reconstructing descriptors from
/// parsed fields — guarantees the emulated device is indistinguishable from the
/// real controller at the descriptor level, which is the whole point of the
/// composite-emulation approach.
/// </summary>
public sealed class DescriptorSet
{
    private readonly byte[] device;
    private readonly byte[] configuration;
    private readonly byte[] hidReport;
    private readonly byte[] hidDescriptor;       // 9-byte 0x21 descriptor sliced from config
    private readonly Dictionary<byte, byte[]> strings; // string index -> descriptor bytes (0x0409)

    public byte HidInterfaceNumber { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public ushort DeviceBcd { get; }
    public byte NumConfigurations { get; }
    public byte NumInterfaces { get; }
    public byte ConfigurationDescriptorValue { get; }
    public int ConfigurationDescriptorLength => configuration.Length;
    public IReadOnlyList<UsbInterfaceDescriptorInfo> Interfaces { get; }
    public IReadOnlyList<UsbEndpointDescriptorInfo> Endpoints { get; }

    private DescriptorSet(byte[] device, byte[] configuration, byte[] hidReport,
        byte[] hidDescriptor, Dictionary<byte, byte[]> strings, byte hidInterfaceNumber)
    {
        this.device = device;
        this.configuration = configuration;
        this.hidReport = hidReport;
        this.hidDescriptor = hidDescriptor;
        this.strings = strings;
        HidInterfaceNumber = hidInterfaceNumber;

        VendorId = (ushort)(device[8] | (device[9] << 8));
        ProductId = (ushort)(device[10] | (device[11] << 8));
        DeviceBcd = (ushort)(device[12] | (device[13] << 8));
        NumConfigurations = device[17];
        NumInterfaces = configuration[4];
        ConfigurationDescriptorValue = configuration[5];
        (Interfaces, Endpoints) = ParseTopology(configuration, NumInterfaces);
    }

    public static DescriptorSet LoadFromFixtures(string fixturesDir)
    {
        byte[] Read(string name)
        {
            string path = Path.Combine(fixturesDir, name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Missing descriptor fixture: {path}");
            }

            return File.ReadAllBytes(path);
        }

        byte[] device = Read("device.bin");
        byte[] configuration = Read("configuration.bin");
        byte[] hidReport = Read("hid-report.bin");

        if (device.Length != 18 || device[1] != UsbDescriptorType.Device)
        {
            throw new InvalidDataException("device.bin is not an 18-byte device descriptor.");
        }

        byte[] hidDescriptor = SliceHidDescriptor(configuration, out byte hidInterface);

        var strings = new Dictionary<byte, byte[]>
        {
            [0] = Read("string-0-lang-0000.bin"),
            [1] = Read("string-1-lang-0409.bin"),
            [2] = Read("string-2-lang-0409.bin"),
        };

        return new DescriptorSet(device, configuration, hidReport, hidDescriptor, strings, hidInterface);
    }

    /// <summary>
    /// Loads the captured descriptors, then derives the HID-only configuration
    /// used by the loopback server self-test. The HID class/report descriptors
    /// and endpoint descriptors remain byte-exact; only the configuration's
    /// total length/interface count and HID interface number are rewritten.
    /// </summary>
    public static DescriptorSet LoadHidOnlyFromFixtures(string fixturesDir)
    {
        DescriptorSet captured = LoadFromFixtures(fixturesDir);
        byte[] hidOnlyConfiguration = BuildHidOnlyConfiguration(
            captured.configuration, captured.HidInterfaceNumber);

        return new DescriptorSet(
            captured.device.ToArray(),
            hidOnlyConfiguration,
            captured.hidReport.ToArray(),
            captured.hidDescriptor.ToArray(),
            captured.strings.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
            hidInterfaceNumber: 0);
    }

    private static byte[] BuildHidOnlyConfiguration(byte[] captured, byte hidInterfaceNumber)
    {
        if (captured.Length < 9 || captured[1] != UsbDescriptorType.Configuration)
        {
            throw new InvalidDataException("configuration.bin has no valid configuration header.");
        }

        int interfaceStart = -1;
        int interfaceEnd = captured.Length;
        int offset = captured[0];
        while (offset + 2 <= captured.Length)
        {
            int length = captured[offset];
            int type = captured[offset + 1];
            if (length < 2 || offset + length > captured.Length)
            {
                throw new InvalidDataException("Malformed descriptor in configuration.bin.");
            }

            if (type == UsbDescriptorType.Interface)
            {
                byte number = captured[offset + 2];
                if (interfaceStart >= 0)
                {
                    interfaceEnd = offset;
                    break;
                }

                if (number == hidInterfaceNumber)
                {
                    interfaceStart = offset;
                }
            }

            offset += length;
        }

        if (interfaceStart < 0)
        {
            throw new InvalidDataException("Captured HID interface was not found in configuration.bin.");
        }

        byte[] hidOnly = new byte[captured[0] + interfaceEnd - interfaceStart];
        captured.AsSpan(0, captured[0]).CopyTo(hidOnly);
        captured.AsSpan(interfaceStart, interfaceEnd - interfaceStart)
            .CopyTo(hidOnly.AsSpan(captured[0]));

        BinaryPrimitives.WriteUInt16LittleEndian(hidOnly.AsSpan(2, 2), checked((ushort)hidOnly.Length));
        hidOnly[4] = 1; // bNumInterfaces
        hidOnly[captured[0] + 2] = 0; // bInterfaceNumber
        return hidOnly;
    }

    private static (IReadOnlyList<UsbInterfaceDescriptorInfo> Interfaces,
        IReadOnlyList<UsbEndpointDescriptorInfo> Endpoints) ParseTopology(
        byte[] config, byte expectedInterfaceCount)
    {
        var interfaces = new Dictionary<byte, UsbInterfaceDescriptorInfo>();
        var endpoints = new List<UsbEndpointDescriptorInfo>();
        byte currentInterface = 0xFF;
        byte currentAlternate = 0;
        int offset = 0;
        while (offset + 2 <= config.Length)
        {
            int length = config[offset];
            int type = config[offset + 1];
            if (length < 2 || offset + length > config.Length)
            {
                throw new InvalidDataException("Malformed descriptor in configuration topology.");
            }

            if (type == UsbDescriptorType.Interface && length >= 9)
            {
                currentInterface = config[offset + 2];
                currentAlternate = config[offset + 3];
                interfaces.TryAdd(currentInterface, new UsbInterfaceDescriptorInfo(
                    currentInterface, config[offset + 5], config[offset + 6], config[offset + 7]));
            }
            else if (type == UsbDescriptorType.Endpoint && length >= 7 && currentInterface != 0xFF)
            {
                endpoints.Add(new UsbEndpointDescriptorInfo(
                    currentInterface,
                    currentAlternate,
                    config[offset + 2],
                    config[offset + 3],
                    BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(offset + 4, 2)),
                    config[offset + 6]));
            }

            offset += length;
        }

        if (interfaces.Count != expectedInterfaceCount)
        {
            throw new InvalidDataException(
                $"Configuration advertises {expectedInterfaceCount} interfaces but describes {interfaces.Count}.");
        }

        return (interfaces.Values.OrderBy(info => info.Number).ToArray(), endpoints);
    }

    /// <summary>
    /// Walks the configuration descriptor to find the HID class descriptor (0x21)
    /// and the interface number it belongs to. Keeps the served HID descriptor
    /// byte-identical to what the real device reports inside its config.
    /// </summary>
    private static byte[] SliceHidDescriptor(byte[] config, out byte hidInterface)
    {
        hidInterface = 0xFF;
        int offset = 0;
        byte currentInterface = 0xFF;
        while (offset + 2 <= config.Length)
        {
            int len = config[offset];
            int type = config[offset + 1];
            if (len == 0 || offset + len > config.Length)
            {
                break;
            }

            if (type == UsbDescriptorType.Interface)
            {
                currentInterface = config[offset + 2];
            }
            else if (type == UsbDescriptorType.Hid)
            {
                hidInterface = currentInterface;
                return config.AsSpan(offset, len).ToArray();
            }

            offset += len;
        }

        throw new InvalidDataException("No HID (0x21) descriptor found in configuration.bin.");
    }

    /// <summary>
    /// Serves a standard GET_DESCRIPTOR request. Returns a stall for descriptor
    /// types the real full-speed device does not provide (e.g. device qualifier).
    /// Every response is truncated to wLength, matching real EP0 behavior.
    /// </summary>
    public ControlResult GetDescriptor(UsbSetupPacket setup)
    {
        byte type = setup.DescriptorType;
        byte index = setup.DescriptorIndex;

        byte[]? source = type switch
        {
            UsbDescriptorType.Device => device,
            UsbDescriptorType.Configuration => configuration,
            UsbDescriptorType.Hid => hidDescriptor,
            UsbDescriptorType.HidReport => hidReport,
            UsbDescriptorType.String => strings.GetValueOrDefault(index),
            _ => null,
        };

        if (source == null)
        {
            return ControlResult.Stalled();
        }

        int take = Math.Min(source.Length, setup.Length);
        return ControlResult.Ok(source.AsSpan(0, take).ToArray());
    }
}
