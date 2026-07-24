// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace VirtualDualSenseUsbip.Protocol;

public static class UsbIpCodec
{
    public const int MaxTransferLength = 4 * 1024 * 1024;
    public const int MaxIsoPackets = 8192;

    public static async ValueTask<byte[]> ReadExactlyAsync(Stream stream, int length,
        CancellationToken cancellationToken = default)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        byte[] bytes = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException($"Expected {length} bytes, received {offset}.");
            offset += read;
        }
        return bytes;
    }

    public static async ValueTask<UsbIpOperation> ReadOperationAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = await ReadExactlyAsync(stream, 8, cancellationToken);
        ushort version = U16(header, 0);
        ushort code = U16(header, 2);
        uint status = U32(header, 4);
        string? busId = code == UsbIpConstants.OpReqImport
            ? DecodeFixedString(await ReadExactlyAsync(stream, 32, cancellationToken)) : null;
        return new UsbIpOperation(version, code, status, busId);
    }

    public static async ValueTask<UsbIpCommand> ReadCommandAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = await ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength, cancellationToken);
        var basic = new UsbIpBasicHeader(U32(header, 0), U32(header, 4), U32(header, 8),
            U32(header, 12), U32(header, 16));
        if (basic.Command == UsbIpConstants.CmdUnlink)
            return new UsbIpUnlink(basic, U32(header, 20));
        if (basic.Command != UsbIpConstants.CmdSubmit)
            throw new InvalidDataException($"Unknown USB/IP command 0x{basic.Command:X8}.");

        int transferLength = I32(header, 24);
        int packetCount = I32(header, 32);
        if (transferLength < 0 || transferLength > MaxTransferLength)
            throw new InvalidDataException($"Invalid transfer length {transferLength}.");
        if (packetCount < -1 || packetCount > MaxIsoPackets)
            throw new InvalidDataException($"Invalid ISO packet count {packetCount}.");
        if (basic.Direction > UsbIpConstants.DirectionIn || basic.Endpoint > 15)
            throw new InvalidDataException("Invalid direction or endpoint.");

        byte[] payload = basic.Direction == UsbIpConstants.DirectionOut && transferLength > 0
            ? await ReadExactlyAsync(stream, transferLength, cancellationToken) : Array.Empty<byte>();
        var iso = new List<UsbIpIsoPacket>(Math.Max(0, packetCount));
        if (packetCount > 0)
        {
            byte[] descriptors = await ReadExactlyAsync(stream,
                checked(packetCount * UsbIpConstants.IsoDescriptorLength), cancellationToken);
            for (int i = 0; i < packetCount; i++)
            {
                int o = i * UsbIpConstants.IsoDescriptorLength;
                iso.Add(new UsbIpIsoPacket(U32(descriptors, o), U32(descriptors, o + 4),
                    U32(descriptors, o + 8), I32(descriptors, o + 12)));
            }
            ValidateIsoPackets(transferLength, iso);
        }
        return new UsbIpSubmit(basic, U32(header, 20), transferLength, I32(header, 28),
            packetCount, I32(header, 36), header.AsSpan(40, 8).ToArray(), payload, iso);
    }

    public static byte[] EncodeOperationReply(ushort code, uint status,
        UsbIpDeviceInfo? device = null, bool includeInterfaceList = false)
    {
        using var output = new MemoryStream();
        WriteU16(output, UsbIpConstants.Version); WriteU16(output, code); WriteU32(output, status);
        if (status != 0) return output.ToArray();
        if (code == UsbIpConstants.OpRepDevList)
            WriteU32(output, device == null ? 0u : 1u);
        if (device != null)
        {
            WriteDevice(output, device);
            if (includeInterfaceList)
            {
                foreach (UsbIpInterfaceInfo iface in device.Interfaces)
                {
                    output.WriteByte(iface.Class); output.WriteByte(iface.SubClass);
                    output.WriteByte(iface.Protocol); output.WriteByte(0);
                }
            }
        }
        return output.ToArray();
    }

    public static byte[] Encode(UsbIpSubmitReply reply)
    {
        using var output = new MemoryStream();
        WriteBasic(output, UsbIpConstants.RetSubmit, reply.SequenceNumber);
        WriteI32(output, reply.Status); WriteI32(output, reply.ActualLength);
        WriteI32(output, reply.StartFrame); WriteI32(output, reply.NumberOfPackets);
        WriteI32(output, reply.ErrorCount); WriteU32(output, 0); WriteU32(output, 0);
        output.Write(reply.TransferBuffer);
        foreach (UsbIpIsoPacket packet in reply.IsoPackets)
        {
            WriteU32(output, packet.Offset); WriteU32(output, packet.Length);
            WriteU32(output, packet.ActualLength); WriteI32(output, packet.Status);
        }
        return output.ToArray();
    }

    public static byte[] Encode(UsbIpUnlinkReply reply)
    {
        using var output = new MemoryStream();
        WriteBasic(output, UsbIpConstants.RetUnlink, reply.SequenceNumber);
        WriteI32(output, reply.Status);
        output.Write(new byte[24]);
        return output.ToArray();
    }

    private static void ValidateIsoPackets(int transferLength, IReadOnlyList<UsbIpIsoPacket> packets)
    {
        foreach (UsbIpIsoPacket packet in packets)
        {
            if (packet.Offset > transferLength || packet.Length > transferLength - packet.Offset)
                throw new InvalidDataException("ISO packet range exceeds transfer buffer.");
        }
    }

    private static void WriteDevice(Stream stream, UsbIpDeviceInfo d)
    {
        WriteFixedString(stream, d.Path, 256); WriteFixedString(stream, d.BusId, 32);
        WriteU32(stream, d.BusNumber); WriteU32(stream, d.DeviceNumber); WriteU32(stream, d.Speed);
        WriteU16(stream, d.VendorId); WriteU16(stream, d.ProductId); WriteU16(stream, d.DeviceBcd);
        stream.WriteByte(d.DeviceClass); stream.WriteByte(d.DeviceSubClass); stream.WriteByte(d.DeviceProtocol);
        stream.WriteByte(d.ConfigurationValue); stream.WriteByte(d.NumberOfConfigurations);
        stream.WriteByte(d.NumberOfInterfaces);
    }

    private static void WriteBasic(Stream stream, uint command, uint sequence)
    {
        WriteU32(stream, command); WriteU32(stream, sequence);
        WriteU32(stream, 0); WriteU32(stream, 0); WriteU32(stream, 0);
    }

    private static void WriteFixedString(Stream stream, string value, int length)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(value);
        if (encoded.Length >= length) throw new InvalidDataException($"String exceeds {length - 1} bytes.");
        stream.Write(encoded); stream.Write(new byte[length - encoded.Length]);
    }

    private static string DecodeFixedString(byte[] bytes)
    {
        int nul = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, nul < 0 ? bytes.Length : nul);
    }

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    private static int I32(byte[] b, int o) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o, 4));
    private static void WriteU16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    private static void WriteI32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
}

public sealed class SerializedStreamWriter(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync() { gate.Dispose(); return ValueTask.CompletedTask; }
}
