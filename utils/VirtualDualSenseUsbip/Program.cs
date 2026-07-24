// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using VirtualDualSenseUsbip.Device;
using VirtualDualSenseUsbip.Live;
using VirtualDualSenseUsbip.Protocol;

const string NativeModeProtocolCapability =
    "DS4WINDOWS_NATIVE_USBIP_PROTOCOL=2";

if (args.Length == 1 &&
    args[0].Equals("capabilities", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(NativeModeProtocolCapability);
    return;
}

if (args.Length >= 1 && args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase))
{
    await UsbIpSelfTest.RunAsync();
    return;
}

if (args.Length >= 1 && args[0].Equals("devicetest", StringComparison.OrdinalIgnoreCase))
{
    string fixtures = args.Length >= 2
        ? args[1]
        : DefaultFixturesPath();
    Environment.Exit(DeviceSelfTest.Run(Path.GetFullPath(fixtures)));
}

if (args.Length >= 1 && args[0].Equals("servertest", StringComparison.OrdinalIgnoreCase))
{
    string fixtures = args.Length >= 2 ? args[1] : DefaultFixturesPath();
    await LiveServerSelfTest.RunAsync(Path.GetFullPath(fixtures));
    return;
}

if (args.Length >= 1 && args[0].Equals("lifecycletest", StringComparison.OrdinalIgnoreCase))
{
    await NativeModeContainmentSelfTest.RunAsync();
    return;
}

