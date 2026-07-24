// SPDX-License-Identifier: GPL-3.0-or-later

namespace VirtualDualSenseUsbip.Protocol;

public static class UsbIpConstants
{
    public const ushort Version = 0x0111;
    public const ushort OpReqImport = 0x8003;
    public const ushort OpRepImport = 0x0003;
    public const ushort OpReqDevList = 0x8005;
    public const ushort OpRepDevList = 0x0005;
    public const uint CmdSubmit = 0x00000001;
    public const uint CmdUnlink = 0x00000002;
    public const uint RetSubmit = 0x00000003;
    public const uint RetUnlink = 0x00000004;
    public const uint DirectionOut = 0;
    public const uint DirectionIn = 1;
    public const int UrbHeaderLength = 48;
    public const int IsoDescriptorLength = 16;
}

public sealed record UsbIpOperation(ushort Version, ushort Code, uint Status, string? BusId = null);

public sealed record UsbIpBasicHeader(uint Command, uint SequenceNumber, uint DeviceId,
    uint Direction, uint Endpoint);

public sealed record UsbIpIsoPacket(uint Offset, uint Length, uint ActualLength, int Status);

public abstract record UsbIpCommand(UsbIpBasicHeader Basic);

public sealed record UsbIpSubmit(UsbIpBasicHeader Basic, uint TransferFlags,
    int TransferBufferLength, int StartFrame, int NumberOfPackets, int Interval,
    byte[] Setup, byte[] TransferBuffer, IReadOnlyList<UsbIpIsoPacket> IsoPackets)
    : UsbIpCommand(Basic);

public sealed record UsbIpUnlink(UsbIpBasicHeader Basic, uint UnlinkSequenceNumber)
    : UsbIpCommand(Basic);

public sealed record UsbIpSubmitReply(uint SequenceNumber, int Status, int ActualLength,
    int StartFrame, int NumberOfPackets, int ErrorCount, byte[] TransferBuffer,
    IReadOnlyList<UsbIpIsoPacket> IsoPackets);

public sealed record UsbIpUnlinkReply(uint SequenceNumber, int Status);

public sealed record UsbIpDeviceInfo(string Path, string BusId, uint BusNumber,
    uint DeviceNumber, uint Speed, ushort VendorId, ushort ProductId, ushort DeviceBcd,
    byte DeviceClass, byte DeviceSubClass, byte DeviceProtocol, byte ConfigurationValue,
    byte NumberOfConfigurations, byte NumberOfInterfaces,
    IReadOnlyList<UsbIpInterfaceInfo> Interfaces);

public sealed record UsbIpInterfaceInfo(byte Class, byte SubClass, byte Protocol);
