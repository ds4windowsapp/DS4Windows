// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VirtualDualSenseUsbip.Live;

internal sealed record NativeModeRenderEndpoint(
    string Id,
    string FriendlyName,
    string? DeviceInstanceId,
    Guid? ContainerId);

internal interface INativeModeRenderEndpointSource
{
    IReadOnlyList<NativeModeRenderEndpoint> GetActiveRenderEndpoints();
}

internal interface INativeModeVirtualDeviceIdentity
{
    bool IsPresent();
    bool OwnsEndpoint(NativeModeRenderEndpoint endpoint);
}

internal sealed class NativeModeRenderLeaseTerminatedEventArgs : EventArgs
{
    public NativeModeRenderLeaseTerminatedEventArgs(Exception exception) =>
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));

    public Exception Exception { get; }
}

internal interface INativeModeRenderLease : IDisposable
{
    event EventHandler<NativeModeRenderLeaseTerminatedEventArgs> UnexpectedTermination;
    void Start();
}

internal interface INativeModeRenderLeaseFactory
{
    INativeModeRenderLease Create(string endpointId);
}

/// <summary>
/// Owns a redundant silent render client inside the USB/IP helper. Once a
/// render lease may have opened the virtual pin, its only release condition is
/// confirmed absence of both the tracked endpoint and the fixed virtual parent.
/// </summary>
internal sealed class NativeModeChildRenderKeepalive
{
    internal const string ReadyMarker = "NativeRenderKeepaliveReady:";
    internal const string FailedMarker = "NativeRenderKeepaliveFailed:";
    internal const string RetainedMarker = "NativeRenderKeepaliveRetained:";
    internal const string ReleasedMarker = "NativeRenderKeepaliveReleased:";

    private static readonly TimeSpan DefaultReadinessPollInterval =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultReadinessRetryMaximumInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultRemovalPollInterval =
        TimeSpan.FromSeconds(1);

    private readonly object stateGate = new();
    private readonly INativeModeRenderEndpointSource endpointSource;
    private readonly INativeModeVirtualDeviceIdentity deviceIdentity;
    private readonly INativeModeRenderLeaseFactory leaseFactory;
    private readonly Action<string> log;
    private readonly TimeSpan readinessPollInterval;
    private readonly TimeSpan removalPollInterval;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource readinessCancellation = new();
    private readonly TaskCompletionSource<bool> readyCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> failureCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> releaseCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HashSet<string>? activeBeforeAttach;
    private Task? readinessMonitor;
    private INativeModeRenderLease? lease;
    private string? endpointId;
    private Exception? pendingTermination;
    private bool begun;
    private bool ready;
    private bool healthy;
    private bool teardownStarted;
    private bool releaseCompleted;
    private bool failurePublished;
    private bool retentionLogged;
    private bool probeFailureLogged;
    private bool readinessProbeFailureLogged;

    public NativeModeChildRenderKeepalive(Action<string> log) : this(
        new WindowsNativeModeRenderEndpointSource(),
        new WindowsNativeModeVirtualDeviceIdentity(),
        new WasapiNativeModeRenderLeaseFactory(),
        log,
        DefaultReadinessPollInterval,
        DefaultRemovalPollInterval)
    {
    }

    internal NativeModeChildRenderKeepalive(
        INativeModeRenderEndpointSource endpointSource,
        INativeModeVirtualDeviceIdentity deviceIdentity,
        INativeModeRenderLeaseFactory leaseFactory,
        Action<string> log,
        TimeSpan? readinessPollInterval = null,
        TimeSpan? removalPollInterval = null)
    {
        this.endpointSource = endpointSource ??
            throw new ArgumentNullException(nameof(endpointSource));
        this.deviceIdentity = deviceIdentity ??
            throw new ArgumentNullException(nameof(deviceIdentity));
        this.leaseFactory = leaseFactory ??
            throw new ArgumentNullException(nameof(leaseFactory));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.readinessPollInterval = readinessPollInterval ??
            DefaultReadinessPollInterval;
        this.removalPollInterval = removalPollInterval ??
            DefaultRemovalPollInterval;
        if (this.readinessPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(readinessPollInterval));
        if (this.removalPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(removalPollInterval));
    }

    internal bool HasLease
    {
        get
        {
            lock (stateGate)
                return lease != null;
        }
    }

