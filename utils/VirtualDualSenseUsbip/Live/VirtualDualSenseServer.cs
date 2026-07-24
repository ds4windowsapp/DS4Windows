// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VirtualDualSenseUsbip.Device;
using VirtualDualSenseUsbip.Protocol;

namespace VirtualDualSenseUsbip.Live;

public sealed record VirtualDualSenseServerOptions(
    int Port = 3240,
    string BusId = "1-1",
    TimeSpan? InputInterval = null,
    TimeSpan? IsochronousOutQuiesceTimeout = null)
{
    public TimeSpan EffectiveInputInterval => InputInterval ?? TimeSpan.FromMilliseconds(4);
    public TimeSpan EffectiveIsochronousOutQuiesceTimeout =>
        IsochronousOutQuiesceTimeout ?? TimeSpan.FromSeconds(1);
}

public sealed record HidOutputCapture(
    DateTimeOffset Timestamp,
    uint SequenceNumber,
    string Transport,
    byte[] Data);

public sealed record IsochronousOutCapture(
    DateTimeOffset Timestamp,
    long MonotonicTimestamp,
    uint SequenceNumber,
    uint Endpoint,
    int StartFrame,
    int Interval,
    byte[] Data,
    IReadOnlyList<UsbIpIsoPacket> Packets);

/// <summary>
/// Local USB/IP v1.1.1 server exporting one synthetic wired DualSense.
/// Management clients receive DEVLIST/IMPORT records; after a successful import
/// the connection becomes a USB/IP URB session until detach/disconnect.
/// </summary>
public sealed class VirtualDualSenseServer : IDisposable
{
    private readonly DescriptorSet descriptors;
    private readonly VirtualDualSenseServerOptions options;
    private readonly TcpListener listener;
    private readonly UsbIpDeviceInfo deviceInfo;
    private readonly IInputReportSource inputReports;
    private readonly FeatureReportSet featureReports;
    private readonly object importStateGate = new();
    private WindowsTimerResolution? timerResolution;
    private bool started;
    private int acceptingStopped;
    private int acceptedClientCount;
    private int importReservations;
    private bool importReplyCommittedSinceStart;

    public int Port { get; private set; }
    internal int AcceptedClientCount =>
        Volatile.Read(ref acceptedClientCount);
    internal bool RequiresHealthyRenderLeaseForShutdown
    {
        get
        {
            lock (importStateGate)
                return importReservations != 0 ||
                    importReplyCommittedSinceStart;
        }
    }
    public event Action<string>? Log;
    public event Action<HidOutputCapture>? HidOutputReceived;
    public event Action<IsochronousOutCapture>? IsochronousOutReceived;

    public VirtualDualSenseServer(DescriptorSet descriptors,
        VirtualDualSenseServerOptions? options = null,
        IInputReportSource? inputReports = null,
        FeatureReportSet? featureReports = null)
    {
        this.descriptors = descriptors;
        this.options = options ?? new VirtualDualSenseServerOptions();
        this.inputReports = inputReports ?? new NeutralInputReportSource();
        this.featureReports = featureReports ?? FeatureReportSet.CreateVirtualDefaults();
        listener = new TcpListener(IPAddress.Loopback, this.options.Port);
        deviceInfo = new UsbIpDeviceInfo(
            "/virtual/ds4windows/dualsense-hid",
            this.options.BusId,
            BusNumber: 1,
            DeviceNumber: 1,
            Speed: 3, // USB_SPEED_HIGH; matches the captured wired controller
            descriptors.VendorId,
            descriptors.ProductId,
            descriptors.DeviceBcd,
            DeviceClass: 0,
            DeviceSubClass: 0,
            DeviceProtocol: 0,
            ConfigurationValue: 0,
            descriptors.NumConfigurations,
            descriptors.NumInterfaces,
            descriptors.Interfaces.Select(info =>
                new UsbIpInterfaceInfo(info.Class, info.SubClass, info.Protocol)).ToArray());
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(listener.Server.SafeHandle.IsClosed, this);
        if (started)
        {
            throw new InvalidOperationException("The USB/IP server has already started.");
        }

        timerResolution = WindowsTimerResolution.Begin(1);
        try
        {
            listener.Start();
        }
        catch
        {
            timerResolution.Dispose();
            timerResolution = null;
            throw;
        }
        started = true;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        EmitLog($"USB/IP server listening on 127.0.0.1:{Port}; busid {options.BusId}; " +
            $"input {inputReports.Description}.");
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
        {
            throw new InvalidOperationException("Call Start before RunAsync.");
        }

        using CancellationTokenRegistration registration =
            cancellationToken.Register(StopAccepting);
        while (!cancellationToken.IsCancellationRequested &&
            Volatile.Read(ref acceptingStopped) == 0)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested ||
                Volatile.Read(ref acceptingStopped) != 0)
            {
                break;
            }
            catch (SocketException) when (
                cancellationToken.IsCancellationRequested ||
                Volatile.Read(ref acceptingStopped) != 0)
            {
                break;
            }
            catch (ObjectDisposedException) when (
                cancellationToken.IsCancellationRequested ||
                Volatile.Read(ref acceptingStopped) != 0)
            {
                break;
            }