if (args.Length >= 1 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
{
    Console.SetOut(new BrokenPipeTolerantTextWriter(Console.Out));
    Console.SetError(new BrokenPipeTolerantTextWriter(Console.Error));

    string? protocolVersion = GetOption(args, "--protocol-version");
    if (!string.Equals(protocolVersion, "2", StringComparison.Ordinal))
    {
        throw new ArgumentException(
            "--protocol-version 2 is required for a DS4Windows-managed server.");
    }
    string? controlStdin = GetOption(args, "--control-stdin");
    if (!string.Equals(controlStdin, "required", StringComparison.Ordinal) ||
        !Console.IsInputRedirected)
    {
        throw new ArgumentException(
            "--control-stdin required with redirected standard input is required " +
            "for a DS4Windows-managed server.");
    }
    Task<NativeModeControlLeaseResult> controlLease =
        NativeModeControlLease.Start(Console.In);

    string fixtures = DefaultFixturesPath();
    string busId = GetOption(args, "--busid") ?? "1-1";
    string inputMode = GetOption(args, "--input") ?? string.Empty;
    string configurationMode = GetOption(args, "--configuration") ?? string.Empty;
    if (!inputMode.Equals("bluetooth", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException(
            "--input bluetooth is required for a DS4Windows-managed server.");
    }
    if (!configurationMode.Equals("composite", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException(
            "--configuration composite is required for Native Mode.");
    }
    bool speakerAudio = ParseOnOff(GetOption(args, "--speaker-audio"), defaultValue: false);
    int speakerVolume = ParsePercentage(GetOption(args, "--speaker-volume"),
        defaultValue: 100, "--speaker-volume");
    SpeakerAudioRoute audioRoute = (GetOption(args, "--route") ?? "auto").ToLowerInvariant() switch
    {
        "auto" => SpeakerAudioRoute.Auto,
        "speaker" => SpeakerAudioRoute.Speaker,
        "headphone" => SpeakerAudioRoute.Headphone,
        _ => throw new ArgumentException("--route must be 'auto', 'speaker', or 'headphone'."),
    };
    const string suggestedSerial = "DS4WSPKCOMP001";
    int port = ParsePort(GetOption(args, "--port"));

    string fullFixturesPath = Path.GetFullPath(fixtures);
    DescriptorSet descriptors = DescriptorSet.LoadFromFixtures(fullFixturesPath);
    using BluetoothDualSenseInputSource bluetoothInput =
        BluetoothDualSenseInputSource.Open(
            ParseBluetoothDeviceIdentity(args),
            message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"),
            new BluetoothAudioOptions(speakerAudio, Microphone: false, audioRoute,
                SpeakerGain: speakerVolume / 100.0f))
        ?? throw new InvalidOperationException(
            "The selected Bluetooth DualSense could not be opened.");
    IInputReportSource inputReports = bluetoothInput;
    FeatureReportSet featureReports =
        FeatureReportSet.CreateVirtualDefaults(bluetoothInput.CalibrationFeatureReport);
    using var server = new VirtualDualSenseServer(descriptors,
        new VirtualDualSenseServerOptions(port, busId), inputReports, featureReports);
    server.Log += message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    object captureGate = new();
    long hidOutputCount = 0;
    byte[]? lastLoggedTriggerBlock = null;
    long isoOutCount = 0;
    long isoOutBytes = 0;
    long isoOutPackets = 0;
    long firstIsoTimestamp = 0;
    long previousIsoTimestamp = 0;
    double minimumIsoGapMs = double.PositiveInfinity;
    double maximumIsoGapMs = 0;
    double[] isoWindowSumSquares = new double[4];
    int[] isoWindowPeaks = new int[4];
    long isoWindowFrames = 0;
    server.HidOutputReceived += capture =>
    {
        _ = bluetoothInput.QueueTriggerReport(capture.Data);
        string hex = Convert.ToHexString(capture.Data);
        bool triggerChanged = false;
        long outputNumber;
        lock (captureGate)
        {
            outputNumber = ++hidOutputCount;
            if (capture.Data.Length >= 33 && capture.Data[0] == 0x02 &&
                (capture.Data[1] & 0x0C) != 0)
            {
                byte[] triggerState = new byte[23];
                triggerState[0] = (byte)(capture.Data[1] & 0x0C);
                capture.Data.AsSpan(11, 22).CopyTo(triggerState.AsSpan(1));
                if (lastLoggedTriggerBlock == null ||
                    !triggerState.AsSpan().SequenceEqual(lastLoggedTriggerBlock))
                {
                    lastLoggedTriggerBlock = triggerState;
                    triggerChanged = true;
                }
            }
        }

        if (outputNumber == 1 || triggerChanged || outputNumber % 5000 == 0)
        {
            Console.WriteLine($"{capture.Timestamp.ToLocalTime():HH:mm:ss.fff} HID OUT " +
                $"total={outputNumber} seq={capture.SequenceNumber} via={capture.Transport} " +
                $"bytes={capture.Data.Length} {hex[..Math.Min(hex.Length, 64)]}");
        }
    };

    server.IsochronousOutReceived += capture =>
    {
        _ = bluetoothInput.QueueHapticAudio(capture.Data);
        double[] captureSumSquares = new double[4];
        int[] capturePeaks = new int[4];
        int captureFrames = capture.Data.Length % 8 == 0 ? capture.Data.Length / 8 : 0;
        for (int frame = 0; frame < captureFrames; frame++)
        {
            int offset = frame * 8;
            for (int channel = 0; channel < 4; channel++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(
                    capture.Data.AsSpan(offset + channel * 2, 2));
                double normalized = sample / 32768.0;
                captureSumSquares[channel] += normalized * normalized;
                capturePeaks[channel] = Math.Max(capturePeaks[channel], Math.Abs((int)sample));
            }
        }

        long count;
        long totalBytes;
        long totalPackets;
        long firstTimestamp;
        double minGapMs;
        double maxGapMs;
        double[] rmsPercent = new double[4];
        double[] peakPercent = new double[4];
        lock (captureGate)
        {
            double incomingGapMs = previousIsoTimestamp == 0
                ? 0
                : (capture.MonotonicTimestamp - previousIsoTimestamp) * 1000.0 /
                  Stopwatch.Frequency;
            if (incomingGapMs > 100)
            {
                isoOutCount = 0;
                isoOutBytes = 0;
                isoOutPackets = 0;
                firstIsoTimestamp = 0;
                minimumIsoGapMs = double.PositiveInfinity;
                maximumIsoGapMs = 0;
                Array.Clear(isoWindowSumSquares);
                Array.Clear(isoWindowPeaks);
                isoWindowFrames = 0;
            }

            count = ++isoOutCount;
            isoOutBytes += capture.Data.Length;
            isoOutPackets += capture.Packets.Count;
            totalBytes = isoOutBytes;
            totalPackets = isoOutPackets;
            if (firstIsoTimestamp == 0)
            {
                firstIsoTimestamp = capture.MonotonicTimestamp;
            }
            if (previousIsoTimestamp != 0 && incomingGapMs <= 100)
            {
                minimumIsoGapMs = Math.Min(minimumIsoGapMs, incomingGapMs);
                maximumIsoGapMs = Math.Max(maximumIsoGapMs, incomingGapMs);
            }
            previousIsoTimestamp = capture.MonotonicTimestamp;
            firstTimestamp = firstIsoTimestamp;
            minGapMs = minimumIsoGapMs;
            maxGapMs = maximumIsoGapMs;
            for (int channel = 0; channel < 4; channel++)
            {
                isoWindowSumSquares[channel] += captureSumSquares[channel];
                isoWindowPeaks[channel] = Math.Max(isoWindowPeaks[channel], capturePeaks[channel]);
            }
            isoWindowFrames += captureFrames;
            if (count == 1 || count % 500 == 0)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    rmsPercent[channel] = isoWindowFrames > 0
                        ? Math.Sqrt(isoWindowSumSquares[channel] / isoWindowFrames) * 100.0
                        : 0;
                    peakPercent[channel] = isoWindowPeaks[channel] * 100.0 / 32768.0;
                    isoWindowSumSquares[channel] = 0;
                    isoWindowPeaks[channel] = 0;
                }
                isoWindowFrames = 0;
            }
        }

        if (count == 1 || count % 500 == 0)
        {
            double elapsedSeconds = Math.Max(0.000001,
                (capture.MonotonicTimestamp - firstTimestamp) / (double)Stopwatch.Frequency);
            uint minPacket = capture.Packets.Min(packet => packet.Length);
            uint maxPacket = capture.Packets.Max(packet => packet.Length);
            string gapRange = double.IsPositiveInfinity(minGapMs)
                ? "n/a"
                : $"{minGapMs:0.###}..{maxGapMs:0.###}";
            string rate = count == 1
                ? "urbs/s=n/a KiB/s=n/a"
                : $"urbs/s={count / elapsedSeconds:0.0} KiB/s={totalBytes / elapsedSeconds / 1024:0.0}";
            string bluetoothStats =
                $" bt36={bluetoothInput.RelayedHapticReportCount} " +
                $"btq={bluetoothInput.HapticQueueBytes} " +
                $"bt-underrun={bluetoothInput.HapticUnderrunCount} " +
                $"bt-errors={bluetoothInput.HapticWriteErrorCount}" +
                (bluetoothInput.RelayedAudioReportCount > 0
                    ? $" bt-audio={bluetoothInput.RelayedAudioReportCount} " +
                      $"spkq={bluetoothInput.SpeakerQueueFrames} " +
                      $"spk-underrun={bluetoothInput.SpeakerUnderrunCount}"
                    : string.Empty);
            Console.WriteLine($"{capture.Timestamp.ToLocalTime():HH:mm:ss.fff} ISO OUT " +
                $"total={count} seq={capture.SequenceNumber} ep={capture.Endpoint} " +
                $"{rate} " +
                $"packets={totalPackets} current={capture.Packets.Count}x{minPacket}..{maxPacket} " +
                $"gap-ms={gapRange} rms%={string.Join('/', rmsPercent.Select(value => value.ToString("0.00", CultureInfo.InvariantCulture)))} " +
                $"peak%={string.Join('/', peakPercent.Select(value => value.ToString("0.0", CultureInfo.InvariantCulture)))} " +
                $"start={capture.StartFrame} interval={capture.Interval}{bluetoothStats}");
        }
    };

    var renderKeepalive = new NativeModeChildRenderKeepalive(
        message => Console.WriteLine(message));
    bool renderSessionBegun = false;
    Task? serverTask = null;
    Exception? terminalFailure = null;
    var consoleStop = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        consoleStop.TrySetResult(true);
    };
    Console.CancelKeyPress += cancelHandler;

    Task? audioStats = null;
    if (speakerAudio)
    {
        audioStats = Task.Run(async () =>
        {
            long lastAudio = 0;
            while (!cancellation.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                long audio = bluetoothInput.RelayedAudioReportCount;
                bool audioActive = audio != lastAudio;
                lastAudio = audio;
                if (!audioActive)
                {
                    continue;
                }

                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} AUDIO " +
                    $"bt-audio={audio} spkq={bluetoothInput.SpeakerQueueFrames} " +
                    $"spk-underrun={bluetoothInput.SpeakerUnderrunCount} " +
                    $"spk-dropped={bluetoothInput.SpeakerDroppedFrames} " +
                    $"bt-errors={bluetoothInput.HapticWriteErrorCount}");
            }
        });
    }

    try
    {
        if (controlLease.IsCompleted)
        {
            ReportControlLeaseEnd(await controlLease);
            return;
        }

        // Capture the endpoint baseline before the USB/IP listener is visible.
        // This child-owned pin is redundant with the parent pin by design.
        renderSessionBegun = true;
        renderKeepalive.BeginSession();
        if (controlLease.IsCompleted)
        {
            ReportControlLeaseEnd(await controlLease);
            return;
        }

        server.Start();
        Console.WriteLine($"Virtual DualSense ({configurationMode} configuration) is ready for usbip-win2.");
        if (speakerAudio)
        {
            Console.WriteLine("Audio relay: speaker=on " +
                $"speaker-volume={speakerVolume}% " +
                $"route={audioRoute.ToString().ToLowerInvariant()}");
        }
        Console.WriteLine($"Attach from an elevated terminal:");
        Console.WriteLine($"  usbip attach -r 127.0.0.1 -b {busId} --serial {suggestedSerial} --once");
        serverTask = server.RunAsync(cancellation.Token);
        Task<Exception> renderFailure = renderKeepalive.WaitForFailureAsync();
        Task completed = await Task.WhenAny(
            serverTask,
            controlLease,
            renderFailure,
            consoleStop.Task);

        if (ReferenceEquals(completed, consoleStop.Task))
        {
            Console.WriteLine("NativeControlLeaseEnded: console stop requested.");
        }
        else if (ReferenceEquals(completed, controlLease))
        {
            NativeModeControlLeaseResult result = await controlLease;
            ReportControlLeaseEnd(result);
        }
        else if (ReferenceEquals(completed, renderFailure))
        {
            terminalFailure = await renderFailure;
        }
        else
        {
            try
            {
                await serverTask;
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or SocketException)
            {
                if (!cancellation.IsCancellationRequested)
                    terminalFailure = ex;
            }
            catch (Exception ex)
            {
                terminalFailure = ex;
            }

            if (terminalFailure == null &&
                !cancellation.IsCancellationRequested)
            {
                terminalFailure = new InvalidOperationException(
                    "The USB/IP server stopped without a control-lease signal.");
            }
        }
    }
    catch (Exception ex)
    {
        terminalFailure ??= ex;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        // Refuse a second attach before evaluating the shutdown barrier. This
        // closes only the listening socket; an active USB/IP session remains
        // alive until cancellation below, under the healthy render lease.
        server.StopAccepting();
        if (renderSessionBegun)
        {
            // If attach raced stop/EOF, keep discovery and any active USB/IP
            // session alive until this process owns a healthy render pin or
            // the exact parent is already absent. Probe failure deliberately
            // blocks here.
            await renderKeepalive.WaitForShutdownSafetyAsync(
                () => server.RequiresHealthyRenderLeaseForShutdown,
                CancellationToken.None);
            renderKeepalive.BeginTeardown();
        }
        // Cancellation now propagates through every active request pump.
        // Awaiting serverTask proves the session and its socket have quiesced
        // while the render pin is still held.
        cancellation.Cancel();
        if (serverTask != null)
        {
            try
            {
                await serverTask;
                Console.WriteLine(
                    "NativeUsbIpSessionClosed: listener stopped, requests " +
                    "quiesced, and the active socket closed.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(
                    "NativeUsbIpSessionClosed: listener stopped, requests " +
                    "quiesced, and the active socket closed.");
            }
            catch (SocketException) when (cancellation.IsCancellationRequested)
            {
                Console.WriteLine(
                    "NativeUsbIpSessionClosed: listener stopped, requests " +
                    "quiesced, and the active socket closed.");
            }
            catch (Exception ex)
            {
                terminalFailure ??= ex;
            }
        }
        if (audioStats != null)
        {
            await audioStats;
        }
        if (renderSessionBegun)
        {
            // No timeout in production. An inconclusive PnP/audio probe means
            // this helper intentionally remains alive retaining the pin.
            await renderKeepalive.RetainUntilRemovedAndReleaseAsync(
                CancellationToken.None);
        }
    }

    if (terminalFailure != null)
    {
        throw new InvalidOperationException(
            "Native Mode helper containment completed after a failure.",
            terminalFailure);
    }
    return;
}