    /// <summary>
    /// Captures the render baseline and starts discovery before the listener is
    /// advertised, so an existing physical DualSense can never be selected.
    /// </summary>
    public void BeginSession()
    {
        lock (stateGate)
        {
            if (begun)
                throw new InvalidOperationException(
                    "The child render keepalive has already begun.");
            begun = true;
        }

        try
        {
            activeBeforeAttach = endpointSource.GetActiveRenderEndpoints()
                .Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            readinessMonitor = Task.Run(MonitorReadinessAsync);
        }
        catch (Exception ex)
        {
            PublishFailure(new InvalidOperationException(
                "Could not capture the render endpoint baseline.", ex));
            throw;
        }
    }

    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
        readyCompletion.Task.WaitAsync(cancellationToken);

    public Task<Exception> WaitForFailureAsync() => failureCompletion.Task;

    public Task WaitForReleaseAsync(CancellationToken cancellationToken = default) =>
        releaseCompletion.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Arms expected teardown. Discovery deliberately continues until a lease
    /// exists or the exact parent is absent, closing the post-attach/pre-ready
    /// lifetime gap.
    /// </summary>
    public void BeginTeardown()
    {
        lock (stateGate)
        {
            if (teardownStarted)
                return;
            teardownStarted = true;
        }

    }