            Interlocked.Increment(ref acceptedClientCount);
            using (client)
            {
                client.NoDelay = true;
                try
                {
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    EmitLog("USB/IP client disconnected.");
                }
                catch (IOException ex)
                {
                    EmitLog($"USB/IP connection ended: {ex.Message}");
                }
                catch (SocketException ex)
                {
                    EmitLog($"USB/IP socket ended: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Stops new attach/re-attach connections without disturbing the active
    /// USB/IP socket. Ordered containment calls this before canceling the
    /// current session.
    /// </summary>
    public void StopAccepting()
    {
        lock (importStateGate)
        {
            if (acceptingStopped != 0)
                return;
            Volatile.Write(ref acceptingStopped, 1);
        }

        try
        {
            listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Dispose and cancellation may converge on the same listener.
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream stream = client.GetStream();
        await using var writer = new SerializedStreamWriter(stream);
        UsbIpOperation operation = await UsbIpCodec.ReadOperationAsync(stream, cancellationToken);

        if (operation.Version != UsbIpConstants.Version || operation.Status != 0)
        {
            ushort replyCode = operation.Code == UsbIpConstants.OpReqImport
                ? UsbIpConstants.OpRepImport
                : UsbIpConstants.OpRepDevList;
            await writer.WriteAsync(
                UsbIpCodec.EncodeOperationReply(replyCode, status: 1), cancellationToken);
            EmitLog($"Rejected USB/IP operation version 0x{operation.Version:X4}, " +
                $"code 0x{operation.Code:X4}, status {operation.Status}.");
            return;
        }

        switch (operation.Code)
        {
            case UsbIpConstants.OpReqDevList:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepDevList, 0,
                        deviceInfo, includeInterfaceList: true), cancellationToken);
                EmitLog($"Served OP_REP_DEVLIST for a {descriptors.NumInterfaces}-interface DualSense.");
                return;

            case UsbIpConstants.OpReqImport when operation.BusId == options.BusId:
                if (!TryReserveImport())
                {
                    await writer.WriteAsync(
                        UsbIpCodec.EncodeOperationReply(
                            UsbIpConstants.OpRepImport, status: 1),
                        cancellationToken);
                    EmitLog($"Rejected import for busid {options.BusId} " +
                        "because shutdown has begun.");
                    return;
                }
                bool importReplyCommitted = false;
                try
                {
                    await writer.WriteAsync(
                        UsbIpCodec.EncodeOperationReply(
                            UsbIpConstants.OpRepImport, 0, deviceInfo),
                        cancellationToken);
                    CommitImportReply();
                    importReplyCommitted = true;
                    EmitLog($"Imported busid {options.BusId}; beginning live URB session.");
                    var session = new UsbIpDeviceSession(
                        stream, writer, descriptors, featureReports, inputReports,
                        options.EffectiveInputInterval,
                        options.EffectiveIsochronousOutQuiesceTimeout,
                        capture => HidOutputReceived?.Invoke(capture),
                        capture => IsochronousOutReceived?.Invoke(capture), EmitLog);
                    await session.RunAsync(cancellationToken);
                }
                finally
                {
                    if (!importReplyCommitted)
                        AbandonImportReservation();
                }
                return;

            case UsbIpConstants.OpReqImport:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepImport, status: 1),
                    cancellationToken);
                EmitLog($"Rejected unknown busid '{operation.BusId}'.");
                return;

            default:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepDevList, status: 1),
                    cancellationToken);
                EmitLog($"Rejected unsupported operation 0x{operation.Code:X4}.");
                return;
        }
    }

    private void EmitLog(string message) => Log?.Invoke(message);

    private bool TryReserveImport()
    {
        lock (importStateGate)
        {
            if (acceptingStopped != 0)
                return false;
            importReservations++;
            return true;
        }
    }

    private void CommitImportReply()
    {
        lock (importStateGate)
        {
            if (importReservations <= 0)
                throw new InvalidOperationException(
                    "The USB/IP import reservation was lost.");
            importReservations--;
            importReplyCommittedSinceStart = true;
        }
    }

    private void AbandonImportReservation()
    {
        lock (importStateGate)
        {
            if (importReservations <= 0)
                throw new InvalidOperationException(
                    "The USB/IP import reservation was lost.");
            importReservations--;
        }
    }

    public void Dispose()
    {
        StopAccepting();
        timerResolution?.Dispose();
        timerResolution = null;
        GC.SuppressFinalize(this);
    }
}

internal sealed class UsbIpDeviceSession
{
    private const uint DeviceId = 0x00010001;
    private const uint HidInterruptOutEndpoint = 3;
    private const uint HidInterruptInEndpoint = 4;
    private const int MaxPendingIsochronousTransfers = 64;
    private const int MaxPendingIsochronousInTransfers = 32;
    private const int MaxPendingIsochronousTotal = 96;
    private const int ConnectionReset = -104; // -ECONNRESET

    // 48 kHz stereo signed 16-bit capture: one USB frame of audio per packet.
    private const int MicrophoneNominalPacketBytes = 192;

    // UAC feature-unit entity ids from the captured configuration: 2 sits in
    // the playback path (IT1 -> FU2 -> OT3), 5 in the capture path
    // (IT4 -> FU5 -> OT6).
    private const byte PlaybackFeatureUnit = 2;
    private const byte CaptureFeatureUnit = 5;

    private readonly Stream stream;
    private readonly SerializedStreamWriter writer;
    private readonly ControlEndpoint controlEndpoint;
    private readonly IInputReportSource inputReports;
    private readonly TimeSpan inputInterval;
    private readonly TimeSpan isochronousOutQuiesceTimeout;
    private readonly Action<HidOutputCapture> captureOutput;
    private readonly Action<IsochronousOutCapture> captureIsochronousOut;
    private readonly Action<string> log;
    private readonly IReadOnlyDictionary<uint, UsbEndpointDescriptorInfo> isochronousOutEndpoints;
    private readonly IReadOnlyDictionary<uint, UsbEndpointDescriptorInfo> isochronousInEndpoints;
    private readonly IUsbAudioRelay? audioRelay;
    private readonly ConcurrentDictionary<uint, PendingTransfer> pendingInput = new();
    private readonly ConcurrentQueue<uint> pendingInputOrder = new();
    private readonly ConcurrentDictionary<uint, PendingTransfer> pendingIsochronous = new();
    private readonly ConcurrentQueue<uint> pendingIsochronousOrder = new();
    private readonly SemaphoreSlim pendingIsochronousSignal = new(0);
    private readonly SemaphoreSlim isochronousLifecycleGate = new(1, 1);
    private readonly ConcurrentQueue<QuiescedIsochronousGeneration> quiescedIsochronousOut = new();
    private readonly SemaphoreSlim quiescedIsochronousOutSignal = new(0);
    private readonly Dictionary<byte, IsochronousOutInterfaceState> isochronousOutInterfaces = new();
    private readonly List<CancellationTokenSource> isochronousOutGenerationCancellations = new();
    private bool isochronousSessionDoomed;
    private readonly ConcurrentDictionary<uint, PendingTransfer> pendingIsochronousIn = new();
    private readonly ConcurrentQueue<uint> pendingIsochronousInOrder = new();
    private readonly SemaphoreSlim pendingIsochronousInSignal = new(0);
    private readonly ConcurrentQueue<QuiescedIsochronousGeneration> quiescedIsochronousIn = new();
    private readonly SemaphoreSlim quiescedIsochronousInSignal = new(0);
    private readonly Dictionary<byte, IsochronousInInterfaceState> isochronousInInterfaces = new();
    private readonly List<CancellationTokenSource> isochronousInGenerationCancellations = new();
    private int pendingIsochronousInHighWater;

    public UsbIpDeviceSession(Stream stream, SerializedStreamWriter writer,
        DescriptorSet descriptors, FeatureReportSet featureReports,
        IInputReportSource inputReports, TimeSpan inputInterval,
        TimeSpan isochronousOutQuiesceTimeout,
        Action<HidOutputCapture> captureOutput,
        Action<IsochronousOutCapture> captureIsochronousOut,
        Action<string> log)
    {
        this.stream = stream;
        this.writer = writer;
        controlEndpoint = new ControlEndpoint(descriptors, featureReports);
        this.inputReports = inputReports;
        this.inputInterval = inputInterval;
        if (isochronousOutQuiesceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(isochronousOutQuiesceTimeout));
        }
        this.isochronousOutQuiesceTimeout = isochronousOutQuiesceTimeout;
        this.captureOutput = captureOutput;
        this.captureIsochronousOut = captureIsochronousOut;
        this.log = log;
        isochronousOutEndpoints = descriptors.Endpoints
            .Where(endpoint => (endpoint.Address & 0x80) == 0 &&
                (endpoint.Attributes & 0x03) == 0x01)
            .ToDictionary(endpoint => (uint)(endpoint.Address & 0x0F));
        isochronousInEndpoints = descriptors.Endpoints
            .Where(endpoint => (endpoint.Address & 0x80) != 0 &&
                (endpoint.Attributes & 0x03) == 0x01)
            .ToDictionary(endpoint => (uint)(endpoint.Address & 0x0F));
        audioRelay = inputReports as IUsbAudioRelay;

