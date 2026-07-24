// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;

namespace VirtualDualSenseUsbip.Live;

internal static class NativeModeContainmentSelfTest
{
    private static readonly TimeSpan TestPoll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    public static async Task RunAsync()
    {
        int checks = 0;
        checks += await ControlLeaseChecksAsync();
        checks += BrokenPipeWriterChecks();
        checks += await BaselineAndRemovalChecksAsync();
        checks += await TransientDiscoveryRetryChecksAsync();
        checks += await PreReadyShutdownBarrierChecksAsync();
        checks += await FailedStartRetentionChecksAsync();
        checks += await ProbeFailureRetentionChecksAsync();
        checks += await UnexpectedTerminationChecksAsync();
        checks += await CommittedImportBarrierChecksAsync();
        Console.WriteLine(
            $"Native Mode containment self-test passed ({checks} checks).");
    }

    private static async Task<int> ControlLeaseChecksAsync()
    {
        NativeModeControlLeaseResult stop = await NativeModeControlLease.WaitAsync(
            new StringReader(" stop \n"));
        Assert(stop.Signal == NativeModeControlSignal.StopRequested,
            "The stop command was not recognized.");

        NativeModeControlLeaseResult eof = await NativeModeControlLease.WaitAsync(
            new StringReader(string.Empty));
        Assert(eof.Signal == NativeModeControlSignal.ParentPipeClosed,
            "EOF was not treated as parent death.");

        NativeModeControlLeaseResult invalid =
            await NativeModeControlLease.WaitAsync(
                new StringReader("detach\n"));
        Assert(invalid.Signal == NativeModeControlSignal.ProtocolViolation,
            "An unsupported command was not rejected.");

        using var blockingInput = new SynchronouslyBlockingTextReader();
        Task<Task<NativeModeControlLeaseResult>> startCall =
            Task.Factory.StartNew(
                () => NativeModeControlLease.Start(blockingInput),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        Task startWinner = await Task.WhenAny(
            startCall, Task.Delay(TestTimeout));
        if (!ReferenceEquals(startWinner, startCall))
            blockingInput.Complete("stop");
        Assert(ReferenceEquals(startWinner, startCall),
            "Starting the control lease blocked helper initialization.");
        Task<NativeModeControlLeaseResult> backgroundLease = await startCall;
        Assert(blockingInput.WaitForReadStart(TestTimeout),
            "The dedicated control-lease reader did not start.");
        Assert(!backgroundLease.IsCompleted,
            "The blocking control lease completed before input arrived.");
        blockingInput.Complete("stop");
        NativeModeControlLeaseResult backgroundResult =
            await backgroundLease.WaitAsync(TestTimeout);
        Assert(backgroundResult.Signal == NativeModeControlSignal.StopRequested,
            "The dedicated control-lease reader lost the stop signal.");
        return 7;
    }

    private static int BrokenPipeWriterChecks()
    {
        var inner = new ThrowingWriter();
        var writer = new BrokenPipeTolerantTextWriter(inner);
        writer.WriteLine("first");
        writer.WriteLine("second");
        Assert(inner.Attempts == 1,
            "The console wrapper retried a known-broken output pipe.");
        return 1;
    }

    private static async Task<int> BaselineAndRemovalChecksAsync()
    {
        var endpoints = new MutableEndpointSource();
        var identity = new MutableIdentity { Present = true };
        var physical = Endpoint("physical", owned: true);
        endpoints.Set(physical);
        var lease = new FakeLease();
        var logs = new ConcurrentQueue<string>();
        var keepalive = CreateKeepalive(
            endpoints, identity, new FakeLeaseFactory(lease), logs);

        keepalive.BeginSession();
        await Task.Delay(TimeSpan.FromMilliseconds(30));
        Assert(!keepalive.HasLease,
            "A render endpoint present in the baseline was selected.");

        endpoints.Set(physical, Endpoint("virtual", owned: true));
        await keepalive.WaitForReadyAsync().WaitAsync(TestTimeout);
        Assert(lease.StartCount == 1,
            "The newly-added exact virtual endpoint was not opened.");
        Assert(logs.Contains(
                $"{NativeModeChildRenderKeepalive.ReadyMarker} active."),
            "The stable ready marker was not emitted.");

        keepalive.BeginTeardown();
        using (var shortWait = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(40)))
        {
            await ExpectCancellationAsync(
                keepalive.RetainUntilRemovedAndReleaseAsync(shortWait.Token));
        }
        Assert(lease.DisposeCount == 0,
            "The render lease closed while the endpoint/parent remained.");

        endpoints.Set();
        identity.Present = false;
        await keepalive.RetainUntilRemovedAndReleaseAsync()
            .WaitAsync(TestTimeout);
        Assert(lease.DisposeCount == 1,
            "The render lease was not released after confirmed removal.");
        Assert(logs.Any(line => line.StartsWith(
                NativeModeChildRenderKeepalive.ReleasedMarker,
                StringComparison.Ordinal)),
            "The stable released marker was not emitted.");
        return 6;
    }