Console.WriteLine("VirtualDualSenseUsbip development and diagnostic commands");
Console.WriteLine("  capabilities          print the DS4Windows helper protocol capability");
Console.WriteLine("  selftest              USB/IP protocol golden vectors + fragmentation");
Console.WriteLine("  devicetest [fixtures] replay captured EP0 enumeration byte-exact");
Console.WriteLine("  servertest [fixtures] exercise the live server over loopback TCP");
Console.WriteLine("  lifecycletest         control-lease and render-retention checks");
Console.WriteLine("  serve [--port 3240] [--busid 1-1]");
Console.WriteLine("        --protocol-version 2 --control-stdin required");
Console.WriteLine("        --input bluetooth --configuration composite");
Console.WriteLine("        --device-path PATH --device-vid VID --device-pid PID");
Console.WriteLine("        [--speaker-audio on|off] [--speaker-volume 0..100]");
Console.WriteLine("        [--route auto|speaker|headphone]");

static string DefaultFixturesPath() =>
    Path.Combine(AppContext.BaseDirectory, "Descriptors");

static BluetoothDualSenseIdentity ParseBluetoothDeviceIdentity(
    string[] arguments)
{
    string? devicePath = GetOption(arguments, "--device-path");
    if (string.IsNullOrWhiteSpace(devicePath))
    {
        throw new ArgumentException(
            "--device-path is required when opening a Bluetooth controller.");
    }

    return new BluetoothDualSenseIdentity(devicePath.Trim(),
        ParseUsbIdentifier(GetOption(arguments, "--device-vid"), "--device-vid"),
        ParseUsbIdentifier(GetOption(arguments, "--device-pid"), "--device-pid"));
}

