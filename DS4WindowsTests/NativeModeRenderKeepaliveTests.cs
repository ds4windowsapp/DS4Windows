using System.Collections.Concurrent;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeRenderKeepaliveTests
{
    [TestMethod]
    public async Task WaitForReady_SelectsOnlyNewDualSenseRenderEndpoint()
    {
        var accessor = new FakeEndpointAccessor();
        accessor.Add("existing-speakers", "SteelSeries Sonar - Gaming");
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);

        keepalive.BeginSession();
        accessor.Add("other-new", "HDMI Output");
        accessor.Add("virtual-render",
            "Speakers (DualSense Wireless Controller)");
        notifications.Notify();

        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { "virtual-render" },
            factory.EndpointIds.ToArray());
        Assert.AreEqual(1, factory.Output.StartCount);
        Assert.IsTrue(keepalive.HasSession);

        await RemoveAndReleaseAsync(keepalive, accessor, notifications,
            presence, factory.Output);
    }

    [TestMethod]
    public async Task WaitForReady_DoesNotCompleteUntilOutputStartReturns()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var output = new FakeOutput { BlockStart = true };
        var factory = new FakeOutputFactory(output);
        var presence = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);

        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        Task ready = Task.Run(async () =>
            await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(2)));

        Assert.IsTrue(output.StartEntered.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsFalse(ready.IsCompleted,
            "Attached readiness must wait for WASAPI Play to succeed.");
        output.AllowStart.Set();
        await ready;

        await RemoveAndReleaseAsync(keepalive, accessor, notifications,
            presence, output);
    }

    [TestMethod]
    public async Task ExistingDualSenseEndpoint_IsNeverClaimedByNewSession()
    {
        var accessor = new FakeEndpointAccessor();
        accessor.Add("preexisting-pad", "DualSense Wireless Controller");
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDeviceIdentity();
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);

        keepalive.BeginSession();

        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            keepalive.WaitForReadyAsync(TimeSpan.FromMilliseconds(50)));
        Assert.AreEqual(0, factory.EndpointIds.Count);

        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        Assert.IsFalse(keepalive.HasSession);
    }

    [TestMethod]
    public async Task Teardown_RetainsOutputUntilEndpointAndParentAreGone()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        // This models the interval before and through Process.Kill.
        keepalive.BeginTeardown();
        await Task.Delay(150);
        Assert.AreEqual(0, presence.CheckCount);
        await keepalive.CompleteTeardownAsync();
        Assert.IsTrue(presence.CheckCount > 0);
        Assert.AreEqual(0, factory.Output.DisposeCount);
        Assert.IsTrue(keepalive.HasSession);

        // Endpoint inactivity alone is not enough while the USB parent remains.
        accessor.Remove("virtual-render");
        notifications.Notify();
        await Task.Delay(150);
        Assert.AreEqual(0, factory.Output.DisposeCount);
        Assert.IsTrue(keepalive.HasSession);

        presence.Present = false;
        notifications.Notify();
        await WaitUntilAsync(() => factory.Output.DisposeCount == 1);
        Assert.IsFalse(keepalive.HasSession);
    }

    [TestMethod]
    public async Task TeardownTimeout_ReleasesOnLaterRemovalNotification()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        Assert.AreEqual(0, factory.Output.DisposeCount);

        accessor.Remove("virtual-render");
        presence.Present = false;
        notifications.Notify();

        await WaitUntilAsync(() => factory.Output.DisposeCount == 1);
        Assert.AreEqual(1, notifications.DisposeCount);
        Assert.IsFalse(keepalive.HasSession);
    }

    [TestMethod]
    public async Task PartiallyStartedOutput_IsRetainedAfterStartFailure()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var output = new FakeOutput { StartException = new InvalidOperationException("init") };
        var factory = new FakeOutputFactory(output);
        var presence = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1)));
        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        Assert.AreEqual(0, output.DisposeCount,
            "A possibly activated client must survive while the device exists.");

        accessor.Remove("virtual-render");
        presence.Present = false;
        notifications.Notify();
        await WaitUntilAsync(() => output.DisposeCount == 1);
    }

    [TestMethod]
    public async Task ConcurrentReadinessAndNotifications_CreateOneOutput()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");

        Task[] waiters = Enumerable.Range(0, 8)
            .Select(_ => keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(2)))
            .ToArray();
        Parallel.For(0, 50, _ => notifications.Notify());
        await Task.WhenAll(waiters);

        Assert.AreEqual(1, factory.EndpointIds.Count);
        Assert.AreEqual(1, factory.Output.StartCount);

        await RemoveAndReleaseAsync(keepalive, accessor, notifications,
            presence, factory.Output);
    }

    [TestMethod]
    public async Task PostReadyOutputDeath_CompletesFatalTaskWithoutReopening()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var identity = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, identity);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));
        Task<Exception> fatalTermination =
            keepalive.WaitForUnexpectedTerminationAsync();
        Task releaseCompletion = keepalive.WaitForReleaseAsync();
        var failure = new InvalidOperationException("device invalidated");

        factory.Output.Terminate(failure);

        Exception reported = await fatalTermination.WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.AreSame(failure, reported);
        Assert.IsFalse(releaseCompletion.IsCompleted);
        notifications.Notify();
        await Task.Delay(50);
        Assert.AreEqual(1, factory.EndpointIds.Count,
            "A dead mandatory stream must never be reopened automatically.");

        keepalive.BeginTeardown();
        accessor.Remove("virtual-render");
        identity.Present = false;
        notifications.Notify();
        await keepalive.CompleteTeardownAsync();
        await releaseCompletion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task ExpectedTeardown_CancelsFatalTaskAndCompletesReleaseTask()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var identity = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, identity);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));
        Task<Exception> fatalTermination =
            keepalive.WaitForUnexpectedTerminationAsync();
        Task releaseCompletion = keepalive.WaitForReleaseAsync();

        keepalive.BeginTeardown();
        accessor.Remove("virtual-render");
        identity.Present = false;
        notifications.Notify();
        await keepalive.CompleteTeardownAsync();

        await releaseCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
            await fatalTermination);
        Assert.IsFalse(keepalive.HasSession);
    }

    [TestMethod]
    public async Task RetainedRemovalMonitor_PollsAtRemovalIntervalNotReadinessInterval()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence,
            removalPollInterval: TimeSpan.FromMilliseconds(400));
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        int checksAfterExplicitProbe = presence.CheckCount;

        await Task.Delay(150);
        Assert.AreEqual(checksAfterExplicitProbe, presence.CheckCount,
            "The retained monitor must not continue the 100 ms readiness poll.");
        await WaitUntilAsync(() => presence.CheckCount > checksAfterExplicitProbe);

        accessor.Remove("virtual-render");
        presence.Present = false;
        notifications.Notify();
        await WaitUntilAsync(() => factory.Output.DisposeCount == 1);
    }

    [TestMethod]
    public async Task RemovalProbeFailure_LogsOnceUntilAProbeRecovers()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDeviceIdentity { Present = true };
        var warnings = new ConcurrentQueue<string>();
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence,
            (message, warning) =>
            {
                if (warning)
                    warnings.Enqueue(message);
            }, removalPollInterval: TimeSpan.FromMilliseconds(30));
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        presence.ThrowOnIsPresent = true;
        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        await WaitUntilAsync(() => presence.CheckCount >= 3);
        Assert.AreEqual(1, warnings.Count(message =>
            message.Contains("Could not confirm virtual audio removal",
                StringComparison.Ordinal)));

        presence.ThrowOnIsPresent = false;
        int beforeRecovery = presence.CheckCount;
        await WaitUntilAsync(() => presence.CheckCount > beforeRecovery);

        presence.ThrowOnIsPresent = true;
        int beforeSecondFailure = presence.CheckCount;
        await WaitUntilAsync(() => presence.CheckCount > beforeSecondFailure);
        await WaitUntilAsync(() => warnings.Count(message =>
            message.Contains("Could not confirm virtual audio removal",
                StringComparison.Ordinal)) == 2);

        presence.ThrowOnIsPresent = false;
        presence.Present = false;
        accessor.Remove("virtual-render");
        notifications.Notify();
        await WaitUntilAsync(() => factory.Output.DisposeCount == 1);
    }

    [TestMethod]
    public async Task NewPhysicalUsbDualSense_IsNotClaimedAsVirtualEndpoint()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var identity = new FakeDeviceIdentity { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, identity);
        keepalive.BeginSession();
        accessor.Add("physical-render", "DualSense Wireless Controller",
            @"USB\VID_054C&PID_0CE6&MI_01\E82712345678");
        accessor.Add("virtual-render", "DualSense Wireless Controller",
            @"USB\VID_054C&PID_0CE6&MI_01\DS4WSPKCOMP001");

        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { "virtual-render" },
            factory.EndpointIds.ToArray());
        await RemoveAndReleaseAsync(keepalive, accessor, notifications,
            identity, factory.Output);
    }

    [TestMethod]
    public void FixedIdentity_RejectsPhysicalPadWithSameVidPid()
    {
        var identity = new WindowsNativeModeVirtualDeviceIdentity(
            () => new[]
            {
                NativeModeDevicePresence.VirtualDualSenseParentInstanceId,
                @"USB\VID_054C&PID_0CE6\E82712345678",
            },
            _ => null,
            _ => null);
        var physical = new NativeModeAudioEndpoint("physical", "DualSense",
            @"USB\VID_054C&PID_0CE6&MI_01\E82712345678");
        var virtualEndpoint = new NativeModeAudioEndpoint("virtual", "DualSense",
            @"USB\VID_054C&PID_0CE6&MI_01\DS4WSPKCOMP001");

        Assert.IsTrue(identity.IsPresent());
        Assert.IsFalse(identity.OwnsEndpoint(physical));
        Assert.IsTrue(identity.OwnsEndpoint(virtualEndpoint));
        Assert.IsFalse(NativeModeDevicePresence
            .IsVirtualDualSenseInstanceOrDescendant(
                @"USB\VID_054C&PID_0CE6\E82712345678"));
        Assert.IsTrue(NativeModeDevicePresence
            .IsVirtualDualSenseInstanceOrDescendant(
                @"USB\VID_054C&PID_0CE6&MI_01\DS4WSPKCOMP001&0001"));
    }

    [TestMethod]
    public void FixedIdentity_UsesParentChainAndContainerFallback()
    {
        Guid virtualContainer = Guid.NewGuid();
        const string endpointInstance =
            @"SWD\MMDEVAPI\{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}";
        const string virtualInterface =
            @"USB\VID_054C&PID_0CE6&MI_01\DS4WSPKCOMP001";
        var parents = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            [endpointInstance] = virtualInterface,
        };
        var identity = new WindowsNativeModeVirtualDeviceIdentity(
            () => new[]
            {
                NativeModeDevicePresence.VirtualDualSenseParentInstanceId,
            },
            instance => parents.TryGetValue(instance, out string parent)
                ? parent
                : null,
            instance => NativeModeDevicePresence
                .IsVirtualDualSenseParentInstanceId(instance)
                    ? virtualContainer
                    : null);

        Assert.IsTrue(identity.OwnsEndpoint(new NativeModeAudioEndpoint(
            "via-parent", "DualSense", endpointInstance)));
        Assert.IsTrue(identity.OwnsEndpoint(new NativeModeAudioEndpoint(
            "via-container", "DualSense", "SWD\\MMDEVAPI\\unknown",
            virtualContainer)));
        Assert.IsFalse(identity.OwnsEndpoint(new NativeModeAudioEndpoint(
            "other-container", "DualSense", "SWD\\MMDEVAPI\\other",
            Guid.NewGuid())));
    }

    private static NativeModeRenderKeepalive CreateKeepalive(
        FakeEndpointAccessor accessor, FakeNotificationSource notifications,
        FakeOutputFactory factory, FakeDeviceIdentity presence,
        Action<string, bool> log = null,
        TimeSpan? readinessPollInterval = null,
        TimeSpan? removalPollInterval = null) =>
        new NativeModeRenderKeepalive(accessor, notifications, factory, presence,
            log ?? ((_, _) => { }), readinessPollInterval,
            removalPollInterval);

    private static async Task RemoveAndReleaseAsync(
        NativeModeRenderKeepalive keepalive, FakeEndpointAccessor accessor,
        FakeNotificationSource notifications, FakeDeviceIdentity presence,
        FakeOutput output)
    {
        keepalive.BeginTeardown();
        accessor.Remove("virtual-render");
        presence.Present = false;
        notifications.Notify();
        await keepalive.CompleteTeardownAsync();
        await WaitUntilAsync(() => output.DisposeCount == 1);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.IsTrue(predicate(), "The asynchronous lifecycle transition timed out.");
    }

    private sealed class FakeEndpointAccessor : INativeModeAudioEndpointAccessor
    {
        private readonly object gate = new();
        private readonly List<NativeModeAudioEndpoint> renderEndpoints = new();

        public IReadOnlyList<NativeModeAudioEndpoint> GetActiveEndpoints(
            NativeModeAudioFlow flow)
        {
            lock (gate)
                return flow == NativeModeAudioFlow.Render
                    ? renderEndpoints.ToArray()
                    : Array.Empty<NativeModeAudioEndpoint>();
        }

        public string GetDefaultEndpointId(NativeModeAudioFlow flow,
            NativeModeAudioRole role) => throw new NotSupportedException();

        public void SetDefaultEndpoint(string endpointId,
            NativeModeAudioRole role) => throw new NotSupportedException();

        public void Add(string id, string name, string deviceInstanceId = null,
            Guid? containerId = null)
        {
            lock (gate)
                renderEndpoints.Add(new NativeModeAudioEndpoint(id, name,
                    deviceInstanceId, containerId));
        }

        public void Remove(string id)
        {
            lock (gate)
            {
                renderEndpoints.RemoveAll(endpoint => string.Equals(endpoint.Id,
                    id, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private sealed class FakeNotificationSource :
        INativeModeAudioNotificationSource
    {
        private readonly object gate = new();
        private readonly List<Action> callbacks = new();
        public int DisposeCount;

        public IDisposable Subscribe(Action endpointOrDefaultChanged)
        {
            lock (gate)
                callbacks.Add(endpointOrDefaultChanged);
            return new Subscription(this, endpointOrDefaultChanged);
        }

        public void Notify()
        {
            Action[] snapshot;
            lock (gate)
                snapshot = callbacks.ToArray();
            foreach (Action callback in snapshot)
                callback();
        }

        private void Remove(Action callback)
        {
            lock (gate)
                callbacks.Remove(callback);
            Interlocked.Increment(ref DisposeCount);
        }

        private sealed class Subscription : IDisposable
        {
            private FakeNotificationSource owner;
            private readonly Action callback;

            public Subscription(FakeNotificationSource owner, Action callback)
            {
                this.owner = owner;
                this.callback = callback;
            }

            public void Dispose() =>
                Interlocked.Exchange(ref owner, null)?.Remove(callback);
        }
    }

    private sealed class FakeOutputFactory : INativeModeRenderOutputFactory
    {
        private readonly FakeOutput suppliedOutput;
        public ConcurrentQueue<string> EndpointIds { get; } = new();
        public FakeOutput Output { get; private set; }

        public FakeOutputFactory(FakeOutput suppliedOutput = null)
        {
            this.suppliedOutput = suppliedOutput;
        }

        public INativeModeRenderOutput Create(string endpointId)
        {
            EndpointIds.Enqueue(endpointId);
            Output = suppliedOutput ?? new FakeOutput();
            return Output;
        }
    }

    private sealed class FakeOutput : INativeModeRenderOutput
    {
        public bool BlockStart;
        public Exception StartException;
        public ManualResetEventSlim StartEntered { get; } = new(false);
        public ManualResetEventSlim AllowStart { get; } = new(false);
        public int StartCount;
        public int DisposeCount;

        public event EventHandler<NativeModeRenderOutputTerminatedEventArgs>
            UnexpectedTermination;

        public void Start()
        {
            Interlocked.Increment(ref StartCount);
            StartEntered.Set();
            if (BlockStart)
                Assert.IsTrue(AllowStart.Wait(TimeSpan.FromSeconds(2)));
            if (StartException != null)
                throw StartException;
        }

        public void Dispose() => Interlocked.Increment(ref DisposeCount);

        public void Terminate(Exception exception = null) =>
            UnexpectedTermination?.Invoke(this,
                new NativeModeRenderOutputTerminatedEventArgs(exception));
    }

    private sealed class FakeDeviceIdentity : INativeModeVirtualDeviceIdentity
    {
        public volatile bool Present;
        public volatile bool ThrowOnIsPresent;
        public int CheckCount;
        public HashSet<string> OwnedEndpointIds { get; } =
            new(StringComparer.OrdinalIgnoreCase) { "virtual-render" };

        public bool IsPresent()
        {
            Interlocked.Increment(ref CheckCount);
            if (ThrowOnIsPresent)
                throw new InvalidOperationException("present-device probe failed");
            return Present;
        }

        public bool OwnsEndpoint(NativeModeAudioEndpoint endpoint) =>
            OwnedEndpointIds.Contains(endpoint.Id);
    }
}