    private static async Task<int> TransientDiscoveryRetryChecksAsync()
    {
        var endpoints = new TransientFailureEndpointSource(
            Endpoint("virtual", owned: true), failureCount: 3);
        var identity = new MutableIdentity { Present = true };
        var lease = new FakeLease();
        var logs = new ConcurrentQueue<string>();
        var keepalive = new NativeModeChildRenderKeepalive(
            endpoints,
            identity,
            new FakeLeaseFactory(lease),
            logs.Enqueue,
            TestPoll,
            TestPoll);

        // The first read is the synchronous pre-attach baseline. The next
        // three reads fail, then discovery recovers while the committed-import
        // barrier remains fail-closed.
        keepalive.BeginSession();
        await keepalive.WaitForShutdownSafetyAsync(
                static () => true)
            .WaitAsync(TestTimeout);

        Assert(endpoints.ReadCount >= 5,
            "Transient endpoint failures were not retried.");
        Assert(lease.StartCount == 1,
            "Discovery recovery did not start the helper render lease.");
        Assert(logs.Count(line => line.Contains(
                "temporarily unavailable",
                StringComparison.Ordinal)) == 1,
            "Transient discovery failures were not rate-limited.");
        Assert(logs.Count(line => line.StartsWith(
                NativeModeChildRenderKeepalive.FailedMarker,
                StringComparison.Ordinal)) == 0,
            "A transient discovery failure incorrectly faulted the helper lease.");

        endpoints.Removed = true;
        identity.Present = false;
        await keepalive.RetainUntilRemovedAndReleaseAsync()
            .WaitAsync(TestTimeout);
        Assert(lease.DisposeCount == 1,
            "The recovered render lease was not released after removal.");
        return 5;
    }

    private static async Task<int> PreReadyShutdownBarrierChecksAsync()
    {
        var endpoints = new MutableEndpointSource();
        var identity = new MutableIdentity { Present = true };
        var lease = new FakeLease();
        var keepalive = CreateKeepalive(
            endpoints,
            identity,
            new FakeLeaseFactory(lease),
            new ConcurrentQueue<string>());

        keepalive.BeginSession();
        keepalive.BeginTeardown();
        using (var shortWait = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(40)))
        {
            await ExpectCancellationAsync(
                keepalive.WaitForShutdownSafetyAsync(
                    shortWait.Token));
        }
        Assert(!keepalive.HasLease,
            "The pre-ready test unexpectedly started a lease.");

        // BeginTeardown must not cancel discovery. The barrier should complete
        // only after a healthy child pin is running.
        endpoints.Set(Endpoint("virtual", owned: true));
        await keepalive.WaitForShutdownSafetyAsync(
                CancellationToken.None)
            .WaitAsync(TestTimeout);
        Assert(lease.StartCount == 1,
            "Discovery stopped during the post-attach/pre-ready window.");