static void ReportControlLeaseEnd(NativeModeControlLeaseResult result)
{
    Console.WriteLine(result.Signal switch
    {
        NativeModeControlSignal.StopRequested =>
            "NativeControlLeaseEnded: stop requested.",
        NativeModeControlSignal.ParentPipeClosed =>
            "NativeControlLeaseEnded: parent pipe closed.",
        _ => "NativeControlLeaseEnded: protocol violation.",
    });
    if (result.Signal == NativeModeControlSignal.ProtocolViolation)
    {
        throw new InvalidOperationException(
            result.Detail ?? "The Native Mode control lease failed.");
    }
}

static int ParseUsbIdentifier(string? value, string optionName)
{
    if (!ushort.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out ushort identifier))
    {
        throw new ArgumentException(
            $"{optionName} must be a decimal USB identifier from 0 to 65535.");
    }

    return identifier;
}

static string? GetOption(string[] arguments, string name)
{
    for (int i = 1; i < arguments.Length; i++)
    {
        if (!arguments[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        if (i + 1 >= arguments.Length)
        {
            throw new ArgumentException($"{name} requires a value.");
        }
        return arguments[i + 1];
    }
    return null;
}

static bool ParseOnOff(string? value, bool defaultValue)
{
    if (value == null)
    {
        return defaultValue;
    }
    return value.ToLowerInvariant() switch
    {
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => throw new ArgumentException($"Expected 'on' or 'off', got '{value}'."),
    };
}

static int ParsePercentage(string? value, int defaultValue, string optionName)
{
    if (value == null)
    {
        return defaultValue;
    }
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int percentage) || percentage is < 0 or > 100)
    {
        throw new ArgumentOutOfRangeException(optionName,
            $"{optionName} must be between 0 and 100.");
    }
    return percentage;
}

static int ParsePort(string? value)
{
    if (value == null)
    {
        return 3240;
    }
    if (!int.TryParse(value, out int port) || port is < 1024 or > 65535)
    {
        throw new ArgumentOutOfRangeException(nameof(value), "Port must be between 1024 and 65535.");
    }
    return port;
}