        HashSet<byte> playbackInterfaces = isochronousOutEndpoints.Values
            .Select(endpoint => endpoint.InterfaceNumber).ToHashSet();
        foreach (byte interfaceNumber in playbackInterfaces)
        {
            var generationCancellation = new CancellationTokenSource();
            isochronousOutGenerationCancellations.Add(generationCancellation);
            isochronousOutInterfaces.Add(interfaceNumber,
                new IsochronousOutInterfaceState(AlternateSetting: 0, Generation: 0,
                    generationCancellation));
        }
        HashSet<byte> captureInterfaces = isochronousInEndpoints.Values
            .Select(endpoint => endpoint.InterfaceNumber).ToHashSet();
        foreach (byte interfaceNumber in captureInterfaces)
        {
            var generationCancellation = new CancellationTokenSource();
            isochronousInGenerationCancellations.Add(generationCancellation);
            isochronousInInterfaces.Add(interfaceNumber,
                new IsochronousInInterfaceState(AlternateSetting: 0, Generation: 0,
                    generationCancellation));
        }
        controlEndpoint.InterfaceAltChanged += (interfaceNumber, alternateSetting) =>
        {
            log($"SET_INTERFACE interface {interfaceNumber} alt {alternateSetting}.");
            if (audioRelay == null)
            {
                return;
            }
            if (playbackInterfaces.Contains(interfaceNumber))
            {
                audioRelay.SetPlaybackInterfaceActive(alternateSetting != 0);
            }
            if (captureInterfaces.Contains(interfaceNumber))
            {
                audioRelay.SetCaptureInterfaceActive(alternateSetting != 0);
            }
        };
        controlEndpoint.AudioScaleChanged += (entityId, scale) =>
        {
            if (audioRelay == null)
            {
                return;
            }
            if (entityId == PlaybackFeatureUnit)
            {
                audioRelay.SetPlaybackVolume(scale);
            }
            else if (entityId == CaptureFeatureUnit)
            {
                audioRelay.SetCaptureVolume(scale);
            }
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task inputPump = PumpInterruptInputAsync(sessionCancellation.Token);
        Task isochronousPump = PumpIsochronousCompletionsAsync(sessionCancellation.Token);
        Task isochronousOutQuiescePump =
            PumpIsochronousOutQuiesceWatchdogsAsync(sessionCancellation.Token);
        Task isochronousInPump = PumpIsochronousInCompletionsAsync(sessionCancellation.Token);
        Task isochronousInQuiescePump =
            PumpIsochronousInQuiesceWatchdogsAsync(sessionCancellation.Token);
        Task commandPump = PumpCommandsAsync(sessionCancellation.Token);
        Task[] sessionTasks = new[]
        {
            commandPump,
            inputPump,
            isochronousPump,
            isochronousOutQuiescePump,
            isochronousInPump,
            isochronousInQuiescePump,
        };
        try
        {
            Task completed = await Task.WhenAny(sessionTasks);
            await completed;
            if (completed != commandPump && !sessionCancellation.IsCancellationRequested)
            {
                throw new IOException("A USB/IP session pump stopped unexpectedly.");
            }
        }
        finally
        {
            sessionCancellation.Cancel();
            try
            {
                await Task.WhenAll(sessionTasks);
            }
            catch (Exception) when (sessionCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                // The URB session is over: release the physical controller's
                // audio paths unconditionally — even when a pump faulted with
                // an I/O error — so the microphone and the continuous 0x36
                // stream never outlive a detach or a dropped connection.
                audioRelay?.SetPlaybackInterfaceActive(false);
                audioRelay?.SetCaptureInterfaceActive(false);
                if (pendingIsochronousInHighWater > 0)
                {
                    log($"ISO IN pending high-water mark: {pendingIsochronousInHighWater}.");
                }
                foreach (CancellationTokenSource generationCancellation in
                    isochronousOutGenerationCancellations)
                {
                    generationCancellation.Dispose();
                }
                foreach (CancellationTokenSource generationCancellation in
                    isochronousInGenerationCancellations)
                {
                    generationCancellation.Dispose();
                }
            }
        }
    }