        lease.RaiseUnexpectedTermination();
        using (var shortWait = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(40)))
        {
            await ExpectCancellationAsync(
                keepalive.WaitForShutdownSafetyAsync(
                    shortWait.Token));
        }
        Assert(keepalive.HasLease,
            "A terminated lease was disposed before device removal.");

        endpoints.Set();
        identity.Present = false;
        await keepalive.RetainUntilRemovedAndReleaseAsync()
            .WaitAsync(TestTimeout);
        Assert(lease.DisposeCount == 1,
            "The pre-ready test lease was not released after removal.");
        return 5;
    }

    private static async Task<int> FailedStartRetentionChecksAsync()
    {
        var endpoints = new MutableEndpointSource();
        var identity = new MutableIdentity { Present = true };
        var lease = new FakeLease { FailStart = true };
        var logs = new ConcurrentQueue<string>();
        var keepalive = CreateKeepalive(
            endpoints, identity, new FakeLeaseFactory(lease), logs);

        keepalive.BeginSession();
        endpoints.Set(Endpoint("virtual", owned: true));
        _ = await keepalive.WaitForFailureAsync().WaitAsync(TestTimeout);
        Assert(keepalive.HasLease,
            "A partially-started lease was not retained.");
        Assert(logs.Count(line => line.StartsWith(
                NativeModeChildRenderKeepalive.FailedMarker,
                StringComparison.Ordinal)) == 1,
            "The failure marker was not stable and single-shot.");

        keepalive.BeginTeardown();
        using (var shortWait = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(40)))
        {
            await ExpectCancellationAsync(
                keepalive.WaitForShutdownSafetyAsync(
                    shortWait.Token));
        }
        Assert(lease.DisposeCount == 0,
            "A failed-start lease closed before confirmed removal.");

        endpoints.Set();
        identity.Present = false;
        await keepalive.RetainUntilRemovedAndReleaseAsync()
            .WaitAsync(TestTimeout);
        Assert(lease.DisposeCount == 1,
            "A failed-start lease was not released after removal.");
        return 5;
    }

    private static async Task<int> ProbeFailureRetentionChecksAsync()
    {
        var endpoints = new MutableEndpointSource();
        var identity = new MutableIdentity { Present = true };
        var lease = new FakeLease();
        var logs = new ConcurrentQueue<string>();
        var keepalive = CreateKeepalive(
            endpoints, identity, new FakeLeaseFactory(lease), logs);

        keepalive.BeginSession();
        endpoints.Set(Endpoint("virtual", owned: true));
        await keepalive.WaitForReadyAsync().WaitAsync(TestTimeout);
        keepalive.BeginTeardown();
        endpoints.ThrowOnRead = true;

        using (var shortWait = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(60)))
        {
            await ExpectCancellationAsync(
                keepalive.RetainUntilRemovedAndReleaseAsync(shortWait.Token));
        }
        Assert(lease.DisposeCount == 0,
            "A probe failure was incorrectly treated as removal.");
        Assert(logs.Count(line => line.Contains(
                "removal could not be confirmed",
                StringComparison.Ordinal)) == 1,
            "Repeated probe failures were not rate-limited.");

        endpoints.ThrowOnRead = false;
        endpoints.Set();
        identity.Present = false;
        await keepalive.RetainUntilRemovedAndReleaseAsync()
            .WaitAsync(TestTimeout);
        Assert(lease.DisposeCount == 1,
            "The lease did not release after a later successful absence probe.");
        return 3;
    }

    private static async Task<int> UnexpectedTerminationChecksAsync()
    {
        var endpoints = new MutableEndpointSource();
        var identity = new MutableIdentity { Present = true };
        var lease = new FakeLease();
        var logs = new ConcurrentQueue<string>();
        var keepalive = CreateKeepalive(
            endpoints, identity, new FakeLeaseFactory(lease), logs);

        keepalive.BeginSession();
        endpoints.Set(Endpoint("virtual", owned: true));
        await keepalive.WaitForReadyAsync().WaitAsync(TestTimeout);
        lease.RaiseUnexpectedTermination();
        _ = await keepalive.WaitForFailureAsync().WaitAsync(TestTimeout);
        Assert(logs.Count(line => line.StartsWith(
                NativeModeChildRenderKeepalive.FailedMarker,
                StringComparison.Ordinal)) == 1,
            "Unexpected termination did not publish one failure marker.");

        keepalive.BeginTeardown();
        endpoints.Set();
        identity.Present = false;
        await keepalive.RetainUntilRemovedAndReleaseAsync()
            .WaitAsync(TestTimeout);
        return 1;
    }

    private static async Task<int> CommittedImportBarrierChecksAsync()
    {
        var endpoints = new MutableEndpointSource();
        var identity = new MutableIdentity { Present = false };
        var keepalive = CreateKeepalive(
            endpoints,
            identity,
            new FakeLeaseFactory(new FakeLease()),
            new ConcurrentQueue<string>());
        bool committedImport = true;

        keepalive.BeginSession();
        using (var shortWait = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(40)))
        {
            await ExpectCancellationAsync(
                keepalive.WaitForShutdownSafetyAsync(
                    () => committedImport,
                    shortWait.Token));
        }

        committedImport = false;
        await keepalive.WaitForShutdownSafetyAsync(
                () => committedImport)
            .WaitAsync(TestTimeout);
        await keepalive.RetainUntilRemovedAndReleaseAsync()
            .WaitAsync(TestTimeout);
        return 2;
    }

    private static NativeModeChildRenderKeepalive CreateKeepalive(
        MutableEndpointSource endpoints,
        MutableIdentity identity,
        FakeLeaseFactory factory,
        ConcurrentQueue<string> logs) =>
        new(
            endpoints,
            identity,
            factory,
            logs.Enqueue,
            TestPoll,
            TestPoll);

    private static NativeModeRenderEndpoint Endpoint(
        string id,
        bool owned) =>
        new(id, $"Endpoint {id}", owned ? "owned" : "other", null);

    private static async Task ExpectCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException(
            "The fail-closed wait unexpectedly completed.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class MutableEndpointSource : INativeModeRenderEndpointSource
    {
        private readonly object gate = new();
        private NativeModeRenderEndpoint[] endpoints = [];
        public bool ThrowOnRead { get; set; }

        public void Set(params NativeModeRenderEndpoint[] value)
        {
            lock (gate)
                endpoints = value.ToArray();
        }

        public IReadOnlyList<NativeModeRenderEndpoint>
            GetActiveRenderEndpoints()
        {
            if (ThrowOnRead)
                throw new InvalidOperationException("Synthetic endpoint failure.");
            lock (gate)
                return endpoints.ToArray();
        }
    }

    private sealed class SynchronouslyBlockingTextReader : TextReader
    {
        private readonly ManualResetEventSlim readStarted = new(false);
        private readonly ManualResetEventSlim release = new(false);
        private string? completedLine;

        public bool WaitForReadStart(TimeSpan timeout) =>
            readStarted.Wait(timeout);

        public void Complete(string? line)
        {
            completedLine = line;
            release.Set();
        }

        public override ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken = default)
        {
            readStarted.Set();
            release.Wait(cancellationToken);
            return ValueTask.FromResult(completedLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                release.Set();
                readStarted.Dispose();
                release.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class MutableIdentity : INativeModeVirtualDeviceIdentity
    {
        public bool Present { get; set; }
        public bool ThrowOnPresence { get; set; }

        public bool IsPresent()
        {
            if (ThrowOnPresence)
                throw new InvalidOperationException("Synthetic PnP failure.");
            return Present;
        }

        public bool OwnsEndpoint(NativeModeRenderEndpoint endpoint) =>
            Present &&
            string.Equals(endpoint.DeviceInstanceId, "owned",
                StringComparison.Ordinal);
    }

    private sealed class TransientFailureEndpointSource :
        INativeModeRenderEndpointSource
    {
        private readonly NativeModeRenderEndpoint endpoint;
        private int failuresRemaining;
        private int readCount;

        public TransientFailureEndpointSource(
            NativeModeRenderEndpoint endpoint,
            int failureCount)
        {
            this.endpoint = endpoint;
            failuresRemaining = failureCount;
        }

        public int ReadCount => Volatile.Read(ref readCount);
        public bool Removed { get; set; }

        public IReadOnlyList<NativeModeRenderEndpoint>
            GetActiveRenderEndpoints()
        {
            int currentRead = Interlocked.Increment(ref readCount);
            if (currentRead > 1 &&
                Interlocked.Decrement(ref failuresRemaining) >= 0)
            {
                throw new InvalidOperationException(
                    "Synthetic transient endpoint failure.");
            }

            return Removed ? [] : currentRead == 1 ? [] : [endpoint];
        }
    }

    private sealed class FakeLeaseFactory : INativeModeRenderLeaseFactory
    {
        private readonly FakeLease lease;
        public FakeLeaseFactory(FakeLease lease) => this.lease = lease;
        public INativeModeRenderLease Create(string endpointId) => lease;
    }

    private sealed class FakeLease : INativeModeRenderLease
    {
        public event EventHandler<NativeModeRenderLeaseTerminatedEventArgs>?
            UnexpectedTermination;
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool FailStart { get; set; }

        public void Start()
        {
            StartCount++;
            if (FailStart)
                throw new InvalidOperationException("Synthetic start failure.");
        }

        public void RaiseUnexpectedTermination() =>
            UnexpectedTermination?.Invoke(
                this,
                new NativeModeRenderLeaseTerminatedEventArgs(
                    new InvalidOperationException(
                        "Synthetic render termination.")));

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public int Attempts { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("Synthetic broken pipe.");
        }
    }
}
