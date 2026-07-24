// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using VirtualDualSenseUsbip.Device;
using VirtualDualSenseUsbip.Protocol;

namespace VirtualDualSenseUsbip.Live;

public static class LiveServerSelfTest
{
    private const uint DeviceId = 0x00010001;

    public static async Task RunAsync(string fixturesDir)
    {
        Console.WriteLine("servertest: exact Bluetooth controller identity");
        TestBluetoothControllerIdentity();

        Console.WriteLine("servertest: Bluetooth-to-USB input conversion");
        TestBluetoothInputConversion();

        Console.WriteLine("servertest: derive HID-only configuration");
        DescriptorSet descriptors = DescriptorSet.LoadHidOnlyFromFixtures(fixturesDir);
        Require(descriptors.NumInterfaces == 1 && descriptors.HidInterfaceNumber == 0,
            "HID-only descriptor set did not expose exactly interface zero");
        Require(descriptors.ConfigurationDescriptorLength == 41,
            $"unexpected HID-only configuration length {descriptors.ConfigurationDescriptorLength}");
        Require(descriptors.Interfaces.Count == 1 &&
                descriptors.Interfaces[0] == new UsbInterfaceDescriptorInfo(0, 3, 0, 0),
            "HID-only topology metadata mismatch");

        Console.WriteLine("servertest: exact composite topology");
        DescriptorSet composite = DescriptorSet.LoadFromFixtures(fixturesDir);
        Require(composite.ConfigurationDescriptorLength == 227 &&
                composite.NumInterfaces == 4 && composite.Interfaces.Count == 4,
            "composite descriptor set did not preserve all four interfaces");
        Require(composite.Interfaces[0] == new UsbInterfaceDescriptorInfo(0, 1, 1, 0) &&
                composite.Interfaces[1] == new UsbInterfaceDescriptorInfo(1, 1, 2, 0) &&
                composite.Interfaces[2] == new UsbInterfaceDescriptorInfo(2, 1, 2, 0) &&
                composite.Interfaces[3] == new UsbInterfaceDescriptorInfo(3, 3, 0, 0),
            "composite interface class topology mismatch");
        Require(composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(1, 1, 0x01, 0x09, 392, 4)) &&
                composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(2, 1, 0x82, 0x05, 196, 4)) &&
                composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(3, 0, 0x84, 0x03, 64, 6)) &&
                composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(3, 0, 0x03, 0x03, 64, 6)),
            "composite endpoint topology mismatch");
        await TestCompositeDeviceListAsync(composite);
        Console.WriteLine("servertest: shutdown import gate");
        await TestShutdownImportGateAsync(descriptors);

        using var server = new VirtualDualSenseServer(descriptors,
            new VirtualDualSenseServerOptions(Port: 0,
                InputInterval: TimeSpan.FromMilliseconds(200)));
        var captured = new TaskCompletionSource<HidOutputCapture>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.HidOutputReceived += output => captured.TrySetResult(output);
        server.Start();

        using var cancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cancellation.Token);
        try
        {
            Console.WriteLine("servertest: OP_REQ_DEVLIST");
            await TestDeviceListAsync(server.Port, descriptors.Interfaces);

            Console.WriteLine("servertest: OP_REQ_IMPORT + EP0 + interrupt endpoints");
            await TestImportSessionAsync(server.Port, captured.Task);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Console.WriteLine("PASS: live USB/IP management, HID-only EP0, interrupt IN/OUT, " +
            "packet-preserving ISO OUT, and UNLINK.");
    }

    private static async Task TestShutdownImportGateAsync(
        DescriptorSet descriptors)
    {
        using (var server = new VirtualDualSenseServer(descriptors,
            new VirtualDualSenseServerOptions(Port: 0)))
        {
            server.Start();
            Task serverTask = server.RunAsync();
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync("127.0.0.1", server.Port);
            await WaitUntilAsync(
                () => server.AcceptedClientCount == 1,
                "the pre-import client was not accepted");

            server.StopAccepting();
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(
                EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));
            byte[] rejected = await UsbIpCodec.ReadExactlyAsync(
                stream, 8);
            Require(U16(rejected, 2) == UsbIpConstants.OpRepImport &&
                    U32(rejected, 4) != 0,
                "an import reserved after StopAccepting was not rejected");
            Require(!server.RequiresHealthyRenderLeaseForShutdown,
                "a rejected pre-commit import required a render lease");

            client.Dispose();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }

        using (var server = new VirtualDualSenseServer(descriptors,
            new VirtualDualSenseServerOptions(Port: 0)))
        {
            server.Start();
            Task serverTask = server.RunAsync();
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync("127.0.0.1", server.Port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(
                EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));
            byte[] reply = await UsbIpCodec.ReadExactlyAsync(
                stream, 8 + 312);
            Require(U16(reply, 2) == UsbIpConstants.OpRepImport &&
                    U32(reply, 4) == 0,
                "the shutdown-gate import did not commit");
            Require(server.RequiresHealthyRenderLeaseForShutdown,
                "a committed import did not require a render lease");

            server.StopAccepting();
            using (var secondClient = new TcpClient())
            using (var connectTimeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(500)))
            {
                bool connected = false;
                try
                {
                    await secondClient.ConnectAsync(
                        "127.0.0.1", server.Port, connectTimeout.Token);
                    connected = true;
                }
                catch (Exception ex) when (
                    ex is SocketException or OperationCanceledException)
                {
                }
                Require(!connected,
                    "a second attach connected after StopAccepting");
            }

            client.Dispose();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            Require(server.RequiresHealthyRenderLeaseForShutdown,
                "a committed import lost its sticky safety state after EOF");
        }

        using (var server = new VirtualDualSenseServer(descriptors,
            new VirtualDualSenseServerOptions(Port: 0)))
        {
            server.Start();
            Task serverTask = server.RunAsync();
            server.StopAccepting();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        string failureMessage)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow +
            TimeSpan.FromSeconds(2);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(5);
        Require(predicate(), failureMessage);
    }

    private static void TestBluetoothControllerIdentity()
    {
        const string devicePath =
            @"\\?\hid#vid_054c&pid_0ce6#selected-controller";
        var identity = new BluetoothDualSenseIdentity(
            devicePath, 0x054C, 0x0CE6);

        BluetoothDualSenseInputSource.ValidateExpectedIdentity(identity);
        Require(BluetoothDualSenseInputSource.MatchesExpectedIdentity(
                identity, $"  {devicePath.ToUpperInvariant()}  ",
                0x054C, 0x0CE6),
            "case-insensitive exact HID identity did not match");
        Require(!BluetoothDualSenseInputSource.MatchesExpectedIdentity(
                identity, devicePath + "-other", 0x054C, 0x0CE6),
            "a different HID path matched the selected controller");
        Require(!BluetoothDualSenseInputSource.MatchesExpectedIdentity(
                identity, devicePath, 0x054C, 0x0DF2),
            "a different product ID matched the selected controller");

        bool unsupportedRejected = false;
        try
        {
            BluetoothDualSenseInputSource.ValidateExpectedIdentity(
                new BluetoothDualSenseIdentity(devicePath, 0x1234, 0x0CE6));
        }
        catch (ArgumentException)
        {
            unsupportedRejected = true;
        }
        Require(unsupportedRejected,
            "an unsupported controller identity was not rejected");
    }

    private static void TestBluetoothInputConversion()
    {
        byte[] bluetooth = new byte[78];
        bluetooth[0] = 0x31;
        bluetooth[1] = 0x10;
        for (int i = 2; i < 74; i++)
        {
            bluetooth[i] = (byte)(i * 3);
        }
        uint crc = ComputeBluetoothCrc(0xA1, bluetooth.AsSpan(0, 74));
        BinaryPrimitives.WriteUInt32LittleEndian(bluetooth.AsSpan(74), crc);

        Require(BluetoothDualSenseInputSource.TryConvertBluetoothReport(
                bluetooth, out byte[] usb) &&
                usb.Length == 64 && usb[0] == 0x01 &&
                usb.AsSpan(1).SequenceEqual(bluetooth.AsSpan(2, 63)),
            "Bluetooth input did not map byte-exact to USB report 0x01");

        bluetooth[10] ^= 0x01;
        Require(!BluetoothDualSenseInputSource.TryConvertBluetoothReport(bluetooth, out _),
            "Bluetooth input with an invalid CRC was accepted");

        byte[] usbOutput = new byte[48];
        usbOutput[0] = 0x02;
        usbOutput[1] = 0x0C;
        usbOutput[11] = 0x22;
        usbOutput[12] = 0x12;
        usbOutput[13] = 0x00;
        usbOutput[14] = 0x21;
        usbOutput[22] = 0x05;
        usbOutput[45] = 0xFF; // must not leak unrelated lightbar state
        Require(BluetoothDualSenseInputSource.TryBuildBluetoothTriggerReport(
                usbOutput, out byte[] bluetoothOutput) &&
                bluetoothOutput.Length == 78 && bluetoothOutput[0] == 0x31 &&
                bluetoothOutput[1] == 0x02 && bluetoothOutput[2] == 0x0C &&
                bluetoothOutput.AsSpan(12, 11).SequenceEqual(usbOutput.AsSpan(11, 11)) &&
                bluetoothOutput.AsSpan(23, 11).SequenceEqual(usbOutput.AsSpan(22, 11)) &&
                bluetoothOutput[46] == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bluetoothOutput.AsSpan(74)) ==
                    ComputeBluetoothCrc(0xA2, bluetoothOutput.AsSpan(0, 74)),
            "USB output did not map to an authenticated trigger-only Bluetooth report");
        usbOutput[1] = 0x02;
        Require(!BluetoothDualSenseInputSource.TryBuildBluetoothTriggerReport(
                usbOutput, out _),
            "output without adaptive-trigger update flags must not alter trigger state");

        byte[] usbAudio = new byte[16 * 8];
        for (int frame = 0; frame < 16; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(usbAudio.AsSpan(frame * 8, 2), 30000);
            BinaryPrimitives.WriteInt16LittleEndian(usbAudio.AsSpan(frame * 8 + 2, 2), -30000);
            BinaryPrimitives.WriteInt16LittleEndian(usbAudio.AsSpan(frame * 8 + 4, 2), 16384);
            BinaryPrimitives.WriteInt16LittleEndian(usbAudio.AsSpan(frame * 8 + 6, 2), -16384);
        }
        byte[] bluetoothPcm = new byte[2];
        Require(BluetoothDualSenseInputSource.ConvertUsbHapticPcm(usbAudio, bluetoothPcm) == 2 &&
                bluetoothPcm[0] == 0x40 && bluetoothPcm[1] == 0xC0,
            "USB speaker channels or gain altered haptic channels 3/4");
        Require(Math.Abs(BluetoothDualSenseInputSource.CombineSpeakerVolume(0.5f, 0.5f) -
                    0.25f) < 0.0001f &&
                Math.Abs(BluetoothDualSenseInputSource.CombineSpeakerVolume(2.0f, 0.5f) -
                    0.5f) < 0.0001f &&
                Math.Abs(BluetoothDualSenseInputSource.CombineSpeakerVolume(0.5f, 2.0f) -
                    0.5f) < 0.0001f,
            "configured speaker gain did not multiply/clamp host playback volume");

        byte[] hapticChunk = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        byte[] hapticReport = new byte[398];
        BluetoothDualSenseInputSource.BuildBluetoothHapticReport(
            hapticReport, sequence: 3, packetCounter: 9, hapticChunk);
        Require(hapticReport[0] == 0x36 && hapticReport[1] == 0x30 &&
                hapticReport[2] == 0x91 && hapticReport[10] == 9 &&
                hapticReport[11] == 0x90 && hapticReport[12] == 63 &&
                hapticReport[76] == 0x92 && hapticReport[77] == 64 &&
                hapticReport.AsSpan(78, 64).SequenceEqual(hapticChunk) &&
                BinaryPrimitives.ReadUInt32LittleEndian(hapticReport.AsSpan(394, 4)) ==
                    ComputeBluetoothCrc(0xA2, hapticReport.AsSpan(0, 394)),
            "Bluetooth report 0x36 native-haptic container or CRC mismatch");

        Console.WriteLine("servertest: microphone report classification");
        byte[] micReport = new byte[78];
        micReport[0] = 0x31;
        micReport[1] = 0x32; // sequence 3, bit 1 = microphone payload
        for (int i = 3; i < 74; i++)
        {
            micReport[i] = (byte)(0xE0 - i);
        }
        uint micCrc = ComputeBluetoothCrc(0xA1, micReport.AsSpan(0, 74));
        BinaryPrimitives.WriteUInt32LittleEndian(micReport.AsSpan(74), micCrc);
        Require(!BluetoothDualSenseInputSource.TryConvertBluetoothReport(micReport, out _),
            "microphone-flagged report leaked into the gamepad parser");
        byte[] opusPayload = new byte[71];
        Require(BluetoothDualSenseInputSource.TryExtractMicrophoneOpusPayload(
                    micReport, opusPayload) &&
                opusPayload.AsSpan().SequenceEqual(micReport.AsSpan(3, 71)),
            "microphone Opus payload extraction mismatch");
        micReport[40] ^= 0x80;
        Require(!BluetoothDualSenseInputSource.TryExtractMicrophoneOpusPayload(
                micReport, opusPayload),
            "microphone report with an invalid CRC was accepted");
        bluetooth[10] ^= 0x01; // restore the valid gamepad CRC from the earlier test
        Require(!BluetoothDualSenseInputSource.TryExtractMicrophoneOpusPayload(
                bluetooth, opusPayload),
            "gamepad report was misclassified as a microphone payload");

        Console.WriteLine("servertest: audio-bearing report 0x36 layout");
        byte[] opusFrame = Enumerable.Range(0, 200).Select(value => (byte)(value ^ 0x5A)).ToArray();
        byte[] audioReport = new byte[398];
        BluetoothDualSenseInputSource.BuildBluetoothAudioReport(
            audioReport, sequence: 3, packetCounter: 9, hapticChunk, opusFrame,
            headphoneRoute: false);
        Require(audioReport.AsSpan(0, 142).SequenceEqual(hapticReport.AsSpan(0, 142)),
            "audio-bearing 0x36 must not perturb the proven haptic layout");
        Require(audioReport[142] == 0x93 && audioReport[143] == 200 &&
                audioReport.AsSpan(144, 200).SequenceEqual(opusFrame) &&
                BinaryPrimitives.ReadUInt32LittleEndian(audioReport.AsSpan(394, 4)) ==
                    ComputeBluetoothCrc(0xA2, audioReport.AsSpan(0, 394)),
            "speaker-routed Opus packet or CRC mismatch");
        BluetoothDualSenseInputSource.BuildBluetoothAudioReport(
            audioReport, sequence: 3, packetCounter: 9, hapticChunk, opusFrame,
            headphoneRoute: true);
        Require(audioReport[142] == 0x96,
            "headphone route byte mismatch");
        Require(hapticReport[4] == 0xFE && audioReport[4] == 0xFE,
            "config byte must be 0xFE while the microphone is off");
        BluetoothDualSenseInputSource.BuildBluetoothHapticReport(
            audioReport, sequence: 3, packetCounter: 9, hapticChunk,
            microphoneActive: true);
        Require(audioReport[4] == 0xFF &&
                BinaryPrimitives.ReadUInt32LittleEndian(audioReport.AsSpan(394, 4)) ==
                    ComputeBluetoothCrc(0xA2, audioReport.AsSpan(0, 394)),
            "config byte must acknowledge an active microphone (0xFF)");

        Console.WriteLine("servertest: amplifier and microphone control reports");
        byte[] ampSetup = BluetoothDualSenseInputSource.BuildAmplifierSetupReport();
        Require(ampSetup.Length == 142 && ampSetup[0] == 0x32 && ampSetup[1] == 0x10 &&
                ampSetup[2] == 0x90 && ampSetup[3] == 0x3F && ampSetup[4] == 0xB0 &&
                ampSetup[5] == 0x80 && ampSetup[8] == 0x64 && ampSetup[9] == 0x64 &&
                ampSetup[41] == 0x02 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ampSetup.AsSpan(138, 4)) ==
                    ComputeBluetoothCrc(0xA2, ampSetup.AsSpan(0, 138)),
            "amplifier setup report mismatch");
        byte[] micOn = BluetoothDualSenseInputSource.BuildMicrophoneControlReport(
            sequence: 5, enable: true);
        byte[] micOff = BluetoothDualSenseInputSource.BuildMicrophoneControlReport(
            sequence: 6, enable: false);
        Require(micOn.Length == 142 && micOn[0] == 0x32 && micOn[1] == 0x50 &&
                micOn[2] == 0x91 && micOn[3] == 0x01 && micOn[4] == 0x03 &&
                micOff[1] == 0x60 && micOff[4] == 0x02 &&
                BinaryPrimitives.ReadUInt32LittleEndian(micOn.AsSpan(138, 4)) ==
                    ComputeBluetoothCrc(0xA2, micOn.AsSpan(0, 138)),
            "microphone control report mismatch");

        Console.WriteLine("servertest: streaming linear resampler");
        TestStereoLinearResampler();
    }

    private static void TestStereoLinearResampler()
    {
        var downsampler = new BluetoothDualSenseInputSource.StereoLinearResampler(48000, 45000);
        short[] ramp = new short[480 * 2];
        for (int frame = 0; frame < 480; frame++)
        {
            ramp[frame * 2] = (short)frame;
            ramp[frame * 2 + 1] = (short)-frame;
        }
        short[] output = new short[600 * 2];
        int produced = downsampler.Resample(ramp, output);
        Require(produced % 2 == 0 && Math.Abs(produced / 2 - 450) <= 1,
            $"48->45 kHz resampler produced {produced / 2} frames for 480 input frames");
        for (int frame = 1; frame < produced / 2; frame++)
        {
            Require(output[frame * 2] >= output[(frame - 1) * 2] &&
                    output[frame * 2] == -output[frame * 2 + 1],
                "48->45 kHz resampler output is not a monotonic mirrored ramp");
        }

        // Chunked delivery must produce the identical sample sequence.
        var whole = new BluetoothDualSenseInputSource.StereoLinearResampler(48000, 45000);
        var chunked = new BluetoothDualSenseInputSource.StereoLinearResampler(48000, 45000);
        short[] signal = new short[512 * 2];
        var random = new Random(1234);
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }
        short[] wholeOut = new short[700 * 2];
        int wholeCount = whole.Resample(signal, wholeOut);
        short[] chunkOut = new short[700 * 2];
        int chunkCount = 0;
        int position = 0;
        foreach (int chunkFrames in new[] { 7, 61, 128, 200, 116 })
        {
            chunkCount += chunked.Resample(
                signal.AsSpan(position * 2, chunkFrames * 2),
                chunkOut.AsSpan(chunkCount));
            position += chunkFrames;
        }
        Require(position == 512 && chunkCount == wholeCount &&
                chunkOut.AsSpan(0, chunkCount).SequenceEqual(wholeOut.AsSpan(0, wholeCount)),
            "chunked resampling diverged from whole-buffer resampling");

        var upsampler = new BluetoothDualSenseInputSource.StereoLinearResampler(45000, 48000);
        short[] upInput = new short[450 * 2];
        for (int frame = 0; frame < 450; frame++)
        {
            upInput[frame * 2] = 1000;
            upInput[frame * 2 + 1] = -1000;
        }
        int upProduced = upsampler.Resample(upInput, output);
        Require(upProduced % 2 == 0 && Math.Abs(upProduced / 2 - 480) <= 1,
            $"45->48 kHz resampler produced {upProduced / 2} frames for 450 input frames");
        for (int i = 0; i < upProduced; i += 2)
        {
            Require(output[i] == 1000 && output[i + 1] == -1000,
                "45->48 kHz resampler distorted a constant signal");
        }
    }

    private static uint ComputeBluetoothCrc(byte seed, ReadOnlySpan<byte> data)
    {
        uint state = UpdateCrc32(0xFFFFFFFF, seed);
        foreach (byte value in data)
        {
            state = UpdateCrc32(state, value);
        }
        return ~state;
    }

    private static uint UpdateCrc32(uint state, byte value)
    {
        state ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            state = (state & 1) != 0 ? 0xEDB88320U ^ (state >> 1) : state >> 1;
        }
        return state;
    }

    private static async Task TestCompositeDeviceListAsync(DescriptorSet descriptors)
    {
        using var server = new VirtualDualSenseServer(descriptors,
            new VirtualDualSenseServerOptions(Port: 0,
                InputInterval: TimeSpan.FromMilliseconds(200),
                IsochronousOutQuiesceTimeout: TimeSpan.FromMilliseconds(750)));
        var captured = new TaskCompletionSource<IsochronousOutCapture>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fatalOutSessionLog = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fatalInSessionLog = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.IsochronousOutReceived += output => captured.TrySetResult(output);
        server.Log += message =>
        {
            if (message.StartsWith("FatalUsbIpSession: ISO OUT", StringComparison.Ordinal))
            {
                fatalOutSessionLog.TrySetResult(message);
            }
            else if (message.StartsWith("FatalUsbIpSession: ISO IN",
                         StringComparison.Ordinal))
            {
                fatalInSessionLog.TrySetResult(message);
            }
        };
        server.Start();
        using var cancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cancellation.Token);
        try
        {
            await TestDeviceListAsync(server.Port, descriptors.Interfaces);
            Console.WriteLine("servertest: composite isochronous OUT completion");
            await TestCompositeIsoOutAsync(server.Port, captured.Task);
            await TestIsochronousOutWatchdogAsync(server.Port, fatalOutSessionLog.Task);
            await TestIsochronousInWatchdogAsync(server.Port, fatalInSessionLog.Task);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task TestCompositeIsoOutAsync(int port,
        Task<IsochronousOutCapture> captureTask)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));
        byte[] importReply = await UsbIpCodec.ReadExactlyAsync(stream, 8 + 312);
        Require(U16(importReply, 2) == UsbIpConstants.OpRepImport && U32(importReply, 4) == 0,
            "composite ISO import failed");

        byte[] setConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 1, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(1, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setConfiguration));
        SubmitReply configurationReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(configurationReply.Status == 0, "composite SET_CONFIGURATION failed");

        byte[] setPlaybackInterface = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 1, length: 0);
        await stream.WriteAsync(EncodeSubmit(2, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterface));
        SubmitReply interfaceReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(interfaceReply.Status == 0, "playback SET_INTERFACE alt 1 failed");

        byte[] audio = new byte[768];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = (byte)(i * 17);
        }
        var packets = new[]
        {
            new UsbIpIsoPacket(0, 384, 0, 0),
            new UsbIpIsoPacket(384, 384, 0, 0),
        };
        await stream.WriteAsync(EncodeIsoSubmit(3, endpoint: 1, startFrame: 1234,
            interval: 1, audio, packets));
        IsoSubmitReply isoReply = await ReadIsoSubmitReplyAsync(stream);
        IsochronousOutCapture capture = await captureTask.WaitAsync(TimeSpan.FromSeconds(2));
        Require(isoReply.SequenceNumber == 3 && isoReply.Status == 0 &&
                isoReply.ActualLength == audio.Length && isoReply.StartFrame == 1234 &&
                isoReply.Packets.Count == 2 &&
                isoReply.Packets.All(packet => packet.ActualLength == 384 && packet.Status == 0) &&
                capture.Endpoint == 1 && capture.StartFrame == 1234 &&
                capture.Data.AsSpan().SequenceEqual(audio),
            "ISO OUT capture or packet-preserving completion mismatch");

        Console.WriteLine("servertest: ISO UNLINK wins pacing race without late RET_SUBMIT");
        byte[] pacedAudio = new byte[38400];
        var pacedPackets = Enumerable.Range(0, 100)
            .Select(index => new UsbIpIsoPacket(checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(4, endpoint: 1, startFrame: 1236,
            interval: 4, pacedAudio, pacedPackets));
        await Task.Delay(TimeSpan.FromMilliseconds(2));
        await stream.WriteAsync(EncodeUnlink(5, unlinkSequence: 4));
        byte[] unlinkReply = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkReply, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkReply, 4) == 5 && I32(unlinkReply, 20) == -104,
            "paced ISO UNLINK did not cancel the pending completion");

        using var noLateCompletion = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(120));
        try
        {
            await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength,
                noLateCompletion.Token);
            throw new InvalidOperationException(
                "canceled ISO transfer emitted a late RET_SUBMIT");
        }
        catch (OperationCanceledException) when (noLateCompletion.IsCancellationRequested)
        {
        }

        await TestIsochronousOutQuiescingAsync(stream);

        Console.WriteLine("servertest: microphone isochronous IN completion");
        byte[] setCaptureInterface = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 2, length: 0);
        await stream.WriteAsync(EncodeSubmit(6, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setCaptureInterface));
        SubmitReply captureAltReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(captureAltReply.Status == 0, "capture SET_INTERFACE alt 1 failed");

        var microphonePackets = Enumerable.Range(0, 10)
            .Select(index => new UsbIpIsoPacket(checked((uint)(index * 196)), 196, 0, 0))
            .ToArray();
        long isoInStarted = Stopwatch.GetTimestamp();
        await stream.WriteAsync(EncodeIsoSubmit(7, endpoint: 2, startFrame: 4321,
            interval: 4, Array.Empty<byte>(), microphonePackets,
            UsbIpConstants.DirectionIn, transferLength: 1960));
        IsoSubmitReply microphoneReply = await ReadIsoInSubmitReplyAsync(stream);
        double isoInElapsedMs = (Stopwatch.GetTimestamp() - isoInStarted) * 1000.0 /
            Stopwatch.Frequency;
        Require(microphoneReply.SequenceNumber == 7 && microphoneReply.Status == 0 &&
                microphoneReply.StartFrame == 4321 && microphoneReply.ErrorCount == 0 &&
                microphoneReply.Packets.Count == 10 &&
                microphoneReply.Packets.All(packet =>
                    packet.ActualLength == 192 && packet.Status == 0) &&
                microphoneReply.Packets.Select(packet => packet.Offset)
                    .SequenceEqual(microphonePackets.Select(packet => packet.Offset)) &&
                microphoneReply.ActualLength == 1920 &&
                microphoneReply.Data.Length == 1920 &&
                microphoneReply.Data.All(value => value == 0),
            "microphone ISO IN did not complete as compact nominal silence");
        Require(isoInElapsedMs >= 8,
            $"microphone ISO IN completed in {isoInElapsedMs:0.0} ms; pacing must cover ~10 ms");

        Console.WriteLine("servertest: microphone ISO IN UNLINK");
        await stream.WriteAsync(EncodeIsoSubmit(8, endpoint: 2, startFrame: 4331,
            interval: 4, Array.Empty<byte>(), microphonePackets,
            UsbIpConstants.DirectionIn, transferLength: 1960));
        await stream.WriteAsync(EncodeUnlink(9, unlinkSequence: 8));
        byte[] microphoneUnlinkReply = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(microphoneUnlinkReply, 0) == UsbIpConstants.RetUnlink &&
                U32(microphoneUnlinkReply, 4) == 9 && I32(microphoneUnlinkReply, 20) == -104,
            "pending microphone ISO IN UNLINK mismatch");
        using var noLateMicrophoneCompletion = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(120));
        try
        {
            await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength,
                noLateMicrophoneCompletion.Token);
            throw new InvalidOperationException(
                "canceled microphone ISO IN emitted a late RET_SUBMIT");
        }
        catch (OperationCanceledException) when (noLateMicrophoneCompletion.IsCancellationRequested)
        {
        }

        await TestIsochronousInQuiescingAsync(stream);
        await TestConfigurationZeroQuiescingAsync(stream);
        await TestConfigurationOneQuiescingAsync(stream);
    }

    private static async Task TestIsochronousInQuiescingAsync(NetworkStream stream)
    {
        Console.WriteLine("servertest: ISO IN alt-zero quiesce barrier and generation reopen");

        UsbIpIsoPacket[] longPackets = Enumerable.Range(0, 1000)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 196)), 196, 0, 0))
            .ToArray();
        UsbIpIsoPacket[] shortPackets = Enumerable.Range(0, 2)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 196)), 196, 0, 0))
            .ToArray();

        await stream.WriteAsync(EncodeIsoSubmit(70, endpoint: 2, startFrame: 12000,
            interval: 4, Array.Empty<byte>(), longPackets,
            UsbIpConstants.DirectionIn, transferLength: 1000 * 196));
        await stream.WriteAsync(EncodeIsoSubmit(71, endpoint: 2, startFrame: 13000,
            interval: 4, Array.Empty<byte>(), shortPackets,
            UsbIpConstants.DirectionIn, transferLength: 2 * 196));
        await stream.WriteAsync(EncodeIsoSubmit(72, endpoint: 2, startFrame: 13002,
            interval: 4, Array.Empty<byte>(), shortPackets,
            UsbIpConstants.DirectionIn, transferLength: 2 * 196));

        byte[] setCaptureInterfaceOff = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 0, index: 2, length: 0);
        await stream.WriteAsync(EncodeSubmit(73, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setCaptureInterfaceOff));
        SubmitReply altZeroReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(altZeroReply.SequenceNumber == 73 && altZeroReply.Status == 0,
            nameof(TestIsochronousInQuiescingAsync));

        using (var noStaleCompletion = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(120)))
        {
            try
            {
                await UsbIpCodec.ReadExactlyAsync(stream,
                    UsbIpConstants.UrbHeaderLength, noStaleCompletion.Token);
                throw new InvalidOperationException(
                    "capture alt-zero ACK was followed by a stale RET_SUBMIT");
            }
            catch (OperationCanceledException) when (noStaleCompletion.IsCancellationRequested)
            {
            }
        }

        byte[] setCaptureInterfaceOn = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 2, length: 0);
        await stream.WriteAsync(EncodeSubmit(74, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setCaptureInterfaceOn));
        SubmitReply unsafeReopenReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(unsafeReopenReply.SequenceNumber == 74 &&
                unsafeReopenReply.Status == ControlResult.Stall,
            nameof(TestIsochronousInQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(75, unlinkSequence: 70));
        byte[] unlinkFirst = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkFirst, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkFirst, 4) == 75 && I32(unlinkFirst, 20) == -104,
            nameof(TestIsochronousInQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(76, unlinkSequence: 71));
        byte[] unlinkSecond = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkSecond, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkSecond, 4) == 76 && I32(unlinkSecond, 20) == -104,
            nameof(TestIsochronousInQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(77, unlinkSequence: 72));
        byte[] unlinkThird = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkThird, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkThird, 4) == 77 && I32(unlinkThird, 20) == -104,
            nameof(TestIsochronousInQuiescingAsync));

        long reopenStarted = Stopwatch.GetTimestamp();
        await stream.WriteAsync(EncodeSubmit(78, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setCaptureInterfaceOn));
        SubmitReply reopenedAltReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(reopenedAltReply.SequenceNumber == 78 && reopenedAltReply.Status == 0,
            nameof(TestIsochronousInQuiescingAsync));

        await stream.WriteAsync(EncodeIsoSubmit(79, endpoint: 2, startFrame: 14000,
            interval: 4, Array.Empty<byte>(), shortPackets,
            UsbIpConstants.DirectionIn, transferLength: 2 * 196));
        IsoSubmitReply reopenedReply = await ReadIsoInSubmitReplyAsync(stream);
        double reopenElapsedMs = (Stopwatch.GetTimestamp() - reopenStarted) * 1000.0 /
            Stopwatch.Frequency;
        Require(reopenedReply.SequenceNumber == 79 && reopenedReply.Status == 0 &&
                reopenedReply.ActualLength == 2 * 192 &&
                reopenedReply.Packets.All(packet =>
                    packet.ActualLength == 192 && packet.Status == 0) &&
                reopenedReply.Data.Length == 2 * 192 &&
                reopenedReply.Data.All(value => value == 0) &&
                reopenElapsedMs < 500,
            nameof(TestIsochronousInQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(80, unlinkSequence: 79));
        byte[] completionWon = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(completionWon, 0) == UsbIpConstants.RetUnlink &&
                U32(completionWon, 4) == 80 && I32(completionWon, 20) == 0,
            nameof(TestIsochronousInQuiescingAsync));

        using var noDuplicateCompletion = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(120));
        try
        {
            await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength,
                noDuplicateCompletion.Token);
            throw new InvalidOperationException(nameof(TestIsochronousInQuiescingAsync));
        }
        catch (OperationCanceledException) when (noDuplicateCompletion.IsCancellationRequested)
        {
        }
    }

    private static async Task TestConfigurationZeroQuiescingAsync(NetworkStream stream)
    {
        Console.WriteLine("servertest: SET_CONFIGURATION zero quiesces and resets ISO OUT");
        byte[] audio = new byte[1000 * 384];
        UsbIpIsoPacket[] packets = Enumerable.Range(0, 1000)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(40, endpoint: 1, startFrame: 8000,
            interval: 4, audio, packets));

        byte[] deconfigure = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 0, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(41, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, deconfigure));
        SubmitReply deconfigureReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(deconfigureReply.SequenceNumber == 41 && deconfigureReply.Status == 0,
            nameof(TestConfigurationZeroQuiescingAsync));

        byte[] getPlaybackInterface = Setup(0x81, UsbStandardRequest.GetInterface,
            value: 0, index: 1, length: 1);
        await stream.WriteAsync(EncodeSubmit(42, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 1, getPlaybackInterface));
        SubmitReply resetAltReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: true);
        Require(resetAltReply.SequenceNumber == 42 && resetAltReply.Status == 0 &&
                resetAltReply.Data.SequenceEqual(new byte[] { 0 }),
            nameof(TestConfigurationZeroQuiescingAsync));

        byte[] setPlaybackInterfaceOn = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 1, length: 0);
        await stream.WriteAsync(EncodeSubmit(43, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterfaceOn));
        SubmitReply unsafeReopen = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(unsafeReopen.SequenceNumber == 43 &&
                unsafeReopen.Status == ControlResult.Stall,
            nameof(TestConfigurationZeroQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(44, unlinkSequence: 40));
        byte[] unlinkReply = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkReply, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkReply, 4) == 44 && I32(unlinkReply, 20) == -104,
            nameof(TestConfigurationZeroQuiescingAsync));

        byte[] configure = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 1, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(45, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, configure));
        SubmitReply configureReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(configureReply.SequenceNumber == 45 && configureReply.Status == 0,
            nameof(TestConfigurationZeroQuiescingAsync));

        long reopenStarted = Stopwatch.GetTimestamp();
        await stream.WriteAsync(EncodeSubmit(46, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterfaceOn));
        SubmitReply reopenReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        byte[] shortAudio = new byte[2 * 384];
        UsbIpIsoPacket[] shortPackets = Enumerable.Range(0, 2)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(47, endpoint: 1, startFrame: 9000,
            interval: 4, shortAudio, shortPackets));
        IsoSubmitReply newGenerationReply = await ReadIsoSubmitReplyAsync(stream);
        double reopenElapsedMs = (Stopwatch.GetTimestamp() - reopenStarted) * 1000.0 /
            Stopwatch.Frequency;
        Require(reopenReply.SequenceNumber == 46 && reopenReply.Status == 0 &&
                newGenerationReply.SequenceNumber == 47 &&
                newGenerationReply.Status == 0 && reopenElapsedMs < 500,
            nameof(TestConfigurationZeroQuiescingAsync));
    }

    private static async Task TestConfigurationOneQuiescingAsync(NetworkStream stream)
    {
        Console.WriteLine(
            "servertest: SET_CONFIGURATION one resets and quiesces active ISO OUT and IN");

        byte[] setCaptureInterfaceOn = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 2, length: 0);
        await stream.WriteAsync(EncodeSubmit(50, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setCaptureInterfaceOn));
        SubmitReply captureAltReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(captureAltReply.SequenceNumber == 50 && captureAltReply.Status == 0,
            nameof(TestConfigurationOneQuiescingAsync));

        byte[] longAudio = new byte[1000 * 384];
        UsbIpIsoPacket[] longPackets = Enumerable.Range(0, 1000)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(51, endpoint: 1, startFrame: 10000,
            interval: 4, longAudio, longPackets));
        UsbIpIsoPacket[] longMicrophonePackets = Enumerable.Range(0, 1000)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 196)), 196, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(90, endpoint: 2, startFrame: 15000,
            interval: 4, Array.Empty<byte>(), longMicrophonePackets,
            UsbIpConstants.DirectionIn, transferLength: 1000 * 196));

        byte[] invalidConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 2, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(52, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, invalidConfiguration));
        SubmitReply invalidReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(invalidReply.SequenceNumber == 52 &&
                invalidReply.Status == ControlResult.Stall,
            nameof(TestConfigurationOneQuiescingAsync));

        byte[] getConfiguration = Setup(0x80, UsbStandardRequest.GetConfiguration,
            value: 0, index: 0, length: 1);
        await stream.WriteAsync(EncodeSubmit(53, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 1, getConfiguration));
        SubmitReply configurationBeforeReset = await ReadSubmitReplyAsync(stream,
            hasInputPayload: true);
        byte[] getPlaybackInterface = Setup(0x81, UsbStandardRequest.GetInterface,
            value: 0, index: 1, length: 1);
        await stream.WriteAsync(EncodeSubmit(54, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 1, getPlaybackInterface));
        SubmitReply playbackBeforeReset = await ReadSubmitReplyAsync(stream,
            hasInputPayload: true);
        byte[] getCaptureInterface = Setup(0x81, UsbStandardRequest.GetInterface,
            value: 0, index: 2, length: 1);
        await stream.WriteAsync(EncodeSubmit(55, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 1, getCaptureInterface));
        SubmitReply captureBeforeReset = await ReadSubmitReplyAsync(stream,
            hasInputPayload: true);
        Require(configurationBeforeReset.SequenceNumber == 53 &&
                configurationBeforeReset.Status == 0 &&
                configurationBeforeReset.Data.SequenceEqual(new byte[] { 1 }) &&
                playbackBeforeReset.SequenceNumber == 54 &&
                playbackBeforeReset.Status == 0 &&
                playbackBeforeReset.Data.SequenceEqual(new byte[] { 1 }) &&
                captureBeforeReset.SequenceNumber == 55 &&
                captureBeforeReset.Status == 0 &&
                captureBeforeReset.Data.SequenceEqual(new byte[] { 1 }),
            nameof(TestConfigurationOneQuiescingAsync));

        byte[] selectConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 1, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(56, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, selectConfiguration));
        SubmitReply selectReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(selectReply.SequenceNumber == 56 && selectReply.Status == 0,
            nameof(TestConfigurationOneQuiescingAsync));

        using (var noStaleCompletion = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(120)))
        {
            try
            {
                await UsbIpCodec.ReadExactlyAsync(stream,
                    UsbIpConstants.UrbHeaderLength, noStaleCompletion.Token);
                throw new InvalidOperationException(
                    "SET_CONFIGURATION ACK was followed by a stale RET_SUBMIT");
            }
            catch (OperationCanceledException) when (noStaleCompletion.IsCancellationRequested)
            {
            }
        }

        await stream.WriteAsync(EncodeSubmit(57, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 1, getPlaybackInterface));
        SubmitReply playbackAfterReset = await ReadSubmitReplyAsync(stream,
            hasInputPayload: true);
        await stream.WriteAsync(EncodeSubmit(58, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 1, getCaptureInterface));
        SubmitReply captureAfterReset = await ReadSubmitReplyAsync(stream,
            hasInputPayload: true);
        await stream.WriteAsync(EncodeSubmit(59, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 1, getConfiguration));
        SubmitReply configurationAfterReset = await ReadSubmitReplyAsync(stream,
            hasInputPayload: true);
        Require(playbackAfterReset.SequenceNumber == 57 &&
                playbackAfterReset.Status == 0 &&
                playbackAfterReset.Data.SequenceEqual(new byte[] { 0 }) &&
                captureAfterReset.SequenceNumber == 58 &&
                captureAfterReset.Status == 0 &&
                captureAfterReset.Data.SequenceEqual(new byte[] { 0 }) &&
                configurationAfterReset.SequenceNumber == 59 &&
                configurationAfterReset.Status == 0 &&
                configurationAfterReset.Data.SequenceEqual(new byte[] { 1 }),
            nameof(TestConfigurationOneQuiescingAsync));

        byte[] setPlaybackInterfaceOn = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 1, length: 0);
        await stream.WriteAsync(EncodeSubmit(60, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterfaceOn));
        SubmitReply unsafeReopen = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(unsafeReopen.SequenceNumber == 60 &&
                unsafeReopen.Status == ControlResult.Stall,
            nameof(TestConfigurationOneQuiescingAsync));

        await stream.WriteAsync(EncodeSubmit(91, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setCaptureInterfaceOn));
        SubmitReply unsafeCaptureReopen = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(unsafeCaptureReopen.SequenceNumber == 91 &&
                unsafeCaptureReopen.Status == ControlResult.Stall,
            nameof(TestConfigurationOneQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(61, unlinkSequence: 51));
        byte[] unlinkReply = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkReply, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkReply, 4) == 61 && I32(unlinkReply, 20) == -104,
            nameof(TestConfigurationOneQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(92, unlinkSequence: 90));
        byte[] unlinkCaptureReply = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkCaptureReply, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkCaptureReply, 4) == 92 &&
                I32(unlinkCaptureReply, 20) == -104,
            nameof(TestConfigurationOneQuiescingAsync));

        long reopenStarted = Stopwatch.GetTimestamp();
        await stream.WriteAsync(EncodeSubmit(62, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterfaceOn));
        SubmitReply reopenReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        byte[] shortAudio = new byte[2 * 384];
        UsbIpIsoPacket[] shortPackets = Enumerable.Range(0, 2)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(63, endpoint: 1, startFrame: 11000,
            interval: 4, shortAudio, shortPackets));
        IsoSubmitReply newGenerationReply = await ReadIsoSubmitReplyAsync(stream);
        await stream.WriteAsync(EncodeSubmit(93, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setCaptureInterfaceOn));
        SubmitReply captureReopenReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        UsbIpIsoPacket[] shortMicrophonePackets = Enumerable.Range(0, 2)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 196)), 196, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(94, endpoint: 2, startFrame: 16000,
            interval: 4, Array.Empty<byte>(), shortMicrophonePackets,
            UsbIpConstants.DirectionIn, transferLength: 2 * 196));
        IsoSubmitReply newCaptureGenerationReply =
            await ReadIsoInSubmitReplyAsync(stream);
        double reopenElapsedMs = (Stopwatch.GetTimestamp() - reopenStarted) * 1000.0 /
            Stopwatch.Frequency;
        Require(reopenReply.SequenceNumber == 62 && reopenReply.Status == 0 &&
                newGenerationReply.SequenceNumber == 63 &&
                newGenerationReply.Status == 0 &&
                captureReopenReply.SequenceNumber == 93 &&
                captureReopenReply.Status == 0 &&
                newCaptureGenerationReply.SequenceNumber == 94 &&
                newCaptureGenerationReply.Status == 0 &&
                newCaptureGenerationReply.ActualLength == 2 * 192 &&
                newCaptureGenerationReply.Packets.All(packet =>
                    packet.ActualLength == 192 && packet.Status == 0) &&
                reopenElapsedMs < 500,
            nameof(TestConfigurationOneQuiescingAsync));
    }

    private static async Task TestIsochronousOutQuiescingAsync(NetworkStream stream)
    {
        Console.WriteLine("servertest: ISO OUT alt-zero quiesce barrier and generation reopen");

        byte[] longAudio = new byte[1000 * 384];
        UsbIpIsoPacket[] longPackets = Enumerable.Range(0, 1000)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();
        byte[] shortAudio = new byte[2 * 384];
        UsbIpIsoPacket[] shortPackets = Enumerable.Range(0, 2)
            .Select(index => new UsbIpIsoPacket(
                checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();

        await stream.WriteAsync(EncodeIsoSubmit(10, endpoint: 1, startFrame: 5000,
            interval: 4, longAudio, longPackets));
        await stream.WriteAsync(EncodeIsoSubmit(11, endpoint: 1, startFrame: 5250,
            interval: 4, shortAudio, shortPackets));
        await stream.WriteAsync(EncodeIsoSubmit(12, endpoint: 1, startFrame: 5252,
            interval: 4, shortAudio, shortPackets));

        byte[] setPlaybackInterfaceOff = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 0, index: 1, length: 0);
        await stream.WriteAsync(EncodeSubmit(20, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterfaceOff));
        SubmitReply altZeroReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(altZeroReply.SequenceNumber == 20 && altZeroReply.Status == 0,
            nameof(TestIsochronousOutQuiescingAsync));
        byte[] setPlaybackInterfaceOn = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 1, length: 0);
        await stream.WriteAsync(EncodeSubmit(21, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterfaceOn));
        SubmitReply unsafeReopenReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(unsafeReopenReply.SequenceNumber == 21 &&
                unsafeReopenReply.Status == ControlResult.Stall,
            nameof(TestIsochronousOutQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(22, unlinkSequence: 10));
        byte[] unlinkWon = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkWon, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkWon, 4) == 22 && I32(unlinkWon, 20) == -104,
            nameof(TestIsochronousOutQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(23, unlinkSequence: 11));
        byte[] unlinkSecond = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkSecond, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkSecond, 4) == 23 && I32(unlinkSecond, 20) == -104,
            nameof(TestIsochronousOutQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(24, unlinkSequence: 12));
        byte[] unlinkThird = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkThird, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkThird, 4) == 24 && I32(unlinkThird, 20) == -104,
            nameof(TestIsochronousOutQuiescingAsync));

        long reopenStarted = Stopwatch.GetTimestamp();
        await stream.WriteAsync(EncodeSubmit(25, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterfaceOn));
        SubmitReply reopenedAltReply = await ReadSubmitReplyAsync(stream,
            hasInputPayload: false);
        Require(reopenedAltReply.SequenceNumber == 25 && reopenedAltReply.Status == 0,
            nameof(TestIsochronousOutQuiescingAsync));

        await stream.WriteAsync(EncodeIsoSubmit(30, endpoint: 1, startFrame: 6000,
            interval: 4, shortAudio, shortPackets));
        IsoSubmitReply reopenedReply = await ReadIsoSubmitReplyAsync(stream);
        double reopenElapsedMs = (Stopwatch.GetTimestamp() - reopenStarted) * 1000.0 /
            Stopwatch.Frequency;
        Require(reopenedReply.SequenceNumber == 30 && reopenedReply.Status == 0 &&
                reopenedReply.ActualLength == shortAudio.Length &&
                reopenedReply.Packets.All(packet =>
                    packet.ActualLength == 384 && packet.Status == 0) &&
                reopenElapsedMs < 500,
            nameof(TestIsochronousOutQuiescingAsync));

        await stream.WriteAsync(EncodeUnlink(31, unlinkSequence: 30));
        byte[] completionWon = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(completionWon, 0) == UsbIpConstants.RetUnlink &&
                U32(completionWon, 4) == 31 && I32(completionWon, 20) == 0,
            nameof(TestIsochronousOutQuiescingAsync));

        using var noDuplicateCompletion = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(120));
        try
        {
            await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength,
                noDuplicateCompletion.Token);
            throw new InvalidOperationException(nameof(TestIsochronousOutQuiescingAsync));
        }
        catch (OperationCanceledException) when (noDuplicateCompletion.IsCancellationRequested)
        {
        }
    }

    private static async Task TestIsochronousOutWatchdogAsync(int port,
        Task<string> fatalSessionLogTask)
    {
        Console.WriteLine("servertest: ISO OUT quiesce watchdog closes one session without reply");
        using (var client = new TcpClient { NoDelay = true })
        {
            await client.ConnectAsync("127.0.0.1", port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));
            byte[] importReply = await UsbIpCodec.ReadExactlyAsync(stream, 8 + 312);
            Require(U16(importReply, 2) == UsbIpConstants.OpRepImport &&
                    U32(importReply, 4) == 0,
                nameof(TestIsochronousOutWatchdogAsync));

            byte[] setConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
                value: 1, index: 0, length: 0);
            await stream.WriteAsync(EncodeSubmit(1, UsbIpConstants.DirectionOut,
                endpoint: 0, transferLength: 0, setConfiguration));
            SubmitReply configurationReply = await ReadSubmitReplyAsync(stream,
                hasInputPayload: false);
            Require(configurationReply.Status == 0,
                nameof(TestIsochronousOutWatchdogAsync));

            byte[] setPlaybackInterfaceOn = Setup(0x01, UsbStandardRequest.SetInterface,
                value: 1, index: 1, length: 0);
            await stream.WriteAsync(EncodeSubmit(2, UsbIpConstants.DirectionOut,
                endpoint: 0, transferLength: 0, setPlaybackInterfaceOn));
            SubmitReply altOneReply = await ReadSubmitReplyAsync(stream,
                hasInputPayload: false);
            Require(altOneReply.Status == 0,
                nameof(TestIsochronousOutWatchdogAsync));

            byte[] audio = new byte[1000 * 384];
            UsbIpIsoPacket[] packets = Enumerable.Range(0, 1000)
                .Select(index => new UsbIpIsoPacket(
                    checked((uint)(index * 384)), 384, 0, 0))
                .ToArray();
            await stream.WriteAsync(EncodeIsoSubmit(3, endpoint: 1, startFrame: 7000,
                interval: 4, audio, packets));

            byte[] setPlaybackInterfaceOff = Setup(0x01, UsbStandardRequest.SetInterface,
                value: 0, index: 1, length: 0);
            await stream.WriteAsync(EncodeSubmit(4, UsbIpConstants.DirectionOut,
                endpoint: 0, transferLength: 0, setPlaybackInterfaceOff));
            SubmitReply altZeroReply = await ReadSubmitReplyAsync(stream,
                hasInputPayload: false);
            Require(altZeroReply.SequenceNumber == 4 && altZeroReply.Status == 0,
                nameof(TestIsochronousOutWatchdogAsync));

            long watchdogStarted = Stopwatch.GetTimestamp();
            bool disconnectedWithoutData = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                byte[] oneByte = new byte[1];
                int received = await stream.ReadAsync(oneByte, timeout.Token);
                disconnectedWithoutData = received == 0;
            }
            catch (IOException)
            {
                disconnectedWithoutData = true;
            }
            catch (SocketException)
            {
                disconnectedWithoutData = true;
            }

            double watchdogElapsedMs = (Stopwatch.GetTimestamp() - watchdogStarted) *
                1000.0 / Stopwatch.Frequency;
            Require(disconnectedWithoutData && watchdogElapsedMs >= 600 &&
                    watchdogElapsedMs < 1800,
                nameof(TestIsochronousOutWatchdogAsync));
            string fatalSessionLog = await fatalSessionLogTask.WaitAsync(
                TimeSpan.FromSeconds(2));
            Require(fatalSessionLog.StartsWith(
                    "FatalUsbIpSession: ISO OUT quiesce watchdog",
                    StringComparison.Ordinal),
                nameof(TestIsochronousOutWatchdogAsync));
        }

        using var reconnect = new TcpClient { NoDelay = true };
        using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await reconnect.ConnectAsync("127.0.0.1", port, reconnectTimeout.Token);
        NetworkStream reconnectStream = reconnect.GetStream();
        await reconnectStream.WriteAsync(
            EncodeOperation(UsbIpConstants.OpReqImport, "1-1"), reconnectTimeout.Token);
        byte[] reconnectReply = await UsbIpCodec.ReadExactlyAsync(reconnectStream, 8 + 312,
            reconnectTimeout.Token);
        Require(U16(reconnectReply, 2) == UsbIpConstants.OpRepImport &&
                U32(reconnectReply, 4) == 0,
            nameof(TestIsochronousOutWatchdogAsync));
    }

    private static async Task TestIsochronousInWatchdogAsync(int port,
        Task<string> fatalSessionLogTask)
    {
        Console.WriteLine("servertest: ISO IN quiesce watchdog closes one session without reply");
        using (var client = new TcpClient { NoDelay = true })
        {
            await client.ConnectAsync("127.0.0.1", port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));
            byte[] importReply = await UsbIpCodec.ReadExactlyAsync(stream, 8 + 312);
            Require(U16(importReply, 2) == UsbIpConstants.OpRepImport &&
                    U32(importReply, 4) == 0,
                nameof(TestIsochronousInWatchdogAsync));

            byte[] setConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
                value: 1, index: 0, length: 0);
            await stream.WriteAsync(EncodeSubmit(1, UsbIpConstants.DirectionOut,
                endpoint: 0, transferLength: 0, setConfiguration));
            SubmitReply configurationReply = await ReadSubmitReplyAsync(stream,
                hasInputPayload: false);
            Require(configurationReply.Status == 0,
                nameof(TestIsochronousInWatchdogAsync));

            byte[] setCaptureInterfaceOn = Setup(0x01, UsbStandardRequest.SetInterface,
                value: 1, index: 2, length: 0);
            await stream.WriteAsync(EncodeSubmit(2, UsbIpConstants.DirectionOut,
                endpoint: 0, transferLength: 0, setCaptureInterfaceOn));
            SubmitReply altOneReply = await ReadSubmitReplyAsync(stream,
                hasInputPayload: false);
            Require(altOneReply.Status == 0,
                nameof(TestIsochronousInWatchdogAsync));

            UsbIpIsoPacket[] packets = Enumerable.Range(0, 1000)
                .Select(index => new UsbIpIsoPacket(
                    checked((uint)(index * 196)), 196, 0, 0))
                .ToArray();
            await stream.WriteAsync(EncodeIsoSubmit(3, endpoint: 2, startFrame: 17000,
                interval: 4, Array.Empty<byte>(), packets,
                UsbIpConstants.DirectionIn, transferLength: 1000 * 196));

            byte[] setCaptureInterfaceOff = Setup(0x01, UsbStandardRequest.SetInterface,
                value: 0, index: 2, length: 0);
            await stream.WriteAsync(EncodeSubmit(4, UsbIpConstants.DirectionOut,
                endpoint: 0, transferLength: 0, setCaptureInterfaceOff));
            SubmitReply altZeroReply = await ReadSubmitReplyAsync(stream,
                hasInputPayload: false);
            Require(altZeroReply.SequenceNumber == 4 && altZeroReply.Status == 0,
                nameof(TestIsochronousInWatchdogAsync));

            long watchdogStarted = Stopwatch.GetTimestamp();
            bool disconnectedWithoutData = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                byte[] oneByte = new byte[1];
                int received = await stream.ReadAsync(oneByte, timeout.Token);
                disconnectedWithoutData = received == 0;
            }
            catch (IOException)
            {
                disconnectedWithoutData = true;
            }
            catch (SocketException)
            {
                disconnectedWithoutData = true;
            }

            double watchdogElapsedMs = (Stopwatch.GetTimestamp() - watchdogStarted) *
                1000.0 / Stopwatch.Frequency;
            Require(disconnectedWithoutData && watchdogElapsedMs >= 600 &&
                    watchdogElapsedMs < 1800,
                nameof(TestIsochronousInWatchdogAsync));
            string fatalSessionLog = await fatalSessionLogTask.WaitAsync(
                TimeSpan.FromSeconds(2));
            Require(fatalSessionLog.StartsWith(
                    "FatalUsbIpSession: ISO IN quiesce watchdog",
                    StringComparison.Ordinal),
                nameof(TestIsochronousInWatchdogAsync));
        }

        using var reconnect = new TcpClient { NoDelay = true };
        using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await reconnect.ConnectAsync("127.0.0.1", port, reconnectTimeout.Token);
        NetworkStream reconnectStream = reconnect.GetStream();
        await reconnectStream.WriteAsync(
            EncodeOperation(UsbIpConstants.OpReqImport, "1-1"), reconnectTimeout.Token);
        byte[] reconnectReply = await UsbIpCodec.ReadExactlyAsync(reconnectStream, 8 + 312,
            reconnectTimeout.Token);
        Require(U16(reconnectReply, 2) == UsbIpConstants.OpRepImport &&
                U32(reconnectReply, 4) == 0,
            nameof(TestIsochronousInWatchdogAsync));
    }

    private static async Task TestDeviceListAsync(int port,
        IReadOnlyList<UsbInterfaceDescriptorInfo> expectedInterfaces)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqDevList));

        byte[] reply = await UsbIpCodec.ReadExactlyAsync(stream,
            12 + 312 + expectedInterfaces.Count * 4);
        Require(U16(reply, 0) == UsbIpConstants.Version &&
                U16(reply, 2) == UsbIpConstants.OpRepDevList &&
                U32(reply, 4) == 0 && U32(reply, 8) == 1,
            "OP_REP_DEVLIST header mismatch");
        Require(U16(reply, 12 + 256 + 32 + 12) == 0x054C &&
                U16(reply, 12 + 256 + 32 + 14) == 0x0CE6,
            "OP_REP_DEVLIST VID/PID mismatch");
        Require(reply[12 + 311] == expectedInterfaces.Count,
            "OP_REP_DEVLIST interface count mismatch");
        for (int i = 0; i < expectedInterfaces.Count; i++)
        {
            int offset = 12 + 312 + i * 4;
            UsbInterfaceDescriptorInfo expected = expectedInterfaces[i];
            Require(reply[offset] == expected.Class &&
                    reply[offset + 1] == expected.SubClass &&
                    reply[offset + 2] == expected.Protocol,
                $"OP_REP_DEVLIST interface {i} class mismatch");
        }
    }

    private static async Task TestImportSessionAsync(int port, Task<HidOutputCapture> captureTask)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));

        byte[] importReply = await UsbIpCodec.ReadExactlyAsync(stream, 8 + 312);
        Require(U16(importReply, 0) == UsbIpConstants.Version &&
                U16(importReply, 2) == UsbIpConstants.OpRepImport &&
                U32(importReply, 4) == 0,
            "OP_REP_IMPORT header mismatch");

        byte[] getDevice = Setup(0x80, UsbStandardRequest.GetDescriptor,
            value: 0x0100, index: 0, length: 18);
        await stream.WriteAsync(EncodeSubmit(1, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 18, getDevice));
        SubmitReply deviceReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(deviceReply.SequenceNumber == 1 && deviceReply.Status == 0 &&
                deviceReply.Data.Length == 18 && deviceReply.Data[1] == UsbDescriptorType.Device,
            "live EP0 device descriptor mismatch");

        byte[] getConfiguration = Setup(0x80, UsbStandardRequest.GetDescriptor,
            value: 0x0200, index: 0, length: 255);
        await stream.WriteAsync(EncodeSubmit(2, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 255, getConfiguration));
        SubmitReply configurationReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(configurationReply.SequenceNumber == 2 && configurationReply.Status == 0 &&
                configurationReply.Data.Length == 41 && configurationReply.Data[4] == 1 &&
                configurationReply.Data[11] == 0,
            "live EP0 HID-only configuration mismatch");

        byte[] getPairing = Setup(0xA1, UsbHidRequest.GetReport,
            value: 0x0309, index: 0, length: 20);
        await stream.WriteAsync(EncodeSubmit(20, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 20, getPairing));
        SubmitReply pairingReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(pairingReply.SequenceNumber == 20 && pairingReply.Status == 0 &&
                pairingReply.Data.Length == 20 && pairingReply.Data[0] == 0x09 &&
                pairingReply.Data[6] == 0x02,
            "virtual pairing feature report mismatch");

        byte[] getFirmware = Setup(0xA1, UsbHidRequest.GetReport,
            value: 0x0320, index: 0, length: 64);
        await stream.WriteAsync(EncodeSubmit(21, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 64, getFirmware));
        SubmitReply firmwareReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(firmwareReply.SequenceNumber == 21 && firmwareReply.Status == 0 &&
                firmwareReply.Data.Length == 64 && firmwareReply.Data[0] == 0x20,
            "firmware feature report mismatch");

        byte[] getCalibration = Setup(0xA1, UsbHidRequest.GetReport,
            value: 0x0305, index: 0, length: 41);
        await stream.WriteAsync(EncodeSubmit(22, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 41, getCalibration));
        SubmitReply calibrationReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(calibrationReply.SequenceNumber == 22 &&
                calibrationReply.Status == ControlResult.Stall && calibrationReply.ActualLength == 0,
            "unknown calibration feature report must stall");

        byte[] setConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 1, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(3, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setConfiguration));
        SubmitReply setReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(setReply.SequenceNumber == 3 && setReply.Status == 0 && setReply.ActualLength == 0,
            "SET_CONFIGURATION failed");

        byte[] outputReport = new byte[64];
        outputReport[0] = 0x02;
        outputReport[1] = 0xFF;
        outputReport[11] = 0x21;
        await stream.WriteAsync(EncodeSubmit(4, UsbIpConstants.DirectionOut,
            endpoint: 3, transferLength: outputReport.Length,
            setup: new byte[8], outputReport));
        SubmitReply outputReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        HidOutputCapture capture = await captureTask.WaitAsync(TimeSpan.FromSeconds(2));
        Require(outputReply.SequenceNumber == 4 && outputReply.Status == 0 &&
                outputReply.ActualLength == 64 && capture.Transport == "interrupt-out" &&
                capture.Data.AsSpan().SequenceEqual(outputReport),
            "interrupt OUT capture or completion mismatch");

        await stream.WriteAsync(EncodeSubmit(5, UsbIpConstants.DirectionIn,
            endpoint: 4, transferLength: 64, setup: new byte[8]));
        SubmitReply inputReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(inputReply.SequenceNumber == 5 && inputReply.Status == 0 &&
                inputReply.Data.Length == 64 && inputReply.Data[0] == 0x01 &&
                inputReply.Data[1] == 0x80 && inputReply.Data[8] == 0x08,
            "neutral interrupt IN report mismatch");

        await stream.WriteAsync(EncodeSubmit(6, UsbIpConstants.DirectionIn,
            endpoint: 4, transferLength: 64, setup: new byte[8]));
        await stream.WriteAsync(EncodeUnlink(7, unlinkSequence: 6));
        byte[] unlinkReply = await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkReply, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkReply, 4) == 7 && I32(unlinkReply, 20) == -104,
            "pending interrupt IN UNLINK mismatch");
    }

    private static byte[] EncodeOperation(ushort code, string? busId = null)
    {
        byte[] bytes = new byte[code == UsbIpConstants.OpReqImport ? 40 : 8];
        WriteU16(bytes, 0, UsbIpConstants.Version);
        WriteU16(bytes, 2, code);
        if (busId != null)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(busId);
            encoded.CopyTo(bytes, 8);
        }
        return bytes;
    }

    private static byte[] EncodeSubmit(uint sequence, uint direction, uint endpoint,
        int transferLength, byte[] setup, byte[]? outputData = null)
    {
        byte[] payload = outputData ?? Array.Empty<byte>();
        byte[] bytes = new byte[UsbIpConstants.UrbHeaderLength + payload.Length];
        WriteU32(bytes, 0, UsbIpConstants.CmdSubmit);
        WriteU32(bytes, 4, sequence);
        WriteU32(bytes, 8, DeviceId);
        WriteU32(bytes, 12, direction);
        WriteU32(bytes, 16, endpoint);
        WriteI32(bytes, 24, transferLength);
        WriteI32(bytes, 28, 0);
        WriteI32(bytes, 32, -1);
        WriteI32(bytes, 36, endpoint is 3 or 4 ? 6 : 0);
        setup.CopyTo(bytes, 40);
        payload.CopyTo(bytes, UsbIpConstants.UrbHeaderLength);
        return bytes;
    }

    private static byte[] EncodeIsoSubmit(uint sequence, uint endpoint, int startFrame,
        int interval, byte[] outputData, IReadOnlyList<UsbIpIsoPacket> packets,
        uint direction = UsbIpConstants.DirectionOut, int? transferLength = null)
    {
        byte[] bytes = new byte[UsbIpConstants.UrbHeaderLength + outputData.Length +
            packets.Count * UsbIpConstants.IsoDescriptorLength];
        WriteU32(bytes, 0, UsbIpConstants.CmdSubmit);
        WriteU32(bytes, 4, sequence);
        WriteU32(bytes, 8, DeviceId);
        WriteU32(bytes, 12, direction);
        WriteU32(bytes, 16, endpoint);
        WriteI32(bytes, 24, transferLength ?? outputData.Length);
        WriteI32(bytes, 28, startFrame);
        WriteI32(bytes, 32, packets.Count);
        WriteI32(bytes, 36, interval);
        outputData.CopyTo(bytes, UsbIpConstants.UrbHeaderLength);
        int descriptorOffset = UsbIpConstants.UrbHeaderLength + outputData.Length;
        for (int i = 0; i < packets.Count; i++)
        {
            UsbIpIsoPacket packet = packets[i];
            int offset = descriptorOffset + i * UsbIpConstants.IsoDescriptorLength;
            WriteU32(bytes, offset, packet.Offset);
            WriteU32(bytes, offset + 4, packet.Length);
            WriteU32(bytes, offset + 8, packet.ActualLength);
            WriteI32(bytes, offset + 12, packet.Status);
        }
        return bytes;
    }

    private static byte[] EncodeUnlink(uint sequence, uint unlinkSequence)
    {
        byte[] bytes = new byte[UsbIpConstants.UrbHeaderLength];
        WriteU32(bytes, 0, UsbIpConstants.CmdUnlink);
        WriteU32(bytes, 4, sequence);
        WriteU32(bytes, 8, DeviceId);
        WriteU32(bytes, 20, unlinkSequence);
        return bytes;
    }

    private static byte[] Setup(byte requestType, byte request,
        ushort value, ushort index, ushort length)
    {
        byte[] setup = new byte[8];
        setup[0] = requestType;
        setup[1] = request;
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(2, 2), value);
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(4, 2), index);
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(6, 2), length);
        return setup;
    }

    private static async Task<SubmitReply> ReadSubmitReplyAsync(Stream stream, bool hasInputPayload)
    {
        byte[] header = await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength);
        Require(U32(header, 0) == UsbIpConstants.RetSubmit, "RET_SUBMIT command mismatch");
        int actualLength = I32(header, 24);
        byte[] data = hasInputPayload && actualLength > 0
            ? await UsbIpCodec.ReadExactlyAsync(stream, actualLength)
            : Array.Empty<byte>();
        return new SubmitReply(U32(header, 4), I32(header, 20), actualLength, data);
    }

    private static async Task<IsoSubmitReply> ReadIsoSubmitReplyAsync(Stream stream)
    {
        return await ReadIsoReplyCoreAsync(stream, readInputData: false);
    }

    /// <summary>Reads an isochronous IN RET_SUBMIT: header, then actual_length
    /// bytes of compact packet data, then the trailing descriptors.</summary>
    private static async Task<IsoSubmitReply> ReadIsoInSubmitReplyAsync(Stream stream)
    {
        return await ReadIsoReplyCoreAsync(stream, readInputData: true);
    }

    private static async Task<IsoSubmitReply> ReadIsoReplyCoreAsync(Stream stream,
        bool readInputData)
    {
        byte[] header = await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength);
        Require(U32(header, 0) == UsbIpConstants.RetSubmit, "ISO RET_SUBMIT command mismatch");
        int actualLength = I32(header, 24);
        byte[] data = readInputData && actualLength > 0
            ? await UsbIpCodec.ReadExactlyAsync(stream, actualLength)
            : Array.Empty<byte>();
        int numberOfPackets = I32(header, 32);
        byte[] descriptors = await UsbIpCodec.ReadExactlyAsync(stream,
            numberOfPackets * UsbIpConstants.IsoDescriptorLength);
        var packets = new UsbIpIsoPacket[numberOfPackets];
        for (int i = 0; i < packets.Length; i++)
        {
            int offset = i * UsbIpConstants.IsoDescriptorLength;
            packets[i] = new UsbIpIsoPacket(
                U32(descriptors, offset),
                U32(descriptors, offset + 4),
                U32(descriptors, offset + 8),
                I32(descriptors, offset + 12));
        }
        return new IsoSubmitReply(
            U32(header, 4),
            I32(header, 20),
            actualLength,
            I32(header, 28),
            I32(header, 36),
            packets,
            data);
    }

    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private static int I32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);
    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
    private static void WriteI32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, 4), value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record SubmitReply(
        uint SequenceNumber,
        int Status,
        int ActualLength,
        byte[] Data);

    private sealed record IsoSubmitReply(
        uint SequenceNumber,
        int Status,
        int ActualLength,
        int StartFrame,
        int ErrorCount,
        IReadOnlyList<UsbIpIsoPacket> Packets,
        byte[] Data);
}