    private async Task PumpCommandsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UsbIpCommand command = await UsbIpCodec.ReadCommandAsync(stream, cancellationToken);
            switch (command)
            {
                case UsbIpSubmit submit:
                    await HandleSubmitAsync(submit, cancellationToken);
                    break;
                case UsbIpUnlink unlink:
                    await HandleUnlinkAsync(unlink, cancellationToken);
                    break;
            }
        }
    }

    private async Task HandleSubmitAsync(UsbIpSubmit submit, CancellationToken cancellationToken)
    {
        if (submit.Basic.DeviceId != DeviceId)
        {
            log($"Rejecting seq {submit.Basic.SequenceNumber}: unexpected device id " +
                $"0x{submit.Basic.DeviceId:X8}.");
            await ReplySubmitAsync(submit.Basic.SequenceNumber, ControlResult.Stall,
                Array.Empty<byte>(), cancellationToken);
            return;
        }

        if (submit.NumberOfPackets > 0)
        {
            await HandleIsochronousAsync(submit, cancellationToken);
            return;
        }

        if (submit.Basic.Endpoint == 0)
        {
            await HandleControlAsync(submit, cancellationToken);
        }
        else if (submit.Basic.Endpoint == HidInterruptInEndpoint &&
                 submit.Basic.Direction == UsbIpConstants.DirectionIn)
        {
            if (!pendingInput.TryAdd(submit.Basic.SequenceNumber, new PendingTransfer(submit)))
            {
                throw new InvalidDataException(
                    $"Duplicate pending sequence {submit.Basic.SequenceNumber}.");
            }
            pendingInputOrder.Enqueue(submit.Basic.SequenceNumber);
        }
        else if (submit.Basic.Endpoint == HidInterruptOutEndpoint &&
                 submit.Basic.Direction == UsbIpConstants.DirectionOut)
        {
            captureOutput(new HidOutputCapture(DateTimeOffset.UtcNow,
                submit.Basic.SequenceNumber, "interrupt-out", submit.TransferBuffer.ToArray()));
            await ReplySubmitAsync(submit.Basic.SequenceNumber, status: 0,
                data: Array.Empty<byte>(), cancellationToken,
                actualLength: submit.TransferBuffer.Length);
        }
        else
        {
            log($"Stalling unsupported endpoint {submit.Basic.Endpoint} direction " +
                $"{submit.Basic.Direction} (seq {submit.Basic.SequenceNumber}).");
            await ReplySubmitAsync(submit.Basic.SequenceNumber, ControlResult.Stall,
                Array.Empty<byte>(), cancellationToken);
        }
    }

    private async Task HandleIsochronousAsync(UsbIpSubmit submit,
        CancellationToken cancellationToken)
    {
        bool isInput = submit.Basic.Direction == UsbIpConstants.DirectionIn;
        UsbEndpointDescriptorInfo? endpoint = null;
        bool knownEndpoint = isInput
            ? isochronousInEndpoints.TryGetValue(submit.Basic.Endpoint, out endpoint)
            : submit.Basic.Direction == UsbIpConstants.DirectionOut &&
              isochronousOutEndpoints.TryGetValue(submit.Basic.Endpoint, out endpoint);
        bool activeAlternateSetting = knownEndpoint && endpoint != null &&
            controlEndpoint.GetAltSetting(endpoint.InterfaceNumber) == endpoint.AlternateSetting;
        bool validPacketSizes = knownEndpoint && endpoint != null &&
            submit.IsoPackets.All(packet => packet.Length <= endpoint.MaxPacketSize);
        long describedLength = submit.IsoPackets.Sum(packet => (long)packet.Length);
        // IN URBs may legally describe gapped descriptor layouts, so only
        // bound the described total; the codec already validated each
        // packet's offset/length range against the transfer buffer.
        bool validPayload = isInput
            ? submit.TransferBuffer.Length == 0 &&
              describedLength <= submit.TransferBufferLength
            : describedLength == submit.TransferBuffer.Length &&
              submit.TransferBuffer.Length == submit.TransferBufferLength;

        if (!knownEndpoint || !activeAlternateSetting || !validPacketSizes || !validPayload)
        {
            log($"Rejecting ISO seq {submit.Basic.SequenceNumber}: endpoint={submit.Basic.Endpoint}, " +
                $"direction={submit.Basic.Direction}, packets={submit.NumberOfPackets}, " +
                $"bytes={submit.TransferBuffer.Length}, described={describedLength}, " +
                $"alt-active={activeAlternateSetting}.");
            await ReplyIsochronousAsync(submit, ControlResult.Stall, successful: false,
                cancellationToken);
            return;
        }

        if (isInput)
        {
            bool queuedIsochronousIn;
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            try
            {
                IsochronousInInterfaceState state =
                    isochronousInInterfaces[endpoint!.InterfaceNumber];
                queuedIsochronousIn = !isochronousSessionDoomed &&
                    state.AlternateSetting == endpoint.AlternateSetting &&
                    pendingIsochronousIn.Count < MaxPendingIsochronousInTransfers &&
                    pendingIsochronousIn.Count + pendingIsochronous.Count <
                        MaxPendingIsochronousTotal &&
                    pendingIsochronousIn.TryAdd(submit.Basic.SequenceNumber,
                        new PendingTransfer(submit,
                            isochronousInInterfaceNumber: endpoint.InterfaceNumber,
                            isochronousInGeneration: state.Generation,
                            isochronousInGenerationCancellation:
                                state.GenerationCancellation.Token));
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }

            if (!queuedIsochronousIn)
            {
                log($"Rejecting ISO IN seq {submit.Basic.SequenceNumber}: pending queue limit or duplicate.");
                await ReplyIsochronousAsync(submit, ControlResult.Stall, successful: false,
                    cancellationToken);
                return;
            }

            pendingIsochronousInHighWater = Math.Max(pendingIsochronousInHighWater,
                pendingIsochronousIn.Count);
            pendingIsochronousInOrder.Enqueue(submit.Basic.SequenceNumber);
            pendingIsochronousInSignal.Release();
            return;
        }

        bool queuedIsochronousOut;
        await isochronousLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            IsochronousOutInterfaceState state =
                isochronousOutInterfaces[endpoint!.InterfaceNumber];
            queuedIsochronousOut = !isochronousSessionDoomed &&
                state.AlternateSetting == endpoint.AlternateSetting &&
                pendingIsochronous.Count < MaxPendingIsochronousTransfers &&
                pendingIsochronous.Count + pendingIsochronousIn.Count <
                    MaxPendingIsochronousTotal &&
                pendingIsochronous.TryAdd(submit.Basic.SequenceNumber,
                    new PendingTransfer(submit, endpoint.InterfaceNumber, state.Generation,
                        state.GenerationCancellation.Token));
        }
        finally
        {
            isochronousLifecycleGate.Release();
        }

        if (!queuedIsochronousOut)
        {
            log($"Rejecting ISO seq {submit.Basic.SequenceNumber}: pending queue limit or duplicate.");
            await ReplyIsochronousAsync(submit, ControlResult.Stall, successful: false,
                cancellationToken);
            return;
        }

        captureIsochronousOut(new IsochronousOutCapture(
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            submit.Basic.SequenceNumber,
            submit.Basic.Endpoint,
            submit.StartFrame,
            submit.Interval,
            submit.TransferBuffer.ToArray(),
            submit.IsoPackets.ToArray()));
        pendingIsochronousOrder.Enqueue(submit.Basic.SequenceNumber);
        pendingIsochronousSignal.Release();
    }

    private async Task HandleControlAsync(UsbIpSubmit submit, CancellationToken cancellationToken)
    {
        UsbSetupPacket setup = UsbSetupPacket.Parse(submit.Setup);
        uint expectedDirection = setup.DeviceToHost
            ? UsbIpConstants.DirectionIn
            : UsbIpConstants.DirectionOut;
        if (submit.Basic.Direction != expectedDirection)
        {
            log($"Stalling EP0 seq {submit.Basic.SequenceNumber}: setup/USB-IP direction mismatch.");
            await ReplySubmitAsync(submit.Basic.SequenceNumber, ControlResult.Stall,
                Array.Empty<byte>(), cancellationToken);
            return;
        }

        if (setup.Type == UsbSetupPacket.TypeClass && setup.Request == UsbHidRequest.SetReport)
        {
            captureOutput(new HidOutputCapture(DateTimeOffset.UtcNow,
                submit.Basic.SequenceNumber, "control-set-report", submit.TransferBuffer.ToArray()));
        }

        ControlResult result;
        var quiescedOutGenerations = new List<QuiescedIsochronousGeneration>();
        var quiescedInGenerations = new List<QuiescedIsochronousGeneration>();
        bool isSetInterface =
            setup.Type == UsbSetupPacket.TypeStandard &&
            setup.Recipient == UsbSetupPacket.RecipientInterface &&
            setup.Request == UsbStandardRequest.SetInterface;
        bool isPlaybackSetInterface = isSetInterface &&
            isochronousOutInterfaces.ContainsKey((byte)setup.Index);
        bool isCaptureSetInterface = isSetInterface &&
            isochronousInInterfaces.ContainsKey((byte)setup.Index);
        bool isSetConfiguration =
            setup.Type == UsbSetupPacket.TypeStandard &&
            setup.Recipient == UsbSetupPacket.RecipientDevice &&
            setup.Request == UsbStandardRequest.SetConfiguration;
        if (isPlaybackSetInterface)
        {
            byte interfaceNumber = (byte)setup.Index;
            byte alternateSetting = (byte)setup.Value;
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            try
            {
                IsochronousOutInterfaceState previous =
                    isochronousOutInterfaces[interfaceNumber];
                bool alternateSettingChanging =
                    previous.AlternateSetting != alternateSetting;
                bool requestedAltActive =
                    IsIsochronousOutAltActive(interfaceNumber, alternateSetting);
                bool requestedAltKnown = alternateSetting == 0 || requestedAltActive;
                bool oldTransfersRemain = pendingIsochronous.Values.Any(pending =>
                    pending.IsochronousOutInterfaceNumber == interfaceNumber);
                bool unsafeActivation = alternateSettingChanging &&
                    requestedAltActive && oldTransfersRemain;
                result = !requestedAltKnown || unsafeActivation
                    ? ControlResult.Stalled()
                    : controlEndpoint.Handle(setup, submit.TransferBuffer);
                if (result.Status == 0 && previous.AlternateSetting != alternateSetting)
                {
                    bool previousAltActive = IsIsochronousOutAltActive(interfaceNumber,
                        previous.AlternateSetting);
                    long nextGeneration = checked(previous.Generation + 1);
                    previous.GenerationCancellation.Cancel();
                    var nextGenerationCancellation = new CancellationTokenSource();
                    isochronousOutGenerationCancellations.Add(nextGenerationCancellation);
                    isochronousOutInterfaces[interfaceNumber] =
                        new IsochronousOutInterfaceState(alternateSetting, nextGeneration,
                            nextGenerationCancellation);
                    if (previousAltActive)
                    {
                        quiescedOutGenerations.Add(new QuiescedIsochronousGeneration(
                            interfaceNumber, previous.Generation, DeadlineTimestamp: 0));
                    }
                }
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }

        }
        else if (isCaptureSetInterface)
        {
            byte interfaceNumber = (byte)setup.Index;
            byte alternateSetting = (byte)setup.Value;
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            try
            {
                IsochronousInInterfaceState previous =
                    isochronousInInterfaces[interfaceNumber];
                bool alternateSettingChanging =
                    previous.AlternateSetting != alternateSetting;
                bool requestedAltActive =
                    IsIsochronousInAltActive(interfaceNumber, alternateSetting);
                bool requestedAltKnown = alternateSetting == 0 || requestedAltActive;
                bool oldTransfersRemain = pendingIsochronousIn.Values.Any(pending =>
                    pending.IsochronousInInterfaceNumber == interfaceNumber);
                bool unsafeActivation = alternateSettingChanging &&
                    requestedAltActive && oldTransfersRemain;
                result = !requestedAltKnown || unsafeActivation
                    ? ControlResult.Stalled()
                    : controlEndpoint.Handle(setup, submit.TransferBuffer);
                if (result.Status == 0 && previous.AlternateSetting != alternateSetting)
                {
                    bool previousAltActive = IsIsochronousInAltActive(interfaceNumber,
                        previous.AlternateSetting);
                    long nextGeneration = checked(previous.Generation + 1);
                    previous.GenerationCancellation.Cancel();
                    var nextGenerationCancellation = new CancellationTokenSource();
                    isochronousInGenerationCancellations.Add(nextGenerationCancellation);
                    isochronousInInterfaces[interfaceNumber] =
                        new IsochronousInInterfaceState(alternateSetting, nextGeneration,
                            nextGenerationCancellation);
                    if (previousAltActive)
                    {
                        quiescedInGenerations.Add(new QuiescedIsochronousGeneration(
                            interfaceNumber, previous.Generation, DeadlineTimestamp: 0));
                    }
                }
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }
        }
        else if (isSetConfiguration)
        {
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            try
            {
                result = controlEndpoint.Handle(setup, submit.TransferBuffer);
                if (result.Status == 0)
                {
                    foreach (KeyValuePair<byte, IsochronousOutInterfaceState> entry in
                        isochronousOutInterfaces.ToArray())
                    {
                        byte interfaceNumber = entry.Key;
                        IsochronousOutInterfaceState previous = entry.Value;
                        if (!IsIsochronousOutAltActive(interfaceNumber,
                                previous.AlternateSetting))
                        {
                            continue;
                        }

                        previous.GenerationCancellation.Cancel();
                        long nextGeneration = checked(previous.Generation + 1);
                        var nextGenerationCancellation = new CancellationTokenSource();
                        isochronousOutGenerationCancellations.Add(
                            nextGenerationCancellation);
                        isochronousOutInterfaces[interfaceNumber] =
                            new IsochronousOutInterfaceState(AlternateSetting: 0,
                                nextGeneration, nextGenerationCancellation);
                        quiescedOutGenerations.Add(new QuiescedIsochronousGeneration(
                            interfaceNumber, previous.Generation, DeadlineTimestamp: 0));
                    }
                    foreach (KeyValuePair<byte, IsochronousInInterfaceState> entry in
                        isochronousInInterfaces.ToArray())
                    {
                        byte interfaceNumber = entry.Key;
                        IsochronousInInterfaceState previous = entry.Value;
                        if (!IsIsochronousInAltActive(interfaceNumber,
                                previous.AlternateSetting))
                        {
                            continue;
                        }

                        previous.GenerationCancellation.Cancel();
                        long nextGeneration = checked(previous.Generation + 1);
                        var nextGenerationCancellation = new CancellationTokenSource();
                        isochronousInGenerationCancellations.Add(
                            nextGenerationCancellation);
                        isochronousInInterfaces[interfaceNumber] =
                            new IsochronousInInterfaceState(AlternateSetting: 0,
                                nextGeneration, nextGenerationCancellation);
                        quiescedInGenerations.Add(new QuiescedIsochronousGeneration(
                            interfaceNumber, previous.Generation, DeadlineTimestamp: 0));
                    }
                }
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }
        }
        else
        {
            result = controlEndpoint.Handle(setup, submit.TransferBuffer);
        }
        int actualLength = result.Status == 0
            ? (submit.Basic.Direction == UsbIpConstants.DirectionIn
                ? result.Data.Length
                : submit.TransferBuffer.Length)
            : 0;

        if (result.Status != 0)
        {
            log($"EP0 stalled: bmRequestType 0x{setup.RequestType:X2}, request " +
                $"0x{setup.Request:X2}, value 0x{setup.Value:X4}, index 0x{setup.Index:X4}, " +
                $"length {setup.Length}.");
        }

        await ReplySubmitAsync(submit.Basic.SequenceNumber, result.Status,
            result.Data, cancellationToken, actualLength);

        if (quiescedOutGenerations.Count > 0)
        {
            long deadline = checked(Stopwatch.GetTimestamp() +
                ToStopwatchTicks(isochronousOutQuiesceTimeout));
            foreach (QuiescedIsochronousGeneration quiesced in quiescedOutGenerations)
            {
                QuiescedIsochronousGeneration armed = quiesced with
                {
                    DeadlineTimestamp = deadline,
                };
                int parked = pendingIsochronous.Values.Count(pending =>
                    pending.IsochronousOutInterfaceNumber == armed.InterfaceNumber &&
                    pending.IsochronousOutGeneration == armed.Generation);
                quiescedIsochronousOut.Enqueue(armed);
                quiescedIsochronousOutSignal.Release();
                log($"ISO OUT quiesced interface {armed.InterfaceNumber} generation " +
                    $"{armed.Generation}: parked {parked} pending transfer(s) for UNLINK; " +
                    $"watchdog {isochronousOutQuiesceTimeout.TotalMilliseconds:0} ms after ACK.");
            }
        }
        if (quiescedInGenerations.Count > 0)
        {
            long deadline = checked(Stopwatch.GetTimestamp() +
                ToStopwatchTicks(isochronousOutQuiesceTimeout));
            foreach (QuiescedIsochronousGeneration quiesced in quiescedInGenerations)
            {
                QuiescedIsochronousGeneration armed = quiesced with
                {
                    DeadlineTimestamp = deadline,
                };
                int parked = pendingIsochronousIn.Values.Count(pending =>
                    pending.IsochronousInInterfaceNumber == armed.InterfaceNumber &&
                    pending.IsochronousInGeneration == armed.Generation);
                quiescedIsochronousIn.Enqueue(armed);
                quiescedIsochronousInSignal.Release();
                log($"ISO IN quiesced interface {armed.InterfaceNumber} generation " +
                    $"{armed.Generation}: parked {parked} pending transfer(s) for UNLINK; " +
                    $"watchdog {isochronousOutQuiesceTimeout.TotalMilliseconds:0} ms after ACK.");
            }
        }
    }

    private async Task HandleUnlinkAsync(UsbIpUnlink unlink, CancellationToken cancellationToken)
    {
        int status = await UnlinkPendingAsync(pendingInput, unlink.UnlinkSequenceNumber,
            cancellationToken);
        if (status == 0)
        {
            status = await UnlinkIsochronousAsync(pendingIsochronous,
                unlink.UnlinkSequenceNumber, cancellationToken);
        }
        if (status == 0)
        {
            status = await UnlinkIsochronousAsync(pendingIsochronousIn,
                unlink.UnlinkSequenceNumber, cancellationToken);
        }
        await WriteUnlinkReplyUnlessDoomedAsync(unlink.Basic.SequenceNumber, status,
            cancellationToken);
    }

    private async Task<int> UnlinkIsochronousAsync(
        ConcurrentDictionary<uint, PendingTransfer> pendingTransfers,
        uint sequenceNumber, CancellationToken cancellationToken)
    {
        await isochronousLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (isochronousSessionDoomed ||
                !pendingTransfers.TryGetValue(sequenceNumber, out PendingTransfer? pending))
            {
                return 0;
            }

            if (pending.TryCancel())
            {
                pendingTransfers.TryRemove(sequenceNumber, out _);
                return ConnectionReset;
            }

            return 0;
        }
        finally
        {
            isochronousLifecycleGate.Release();
        }
    }

    private async Task WriteUnlinkReplyUnlessDoomedAsync(uint sequenceNumber, int status,
        CancellationToken cancellationToken)
    {
        await isochronousLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!isochronousSessionDoomed)
            {
                await writer.WriteAsync(
                    UsbIpCodec.Encode(new UsbIpUnlinkReply(sequenceNumber, status)),
                    cancellationToken);
            }
        }
        finally
        {
            isochronousLifecycleGate.Release();
        }
    }

    private static async Task<int> UnlinkPendingAsync(
        ConcurrentDictionary<uint, PendingTransfer> pendingTransfers,
        uint sequenceNumber,
        CancellationToken cancellationToken)
    {
        if (!pendingTransfers.TryGetValue(sequenceNumber, out PendingTransfer? pending))
        {
            return 0;
        }

        if (pending.TryCancel())
        {
            pendingTransfers.TryRemove(sequenceNumber, out _);
            return ConnectionReset;
        }

        // Completion won the race. USB/IP status 0 is only valid after the
        // corresponding RET_SUBMIT is on the wire; otherwise the client can
        // observe RET_UNLINK followed by a second completion for the same URB.
        await pending.Completion.WaitAsync(cancellationToken);
        return 0;
    }

    private async Task PumpInterruptInputAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(inputInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            PendingTransfer? pending = null;
            while (pendingInputOrder.TryDequeue(out uint sequence))
            {
                if (pendingInput.TryGetValue(sequence, out pending))
                {
                    break;
                }
            }

            if (pending == null || !pending.TryBeginCompletion())
            {
                continue;
            }

            try
            {
                UsbIpSubmit submit = pending.Submit;
                byte[] report = inputReports.CreateReport(submit.TransferBufferLength);
                await ReplySubmitAsync(submit.Basic.SequenceNumber, status: 0,
                    report, cancellationToken);
                pending.Complete();
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                throw;
            }
            finally
            {
                pendingInput.TryRemove(pending.Submit.Basic.SequenceNumber, out _);
            }
        }
    }

    private async Task PumpIsochronousCompletionsAsync(CancellationToken cancellationToken)
    {
        long nextCompletionTimestamp = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await pendingIsochronousSignal.WaitAsync(cancellationToken);

            PendingTransfer? pending = null;
            while (pendingIsochronousOrder.TryDequeue(out uint sequence))
            {
                if (pendingIsochronous.TryGetValue(sequence, out pending))
                {
                    break;
                }
            }
            if (pending == null)
            {
                continue;
            }

            UsbIpSubmit submit = pending.Submit;
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            bool generationActive;
            try
            {
                generationActive = IsIsochronousOutGenerationActive(pending);
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }
            if (!generationActive)
            {
                nextCompletionTimestamp = 0;
                continue;
            }

            UsbEndpointDescriptorInfo endpoint = isochronousOutEndpoints[submit.Basic.Endpoint];
            long durationTicks = IsochronousDurationTicks(endpoint.Interval,
                submit.NumberOfPackets);
            long now = Stopwatch.GetTimestamp();
            long completionTimestamp = checked(
                Math.Max(nextCompletionTimestamp, now) + durationTicks);

            using (var pacingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, pending.IsochronousOutGenerationCancellation))
            {
                try
                {
                    await DelayUntilAsync(completionTimestamp, pacingCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    pending.IsochronousOutGenerationCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    nextCompletionTimestamp = 0;
                    continue;
                }
            }
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            try
            {
                if (!IsIsochronousOutGenerationActive(pending))
                {
                    nextCompletionTimestamp = 0;
                    continue;
                }
                if (!pending.TryBeginCompletion())
                {
                    continue;
                }

                try
                {
                    await ReplyIsochronousAsync(submit, status: 0, successful: true,
                        cancellationToken);
                    pending.Complete();
                }
                catch (Exception ex)
                {
                    pending.Fail(ex);
                    throw;
                }
                finally
                {
                    pendingIsochronous.TryRemove(submit.Basic.SequenceNumber, out _);
                }
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }

            // Ideal timeline: chain from the SCHEDULED completion instant, not
            // the post-write clock. Ratcheting from `now` folded per-URB write
            // overhead into the stream clock — measured 98.8 URBs/s instead of
            // 100, i.e. the audio engine (whose pin clock follows these
            // completions) fed audio 1.2 % slower than the Bluetooth side
            // consumes, draining the relay buffer every few seconds. Resync
            // only after falling far behind (bounded catch-up, never a burst
            // loop — immediate ISO completion remains forbidden).
            now = Stopwatch.GetTimestamp();
            nextCompletionTimestamp = now - completionTimestamp > Stopwatch.Frequency / 20
                ? now
                : completionTimestamp;
        }
    }

    private async Task PumpIsochronousOutQuiesceWatchdogsAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await quiescedIsochronousOutSignal.WaitAsync(cancellationToken);
            while (quiescedIsochronousOut.TryDequeue(
                out QuiescedIsochronousGeneration quiesced))
            {
                await DelayUntilAsync(quiesced.DeadlineTimestamp, cancellationToken);
                await FailSessionIfQuiescedIsochronousOutRemainsAsync(quiesced,
                    cancellationToken);
            }
        }
    }

    private async Task FailSessionIfQuiescedIsochronousOutRemainsAsync(
        QuiescedIsochronousGeneration quiesced,
        CancellationToken cancellationToken)
    {
        await isochronousLifecycleGate.WaitAsync(cancellationToken);
        int stranded;
        try
        {
            stranded = pendingIsochronous.Values.Count(pending =>
                pending.IsochronousOutInterfaceNumber == quiesced.InterfaceNumber &&
                pending.IsochronousOutGeneration == quiesced.Generation);
            if (stranded > 0)
            {
                isochronousSessionDoomed = true;
            }
        }
        finally
        {
            isochronousLifecycleGate.Release();
        }

        if (stranded > 0)
        {
            log($"FatalUsbIpSession: ISO OUT quiesce watchdog found {stranded} unlinked transfer(s) " +
                $"from interface {quiesced.InterfaceNumber} generation " +
                $"{quiesced.Generation}; closing this USB/IP session without RET_SUBMIT.");
            throw new IOException("ISO OUT quiesce timed out waiting for UNLINK.");
        }
    }

    /// <summary>
    /// Completes isochronous IN transfers (the microphone endpoint) on one
    /// ordered, monotonically advancing packet timeline, exactly like the OUT
    /// pump: each URB completes after its packets' real duration, so queued
    /// URBs can never burst-complete. Packet data comes from the audio relay
    /// when it has microphone PCM and is silence otherwise — the capture
    /// cadence never waits on Bluetooth.
    /// </summary>
    private async Task PumpIsochronousInCompletionsAsync(CancellationToken cancellationToken)
    {
        long nextCompletionTimestamp = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await pendingIsochronousInSignal.WaitAsync(cancellationToken);

            PendingTransfer? pending = null;
            while (pendingIsochronousInOrder.TryDequeue(out uint sequence))
            {
                if (pendingIsochronousIn.TryGetValue(sequence, out pending))
                {
                    break;
                }
            }
            if (pending == null)
            {
                continue;
            }

            UsbIpSubmit submit = pending.Submit;
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            bool generationActive;
            try
            {
                generationActive = IsIsochronousInGenerationActive(pending);
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }
            if (!generationActive)
            {
                nextCompletionTimestamp = 0;
                continue;
            }

            UsbEndpointDescriptorInfo endpoint = isochronousInEndpoints[submit.Basic.Endpoint];
            long durationTicks = IsochronousDurationTicks(endpoint.Interval,
                submit.NumberOfPackets);
            long now = Stopwatch.GetTimestamp();
            long completionTimestamp = checked(
                Math.Max(nextCompletionTimestamp, now) + durationTicks);

            using (var pacingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, pending.IsochronousInGenerationCancellation))
            {
                try
                {
                    await DelayUntilAsync(completionTimestamp, pacingCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    pending.IsochronousInGenerationCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    nextCompletionTimestamp = 0;
                    continue;
                }
            }
            await isochronousLifecycleGate.WaitAsync(cancellationToken);
            try
            {
                if (!IsIsochronousInGenerationActive(pending))
                {
                    nextCompletionTimestamp = 0;
                    continue;
                }
                if (!pending.TryBeginCompletion())
                {
                    continue;
                }

                try
                {
                    await ReplyIsochronousInAsync(submit, cancellationToken);
                    pending.Complete();
                }
                catch (Exception ex)
                {
                    pending.Fail(ex);
                    throw;
                }
                finally
                {
                    pendingIsochronousIn.TryRemove(submit.Basic.SequenceNumber, out _);
                }
            }
            finally
            {
                isochronousLifecycleGate.Release();
            }

            // Ideal timeline with bounded catch-up — same reasoning as the OUT
            // pump: post-write ratcheting would run the capture clock slow.
            now = Stopwatch.GetTimestamp();
            nextCompletionTimestamp = now - completionTimestamp > Stopwatch.Frequency / 20
                ? now
                : completionTimestamp;
        }
    }

    private async Task PumpIsochronousInQuiesceWatchdogsAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await quiescedIsochronousInSignal.WaitAsync(cancellationToken);
            while (quiescedIsochronousIn.TryDequeue(
                out QuiescedIsochronousGeneration quiesced))
            {
                await DelayUntilAsync(quiesced.DeadlineTimestamp, cancellationToken);
                await FailSessionIfQuiescedIsochronousInRemainsAsync(quiesced,
                    cancellationToken);
            }

        }
    }

    private async Task FailSessionIfQuiescedIsochronousInRemainsAsync(
        QuiescedIsochronousGeneration quiesced,
        CancellationToken cancellationToken)
    {
        await isochronousLifecycleGate.WaitAsync(cancellationToken);
        int stranded;
        try
        {
            stranded = pendingIsochronousIn.Values.Count(pending =>
                pending.IsochronousInInterfaceNumber == quiesced.InterfaceNumber &&
                pending.IsochronousInGeneration == quiesced.Generation);
            if (stranded > 0)
            {
                isochronousSessionDoomed = true;
            }
        }
        finally
        {
            isochronousLifecycleGate.Release();
        }

        if (stranded > 0)
        {
            log($"FatalUsbIpSession: ISO IN quiesce watchdog found {stranded} unlinked transfer(s) " +
                $"from interface {quiesced.InterfaceNumber} generation " +
                $"{quiesced.Generation}; closing this USB/IP session without RET_SUBMIT.");
            throw new IOException("ISO IN quiesce timed out waiting for UNLINK.");
        }
    }

    /// <summary>
    /// Builds the successful RET_SUBMIT for an isochronous IN URB. Per the
    /// USB/IP protocol, IN data is sent compactly — each packet's actual bytes
    /// concatenated with no inter-packet padding — followed by the descriptors,
    /// whose offsets/lengths are echoed and only actual_length is set.
    /// </summary>
    private ValueTask ReplyIsochronousInAsync(UsbIpSubmit submit,
        CancellationToken cancellationToken)
    {
        int packetCount = submit.IsoPackets.Count;
        byte[] data = new byte[submit.TransferBufferLength];
        var packets = new UsbIpIsoPacket[packetCount];
        int filled = 0;
        for (int i = 0; i < packetCount; i++)
        {
            UsbIpIsoPacket packet = submit.IsoPackets[i];
            int capacity = (int)Math.Min(packet.Length, (uint)(data.Length - filled));
            int written = 0;
            if (capacity > 0)
            {
                int nominal = Math.Min(MicrophoneNominalPacketBytes, capacity);
                written = audioRelay?.FillMicrophonePacket(
                    data.AsSpan(filled, capacity), nominal) ?? 0;
                // Distrust relay results: clamp into the packet and keep whole
                // 4-byte stereo frames so descriptor accounting cannot drift.
                written = Math.Clamp(written, 0, capacity) & ~3;
                if (written <= 0)
                {
                    written = nominal; // buffer is already zeroed: silence
                }
            }
            packets[i] = packet with { ActualLength = (uint)written, Status = 0 };
            filled += written;
        }

        var reply = new UsbIpSubmitReply(
            submit.Basic.SequenceNumber,
            Status: 0,
            filled,
            submit.StartFrame,
            packetCount,
            ErrorCount: 0,
            data.AsSpan(0, filled).ToArray(),
            packets);
        return writer.WriteAsync(UsbIpCodec.Encode(reply), cancellationToken);
    }

    private static long IsochronousDurationTicks(byte endpointInterval, int packetCount)
    {
        // High-speed USB bInterval is 2^(bInterval-1) microframes; one
        // microframe is 125 us. The captured DualSense endpoint uses 4,
        // therefore each packet represents exactly 1 ms of audio.
        int interval = Math.Clamp(endpointInterval, (byte)1, (byte)16);
        double seconds = packetCount * 0.000125 * (1 << (interval - 1));
        return Math.Max(1, checked((long)Math.Round(seconds * Stopwatch.Frequency)));
    }

    private bool IsIsochronousOutAltActive(byte interfaceNumber, byte alternateSetting) =>
        isochronousOutEndpoints.Values.Any(endpoint =>
            endpoint.InterfaceNumber == interfaceNumber &&
            endpoint.AlternateSetting == alternateSetting);

    private bool IsIsochronousOutGenerationActive(PendingTransfer pending)
    {
        return !isochronousSessionDoomed &&
            pending.IsochronousOutInterfaceNumber is byte interfaceNumber &&
            isochronousOutInterfaces.TryGetValue(interfaceNumber,
                out IsochronousOutInterfaceState state) &&
            state.Generation == pending.IsochronousOutGeneration &&
            IsIsochronousOutAltActive(interfaceNumber, state.AlternateSetting);
    }

    private bool IsIsochronousInAltActive(byte interfaceNumber, byte alternateSetting) =>
        isochronousInEndpoints.Values.Any(endpoint =>
            endpoint.InterfaceNumber == interfaceNumber &&
            endpoint.AlternateSetting == alternateSetting);

    private bool IsIsochronousInGenerationActive(PendingTransfer pending)
    {
        return !isochronousSessionDoomed &&
            pending.IsochronousInInterfaceNumber is byte interfaceNumber &&
            isochronousInInterfaces.TryGetValue(interfaceNumber,
                out IsochronousInInterfaceState state) &&
            state.Generation == pending.IsochronousInGeneration &&
            IsIsochronousInAltActive(interfaceNumber, state.AlternateSetting);
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        Math.Max(1, checked((long)Math.Ceiling(
            duration.TotalSeconds * Stopwatch.Frequency)));

    private static async Task DelayUntilAsync(long targetTimestamp,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            long remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }
            TimeSpan remaining = TimeSpan.FromSeconds(
                remainingTicks / (double)Stopwatch.Frequency);
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private ValueTask ReplySubmitAsync(uint sequenceNumber, int status, byte[] data,
        CancellationToken cancellationToken, int? actualLength = null)
    {
        var reply = new UsbIpSubmitReply(sequenceNumber, status,
            actualLength ?? data.Length,
            StartFrame: -1,
            NumberOfPackets: 0,
            ErrorCount: 0,
            data,
            Array.Empty<UsbIpIsoPacket>());
        return writer.WriteAsync(UsbIpCodec.Encode(reply), cancellationToken);
    }

    private ValueTask ReplyIsochronousAsync(UsbIpSubmit submit, int status, bool successful,
        CancellationToken cancellationToken)
    {
        UsbIpIsoPacket[] completedPackets = submit.IsoPackets
            .Select(packet => packet with
            {
                ActualLength = successful ? packet.Length : 0,
                Status = successful ? 0 : status,
            })
            .ToArray();
        int actualLength = successful
            ? checked((int)completedPackets.Sum(packet => (long)packet.ActualLength))
            : 0;
        var reply = new UsbIpSubmitReply(
            submit.Basic.SequenceNumber,
            status,
            actualLength,
            submit.StartFrame,
            submit.NumberOfPackets,
            successful ? 0 : submit.NumberOfPackets,
            Array.Empty<byte>(),
            completedPackets);
        return writer.WriteAsync(UsbIpCodec.Encode(reply), cancellationToken);
    }

    private sealed class PendingTransfer
    {
        private const int Pending = 0;
        private const int Completing = 1;
        private const int Canceled = 2;
        private const int Completed = 3;

        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int state = Pending;

        public PendingTransfer(UsbIpSubmit submit,
            byte? isochronousOutInterfaceNumber = null,
            long isochronousOutGeneration = -1,
            CancellationToken isochronousOutGenerationCancellation = default,
            byte? isochronousInInterfaceNumber = null,
            long isochronousInGeneration = -1,
            CancellationToken isochronousInGenerationCancellation = default)
        {
            Submit = submit;
            IsochronousOutInterfaceNumber = isochronousOutInterfaceNumber;
            IsochronousOutGeneration = isochronousOutGeneration;
            IsochronousOutGenerationCancellation =
                isochronousOutGenerationCancellation;
            IsochronousInInterfaceNumber = isochronousInInterfaceNumber;
            IsochronousInGeneration = isochronousInGeneration;
            IsochronousInGenerationCancellation =
                isochronousInGenerationCancellation;
        }

        public UsbIpSubmit Submit { get; }
        public byte? IsochronousOutInterfaceNumber { get; }
        public long IsochronousOutGeneration { get; }
        public CancellationToken IsochronousOutGenerationCancellation { get; }
        public byte? IsochronousInInterfaceNumber { get; }
        public long IsochronousInGeneration { get; }
        public CancellationToken IsochronousInGenerationCancellation { get; }
        public Task Completion => completion.Task;

        public bool TryCancel()
        {
            if (Interlocked.CompareExchange(ref state, Canceled, Pending) != Pending)
            {
                return false;
            }

            completion.TrySetResult();
            return true;
        }

        public bool TryBeginCompletion() =>
            Interlocked.CompareExchange(ref state, Completing, Pending) == Pending;

        public void Complete()
        {
            Volatile.Write(ref state, Completed);
            completion.TrySetResult();
        }

        public void Fail(Exception exception)
        {
            Volatile.Write(ref state, Completed);
            completion.TrySetException(exception);
        }
    }

    private readonly record struct IsochronousOutInterfaceState(
        byte AlternateSetting,
        long Generation,
        CancellationTokenSource GenerationCancellation);

    private readonly record struct IsochronousInInterfaceState(
        byte AlternateSetting,
        long Generation,
        CancellationTokenSource GenerationCancellation);

    private readonly record struct QuiescedIsochronousGeneration(
        byte InterfaceNumber,
        long Generation,
        long DeadlineTimestamp);
}
