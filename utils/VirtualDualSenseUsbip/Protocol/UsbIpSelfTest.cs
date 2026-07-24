// SPDX-License-Identifier: GPL-3.0-or-later

namespace VirtualDualSenseUsbip.Protocol;

public static class UsbIpSelfTest
{
    private const string CmdInterruptIn =
        "0000000100000d050001000f00000001000000010000020000000040ffffffff00000000000000040000000000000000";
    private const string CmdInterruptOut =
        "0000000100000d060001000f00000000000000010000000000000040ffffffff00000000000000040000000000000000" +
        "ffffffff860008a784ce5ae212376300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string RetInterruptOut =
        "0000000300000d060000000000000000000000000000000000000040ffffffff00000000000000000000000000000000";

    public static async Task RunAsync()
    {
        Console.WriteLine("selftest: parse interrupt IN golden vector");
        await ParseGoldenSubmitAsync(CmdInterruptIn, UsbIpConstants.DirectionIn, 0x40, 0);
        Console.WriteLine("selftest: parse interrupt OUT golden vector");
        await ParseGoldenSubmitAsync(CmdInterruptOut, UsbIpConstants.DirectionOut, 0x40, 0x40);

        Console.WriteLine("selftest: encode RET_SUBMIT golden vector");
        byte[] encoded = UsbIpCodec.Encode(new UsbIpSubmitReply(0x0D06, 0, 0x40,
            -1, 0, 0, Array.Empty<byte>(), Array.Empty<UsbIpIsoPacket>()));
        Require(Convert.ToHexString(encoded).Equals(RetInterruptOut, StringComparison.OrdinalIgnoreCase),
            "RET_SUBMIT golden vector mismatch");

        Console.WriteLine("selftest: fragmented OP_REQ_IMPORT");
        byte[] import = Convert.FromHexString("0111800300000000" +
            Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("1-1")) + new string('0', 58));
        await using var fragmented = new FragmentedReadStream(import, 1, 2, 3);
        UsbIpOperation operation = await UsbIpCodec.ReadOperationAsync(fragmented);
        Require(operation is { Version: 0x0111, Code: 0x8003, Status: 0, BusId: "1-1" },
            "fragmented OP_REQ_IMPORT parse failed");

        Console.WriteLine("selftest: management device records");
        var interfaces = new[]
        {
            new UsbIpInterfaceInfo(1, 1, 0), new UsbIpInterfaceInfo(1, 2, 0),
            new UsbIpInterfaceInfo(1, 2, 0), new UsbIpInterfaceInfo(3, 0, 0),
        };
        var device = new UsbIpDeviceInfo("/virtual/ds4windows/dualsense", "1-1", 1, 1, 3,
            0x054C, 0x0CE6, 0x0100, 0, 0, 0, 1, 1, 4, interfaces);
        byte[] devList = UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepDevList, 0,
            device, includeInterfaceList: true);
        byte[] importReply = UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepImport, 0, device);
        Require(devList.Length == 12 + 312 + 16 && importReply.Length == 8 + 312,
            "management device record size mismatch");
        Require(devList.AsSpan(0, 12).SequenceEqual(Convert.FromHexString("011100050000000000000001")),
            "OP_REP_DEVLIST header mismatch");

        Console.WriteLine("selftest: CMD_UNLINK and RET_UNLINK");
        byte[] unlinkCommand = Convert.FromHexString(
            "000000020000123400010001000000000000000000000d06000000000000000000000000000000000000000000000000");
        UsbIpUnlink unlink = (UsbIpUnlink)await UsbIpCodec.ReadCommandAsync(new MemoryStream(unlinkCommand));
        Require(unlink.UnlinkSequenceNumber == 0x0D06 && unlink.Basic.SequenceNumber == 0x1234,
            "CMD_UNLINK parse mismatch");
        byte[] unlinkReply = UsbIpCodec.Encode(new UsbIpUnlinkReply(0x1234, -104));
        Require(unlinkReply.Length == 48 && unlinkReply.AsSpan(0, 8)
            .SequenceEqual(Convert.FromHexString("0000000400001234")), "RET_UNLINK encode mismatch");
        Console.WriteLine("PASS: USB/IP golden vectors and fragmented exact-read transport.");
    }

    private static async Task ParseGoldenSubmitAsync(string hex, uint direction,
        int requestedLength, int payloadLength)
    {
        await using var stream = new FragmentedReadStream(Convert.FromHexString(hex), 1, 7, 2, 13);
        UsbIpSubmit submit = (UsbIpSubmit)await UsbIpCodec.ReadCommandAsync(stream);
        Require(submit.Basic.Direction == direction && submit.TransferBufferLength == requestedLength &&
            submit.TransferBuffer.Length == payloadLength && submit.Basic.Endpoint == 1,
            "CMD_SUBMIT golden vector mismatch");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FragmentedReadStream(byte[] bytes, params int[] chunkSizes) : MemoryStream(bytes)
    {
        private int chunk;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int max = chunkSizes[chunk++ % chunkSizes.Length];
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, max)], cancellationToken);
        }
    }
}