    /// <summary>
    /// Before the active USB/IP session is stopped, proves that either this
    /// process owns a healthy render lease or no exact virtual parent exists. If a
    /// discovery probe fails while the parent is present, this fail-closed
    /// barrier intentionally does not complete.
    /// </summary>
    public async Task WaitForShutdownSafetyAsync(
        CancellationToken cancellationToken = default)
    {
        await WaitForShutdownSafetyAsync(
            static () => false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The predicate is true while an accepted import reply can still
    /// materialize a virtual parent. Callers must first atomically stop new
    /// imports, so the predicate cannot change from false back to true.
    /// </summary>
    public async Task WaitForShutdownSafetyAsync(
        Func<bool> requiresHealthyRenderLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiresHealthyRenderLease);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateGate)
            {
                if (healthy || releaseCompleted)
                    return;
            }

            bool parentPresent;
            bool healthyLeaseRequired;
            try
            {
                healthyLeaseRequired = requiresHealthyRenderLease();
                parentPresent = deviceIdentity.IsPresent();
                lock (stateGate)
                    probeFailureLogged = false;
            }
            catch (Exception ex)
            {
                bool shouldLog;
                lock (stateGate)
                {
                    shouldLog = !probeFailureLogged;
                    probeFailureLogged = true;
                }
                if (shouldLog)
                {
                    log($"{RetainedMarker} shutdown safety could not be " +
                        $"confirmed; discovery remains active: {ex.Message}");
                }
                healthyLeaseRequired = true;
                parentPresent = true;
            }

            if (!parentPresent && !healthyLeaseRequired)
                return;

            bool shouldLogRetention;
            lock (stateGate)
            {
                shouldLogRetention = !retentionLogged;
                retentionLogged = true;
            }
            if (shouldLogRetention)
            {
                log($"{RetainedMarker} waiting for a healthy running child " +
                    "render pin before the " +
                    "active USB/IP session can be stopped.");
            }

            await Task.Delay(readinessPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits without a production timeout. Probe failure is not absence; the
    /// helper remains alive and retains the pin until both removal signals can
    /// be positively confirmed.
    /// </summary>
    public async Task RetainUntilRemovedAndReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        BeginTeardown();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            INativeModeRenderLease? leaseToDispose = null;
            bool releaseWithoutLease = false;
            try
            {
                string? trackedEndpoint;
                INativeModeRenderLease? retainedLease;
                lock (stateGate)
                {
                    if (releaseCompleted)
                        return;
                    retainedLease = lease;
                    trackedEndpoint = endpointId;
                }

                if (retainedLease == null)
                {
                    bool virtualParentPresent;
                    try
                    {
                        virtualParentPresent = deviceIdentity.IsPresent();
                        lock (stateGate)
                            probeFailureLogged = false;
                    }
                    catch (Exception ex)
                    {
                        bool shouldLog;
                        lock (stateGate)
                        {
                            shouldLog = !probeFailureLogged;
                            probeFailureLogged = true;
                        }
                        if (shouldLog)
                        {
                            log($"{RetainedMarker} parent removal could not be " +
                                $"confirmed; discovery remains active: {ex.Message}");
                        }
                        virtualParentPresent = true;
                    }

                    if (virtualParentPresent)
                    {
                        bool shouldLog;
                        lock (stateGate)
                        {
                            shouldLog = !retentionLogged;
                            retentionLogged = true;
                        }
                        if (shouldLog)
                        {
                            log($"{RetainedMarker} exact parent is still present; " +
                                "waiting for a child render pin or removal.");
                        }
                    }
                    else
                    {
                        readinessCancellation.Cancel();
                        lock (stateGate)
                        {
                            releaseCompleted = true;
                            releaseWithoutLease = true;
                        }
                    }
                }
                else
                {
                    readinessCancellation.Cancel();
                    bool endpointPresent;
                    bool virtualParentPresent;
                    bool probeSucceeded = false;
                    try
                    {
                        endpointPresent = endpointSource.GetActiveRenderEndpoints()
                            .Any(endpoint => string.Equals(endpoint.Id,
                                trackedEndpoint,
                                StringComparison.OrdinalIgnoreCase));
                        virtualParentPresent = deviceIdentity.IsPresent();
                        probeSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        bool shouldLog;
                        lock (stateGate)
                        {
                            shouldLog = !probeFailureLogged;
                            probeFailureLogged = true;
                        }
                        if (shouldLog)
                        {
                            log($"{RetainedMarker} removal could not be confirmed; " +
                                $"the helper render pin remains open: {ex.Message}");
                        }
                        endpointPresent = true;
                        virtualParentPresent = true;
                    }

                    if (probeSucceeded)
                    {
                        lock (stateGate)
                            probeFailureLogged = false;
                    }

                    if (endpointPresent || virtualParentPresent)
                    {
                        bool shouldLog;
                        lock (stateGate)
                        {
                            shouldLog = !retentionLogged;
                            retentionLogged = true;
                        }
                        if (shouldLog)
                        {
                            log($"{RetainedMarker} waiting for the exact endpoint " +
                                "and fixed virtual parent to disappear.");
                        }
                    }
                    else
                    {
                        lock (stateGate)
                        {
                            leaseToDispose = lease;
                            lease = null;
                            releaseCompleted = true;
                        }
                    }
                }
            }
            finally
            {
                operationGate.Release();
            }

            if (releaseWithoutLease)
            {
                await AwaitReadinessMonitorAfterCancellationAsync()
                    .ConfigureAwait(false);
                log($"{ReleasedMarker} exact virtual parent is absent; no child " +
                    "render pin was opened.");
                releaseCompletion.TrySetResult(true);
                failureCompletion.TrySetCanceled();
                return;
            }

            if (leaseToDispose != null)
            {
                leaseToDispose.UnexpectedTermination -= OnUnexpectedTermination;
                try
                {
                    leaseToDispose.Dispose();
                }
                catch (Exception ex)
                {
                    log($"{ReleasedMarker} endpoint removal was confirmed, but " +
                        $"disposing the inert render client reported: {ex.Message}");
                }
                log($"{ReleasedMarker} exact virtual endpoint and parent are absent.");
                releaseCompletion.TrySetResult(true);
                failureCompletion.TrySetCanceled();
                return;
            }

            await Task.Delay(removalPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task AwaitReadinessMonitorAfterCancellationAsync()
    {
        Task? monitor;
        lock (stateGate)
            monitor = readinessMonitor;
        if (monitor == null)
            return;

        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when confirmed parent absence ends discovery.
        }
    }

    private async Task MonitorReadinessAsync()
    {
        CancellationToken token = readinessCancellation.Token;
        TimeSpan retryDelay = readinessPollInterval;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            NativeModeRenderEndpoint? candidate;
            try
            {
                HashSet<string> baseline = activeBeforeAttach ??
                    throw new InvalidOperationException(
                        "The render endpoint baseline is unavailable.");
                candidate = endpointSource.GetActiveRenderEndpoints()
                    .FirstOrDefault(endpoint =>
                        !baseline.Contains(endpoint.Id) &&
                        deviceIdentity.OwnsEndpoint(endpoint));

                bool recovered;
                lock (stateGate)
                {
                    recovered = readinessProbeFailureLogged;
                    readinessProbeFailureLogged = false;
                }
                if (recovered)
                {
                    log($"{RetainedMarker} render endpoint discovery recovered; " +
                        "continuing helper keepalive startup.");
                }
                retryDelay = readinessPollInterval;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                bool shouldLog;
                lock (stateGate)
                {
                    shouldLog = !readinessProbeFailureLogged;
                    readinessProbeFailureLogged = true;
                }
                if (shouldLog)
                {
                    // Do not include the exception: endpoint enumeration errors
                    // can contain endpoint ids or device-instance paths. A
                    // transient probe failure is not absence and must not end
                    // discovery while an import may still be materializing.
                    log($"{RetainedMarker} render endpoint discovery is " +
                        "temporarily unavailable; retrying fail-closed.");
                }

                await Task.Delay(retryDelay, token).ConfigureAwait(false);
                retryDelay = NextReadinessRetryDelay(retryDelay);
                continue;
            }

            if (candidate != null)
            {
                await StartLeaseAsync(candidate, token).ConfigureAwait(false);
                return;
            }

            await Task.Delay(readinessPollInterval, token).ConfigureAwait(false);
        }
    }

    private static TimeSpan NextReadinessRetryDelay(TimeSpan current)
    {
        double milliseconds = Math.Min(
            current.TotalMilliseconds * 2,
            DefaultReadinessRetryMaximumInterval.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private async Task StartLeaseAsync(
        NativeModeRenderEndpoint candidate,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            INativeModeRenderLease created = leaseFactory.Create(candidate.Id);
            created.UnexpectedTermination += OnUnexpectedTermination;
            lock (stateGate)
            {
                // Publish before Start: Init/Play may activate the kernel pin
                // and then throw, in which case the lease must still be retained.
                endpointId = candidate.Id;
                lease = created;
            }

            try
            {
                created.Start();
                Exception? startupTermination;
                lock (stateGate)
                {
                    ready = true;
                    startupTermination = pendingTermination;
                    healthy = startupTermination == null;
                }

                if (startupTermination != null)
                {
                    PublishFailure(startupTermination);
                    return;
                }

                log($"{ReadyMarker} active.");
                readyCompletion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                PublishFailure(new InvalidOperationException(
                    "Could not start the helper render pin.", ex));
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void OnUnexpectedTermination(
        object? sender,
        NativeModeRenderLeaseTerminatedEventArgs args)
    {
        Exception failure = args.Exception;
        bool publish;
        lock (stateGate)
        {
            if (releaseCompleted || !ReferenceEquals(sender, lease))
            {
                return;
            }

            healthy = false;
            if (!ready)
            {
                pendingTermination ??= failure;
                publish = false;
            }
            else
            {
                publish = !teardownStarted;
            }
        }

        if (publish)
            PublishFailure(failure);
    }

    private void PublishFailure(Exception failure)
    {
        bool shouldLog;
        lock (stateGate)
        {
            shouldLog = !failurePublished;
            failurePublished = true;
        }
        if (!shouldLog)
            return;

        // Keep the stable parent-facing marker free of endpoint ids, HID
        // paths, and controller addresses. The exception remains available to
        // the in-process coordinator and tests.
        log($"{FailedMarker} helper render pin could not be maintained.");
        readyCompletion.TrySetException(failure);
        failureCompletion.TrySetResult(failure);
    }

}

internal sealed class WasapiNativeModeRenderLeaseFactory :
    INativeModeRenderLeaseFactory
{
    public INativeModeRenderLease Create(string endpointId) =>
        new WasapiNativeModeRenderLease(endpointId);

    private sealed class WasapiNativeModeRenderLease : INativeModeRenderLease
    {
        private readonly string endpointId;
        private MMDevice? endpoint;
        private WasapiOut? output;
        private int started;
        private int disposed;

        public WasapiNativeModeRenderLease(string endpointId)
        {
            this.endpointId = !string.IsNullOrWhiteSpace(endpointId)
                ? endpointId
                : throw new ArgumentException(
                    "A render endpoint id is required.", nameof(endpointId));
        }

        public event EventHandler<NativeModeRenderLeaseTerminatedEventArgs>?
            UnexpectedTermination;

        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                throw new InvalidOperationException(
                    "The render lease has already started.");

            using var enumerator = new MMDeviceEnumerator();
            endpoint = enumerator.GetDevice(endpointId);
            using AudioClient audioClient = endpoint.AudioClient;
            WaveFormat mixFormat = audioClient.MixFormat;

            output = new WasapiOut(endpoint, AudioClientShareMode.Shared,
                useEventSync: true, latency: 20);
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(new SilenceProvider(mixFormat));
            output.Play();

            // PlaybackState changes before the worker calls IAudioClient.Start.
            // Clock movement proves the render pin is actually running.
            DateTimeOffset deadline = DateTimeOffset.UtcNow +
                TimeSpan.FromSeconds(3);
            Exception? lastStartFailure = null;
            while (DateTimeOffset.UtcNow < deadline &&
                output.PlaybackState == PlaybackState.Playing)
            {
                try
                {
                    if (output.GetPosition() > 0)
                        return;
                }
                catch (Exception ex)
                {
                    lastStartFailure = ex;
                }
                Thread.Sleep(10);
            }

            if (lastStartFailure != null)
                throw lastStartFailure;
            if (output.PlaybackState == PlaybackState.Playing)
            {
                throw new TimeoutException(
                    "WASAPI render clock did not advance.");
            }
            throw new InvalidOperationException(
                "WASAPI did not enter the playing state.");
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            Exception failure = args.Exception ?? new InvalidOperationException(
                "The mandatory helper render stream stopped.");
            UnexpectedTermination?.Invoke(this,
                new NativeModeRenderLeaseTerminatedEventArgs(failure));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            // Never call Stop. The owner reaches Dispose only after endpoint
            // and parent removal, when an alt-setting transition is impossible.
            if (output != null)
                output.PlaybackStopped -= OnPlaybackStopped;
            output?.Dispose();
            endpoint?.Dispose();
        }
    }
}

internal sealed class WindowsNativeModeRenderEndpointSource :
    INativeModeRenderEndpointSource
{
    private static readonly PropertyKey DeviceContainerIdProperty =
        new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    public IReadOnlyList<NativeModeRenderEndpoint> GetActiveRenderEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render, DeviceState.Active);
        var result = new List<NativeModeRenderEndpoint>(devices.Count);
        foreach (MMDevice device in devices)
        {
            using (device)
            {
                result.Add(new NativeModeRenderEndpoint(
                    device.ID,
                    device.FriendlyName ?? string.Empty,
                    TryGetStringProperty(device,
                        PropertyKeys.PKEY_Device_InstanceId),
                    TryGetGuidProperty(device, DeviceContainerIdProperty)));
            }
        }
        return result;
    }

    private static string? TryGetStringProperty(
        MMDevice device,
        PropertyKey propertyKey)
    {
        try
        {
            return device.Properties.Contains(propertyKey)
                ? device.Properties[propertyKey]?.Value as string
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static Guid? TryGetGuidProperty(
        MMDevice device,
        PropertyKey propertyKey)
    {
        try
        {
            if (!device.Properties.Contains(propertyKey))
                return null;

            object? value = device.Properties[propertyKey]?.Value;
            if (value is Guid guid)
                return guid;
            return value is string text && Guid.TryParse(text, out guid)
                ? guid
                : null;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class WindowsNativeModeVirtualDeviceIdentity :
    INativeModeVirtualDeviceIdentity
{
    internal const string VirtualDualSenseSerial = "DS4WSPKCOMP001";
    internal const string VirtualDualSenseParentInstanceId =
        @"USB\VID_054C&PID_0CE6\DS4WSPKCOMP001";

    private const string DualSenseUsbInstancePrefix =
        @"USB\VID_054C&PID_0CE6";
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly DevPropKey DeviceParentProperty =
        new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 8);
    private static readonly DevPropKey DeviceContainerIdProperty =
        new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    public bool IsPresent() => GetExactPresentParents().Count != 0;

    public bool OwnsEndpoint(NativeModeRenderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        IReadOnlyList<string> exactParents = GetExactPresentParents();
        if (exactParents.Count == 0)
            return false;

        string? current = endpoint.DeviceInstanceId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int depth = 0;
            depth < 16 && !string.IsNullOrWhiteSpace(current) &&
            visited.Add(current);
            depth++)
        {
            if (IsVirtualDualSenseInstanceOrDescendant(current))
                return true;
            current = TryGetStringProperty(current, DeviceParentProperty);
        }

        if (!endpoint.ContainerId.HasValue)
            return false;

        return exactParents.Any(parent =>
            TryGetContainerId(parent) is Guid parentContainer &&
            parentContainer == endpoint.ContainerId.Value);
    }

    private static IReadOnlyList<string> GetExactPresentParents() =>
        EnumeratePresentDeviceInstanceIds()
            .Where(instanceId => string.Equals(
                instanceId,
                VirtualDualSenseParentInstanceId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool IsVirtualDualSenseInstanceOrDescendant(
        string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) ||
            !instanceId.StartsWith(
                DualSenseUsbInstancePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int separator = instanceId.LastIndexOf((char)92);
        if (separator < 0 || separator == instanceId.Length - 1)
            return false;

        string suffix = instanceId[(separator + 1)..];
        return string.Equals(
                suffix,
                VirtualDualSenseSerial,
                StringComparison.OrdinalIgnoreCase) ||
            suffix.StartsWith(
                VirtualDualSenseSerial + "&",
                StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EnumeratePresentDeviceInstanceIds()
    {
        IntPtr deviceInfoSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetupDiGetClassDevs could not enumerate present devices.");
        }

        var result = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = CreateDeviceInfoData();
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        break;
                    throw new Win32Exception(
                        error,
                        "SetupDiEnumDeviceInfo could not enumerate a device.");
                }

                result.Add(GetDeviceInstanceId(deviceInfoSet, ref deviceInfo));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
        return result;
    }

    private static string GetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfo)
    {
        uint requiredSize = 0;
        if (!SetupDiGetDeviceInstanceId(
            deviceInfoSet,
            ref deviceInfo,
            null,
            0,
            ref requiredSize))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    error,
                    "SetupDiGetDeviceInstanceId could not size an instance id.");
            }
        }

        if (requiredSize <= 1)
            throw new InvalidOperationException(
                "SetupDiGetDeviceInstanceId returned an empty instance id.");

        var buffer = new StringBuilder(checked((int)requiredSize));
        if (!SetupDiGetDeviceInstanceId(
            deviceInfoSet,
            ref deviceInfo,
            buffer,
            requiredSize,
            ref requiredSize))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetupDiGetDeviceInstanceId could not read an instance id.");
        }
        return buffer.ToString();
    }

    private static string? TryGetStringProperty(
        string deviceInstanceId,
        DevPropKey key)
    {
        byte[]? data = TryGetDeviceProperty(deviceInstanceId, key);
        return data == null
            ? null
            : Encoding.Unicode.GetString(data)
                .TrimEnd('\0');
    }

    private static Guid? TryGetContainerId(string deviceInstanceId)
    {
        byte[]? data = TryGetDeviceProperty(
            deviceInstanceId,
            DeviceContainerIdProperty);
        return data is { Length: 16 } ? new Guid(data) : null;
    }

    private static byte[]? TryGetDeviceProperty(
        string deviceInstanceId,
        DevPropKey key)
    {
        IntPtr deviceInfoSet = SetupDiCreateDeviceInfoList(
            IntPtr.Zero,
            IntPtr.Zero);
        if (deviceInfoSet == InvalidHandleValue)
            return null;

        try
        {
            var deviceInfo = CreateDeviceInfoData();
            if (!SetupDiOpenDeviceInfo(
                deviceInfoSet,
                deviceInstanceId,
                IntPtr.Zero,
                0,
                ref deviceInfo))
            {
                return null;
            }

            uint propertyType = 0;
            uint requiredSize = 0;
            if (!SetupDiGetDeviceProperty(
                deviceInfoSet,
                ref deviceInfo,
                ref key,
                ref propertyType,
                null,
                0,
                ref requiredSize,
                0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorInsufficientBuffer || requiredSize == 0)
                    return null;
            }

            var buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceProperty(
                deviceInfoSet,
                ref deviceInfo,
                ref key,
                ref propertyType,
                buffer,
                checked((uint)buffer.Length),
                ref requiredSize,
                0))
            {
                return null;
            }

            if (requiredSize != buffer.Length)
                Array.Resize(ref buffer, checked((int)requiredSize));
            return buffer;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static SpDevInfoData CreateDeviceInfoData() =>
        new()
        {
            Size = checked((uint)Marshal.SizeOf<SpDevInfoData>()),
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public DevPropKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfo,
        StringBuilder? deviceInstanceId,
        uint deviceInstanceIdSize,
        ref uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(
        IntPtr classGuid,
        IntPtr parentWindow);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        EntryPoint = "SetupDiOpenDeviceInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfo(
        IntPtr deviceInfoSet,
        string deviceInstanceId,
        IntPtr parentWindow,
        uint openFlags,
        ref SpDevInfoData deviceInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfo,
        ref DevPropKey propertyKey,
        ref uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        ref uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(
        IntPtr deviceInfoSet);
}
