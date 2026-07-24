using System.ComponentModel;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeElevationBrokerTests
{
    [TestMethod]
    public async Task EnsureAttachTask_IsOnlyANonElevatedPreflight()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false);

        NativeModeAttachResult result = await broker.EnsureAttachTaskAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Reason, "each time");
        Assert.AreEqual(0, runner.AsyncCalls.Count);
    }

    [TestMethod]
    public async Task EnsureAttachTask_RemovesAndRechecksExactLegacyTask()
    {
        int presenceChecks = 0;
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false,
            legacyTaskPresent: () => ++presenceChecks == 1);

        NativeModeAttachResult result = await broker.EnsureAttachTaskAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, presenceChecks);
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        Assert.AreEqual(@"C:\Windows\System32\schtasks.exe",
            runner.AsyncCalls[0].Executable);
        CollectionAssert.AreEqual(new[]
        {
            "/Delete", "/TN", @"DS4Windows\NativeDualSenseAttach", "/F",
        }, runner.AsyncCalls[0].Arguments);
        Assert.IsTrue(runner.AsyncCalls[0].Elevate);
    }

    [TestMethod]
    public async Task RunAttach_RefusesWhenLegacyTaskDeletionIsCanceled()
    {
        var runner = new FakeCommandRunner
        {
            AsynchronousResult = new NativeModeCommandResult(1223,
                string.Empty, "The elevation prompt was canceled."),
        };
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false,
            legacyTaskPresent: () => true);

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.LegacyTaskCleanupFailed,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "must be removed");
        StringAssert.Contains(result.Reason, "canceled");
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        CollectionAssert.AreEqual(new[]
        {
            "/Delete", "/TN", @"DS4Windows\NativeDualSenseAttach", "/F",
        }, runner.AsyncCalls[0].Arguments);
    }

    [TestMethod]
    public async Task RunAttach_RefusesWhenLegacyTaskStillExistsAfterDelete()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false,
            legacyTaskPresent: () => true);

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.LegacyTaskCleanupFailed,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "still exists");
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        Assert.AreEqual("schtasks.exe",
            Path.GetFileName(runner.AsyncCalls[0].Executable));
    }

    [DataTestMethod]
    [DataRow("usbip.exe")]
    [DataRow(@"C:\Program Files\USBip\helper.exe")]
    [DataRow(@"\\server\share\usbip.exe")]
    [DataRow(@"D:\USBip\usbip.exe")]
    [DataRow(@"C:\Program Files Evil\USBip\usbip.exe")]
    [DataRow(@"C:\Program Files\USBip\..\USBip\usbip.exe")]
    public async Task RunAttach_InvalidOrRemotePathFailsClosed(string usbipPath)
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false);

        NativeModeAttachResult result = await broker.RunAttachAsync(usbipPath);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.InvalidUsbipPath,
            result.FailureKind);
        Assert.AreEqual(0, runner.AsyncCalls.Count);
    }

    [TestMethod]
    public async Task RunAttach_MissingExecutableFailsBeforeElevation()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false, fileExists: _ => false);

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.UsbipMissing,
            result.FailureKind);
        Assert.AreEqual(0, runner.AsyncCalls.Count);
    }

    [TestMethod]
    public async Task WaitForDeviceArrival_StopsAtFirstPresenceObservation()
    {
        int checks = 0;
        int delays = 0;

        bool arrived = await NativeModeElevationBroker.WaitForDeviceArrivalAsync(
            () => ++checks >= 3,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.IsTrue(arrived);
        Assert.AreEqual(3, checks);
        Assert.AreEqual(2, delays);
    }

    [TestMethod]
    public async Task WaitForDeviceArrival_ReturnsFalseAtDeadline()
    {
        bool arrived = await NativeModeElevationBroker.WaitForDeviceArrivalAsync(
            () => false, (_, _) => Task.CompletedTask,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.IsFalse(arrived);
    }

    [TestMethod]
    public async Task RunAttach_WhenElevatedRunsUsbipDirectlyThenConfirmsArrival()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: true,
            devicePresent: () => true);
        string usbipPath = @"C:\Program Files\USBip\usbip.exe";

        NativeModeAttachResult result = await broker.RunAttachAsync(usbipPath);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        Assert.AreEqual(usbipPath, runner.AsyncCalls[0].Executable);
        CollectionAssert.AreEqual(new[]
        {
            "attach", "-r", "127.0.0.1", "-b", "1-1",
            "--serial", "DS4WSPKCOMP001", "--once",
        }, runner.AsyncCalls[0].Arguments);
        Assert.IsFalse(runner.AsyncCalls[0].Elevate);
    }

    [TestMethod]
    public async Task RunAttach_WhenNotElevatedPromptsForOnlyTheFixedAction()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => true);
        string usbipPath = @"C:\Program Files\USBip\usbip.exe";

        NativeModeAttachResult result = await broker.RunAttachAsync(usbipPath);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        Assert.AreEqual(Path.GetFullPath(usbipPath),
            runner.AsyncCalls[0].Executable);
        CollectionAssert.AreEqual(new[]
        {
            "attach", "-r", "127.0.0.1", "-b", "1-1",
            "--serial", "DS4WSPKCOMP001", "--once",
        }, runner.AsyncCalls[0].Arguments);
        Assert.IsTrue(runner.AsyncCalls[0].Elevate);
    }

    [TestMethod]
    public async Task RunAttach_CanceledUacReturnsCommandFailure()
    {
        var runner = new FakeCommandRunner
        {
            AsynchronousResult = new NativeModeCommandResult(1223,
                string.Empty, "The elevation prompt was canceled."),
        };
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false);

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.CommandFailed,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "canceled");
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        Assert.IsTrue(runner.AsyncCalls[0].Elevate);
    }

    [TestMethod]
    public async Task RunAttach_CallerCancellationInterruptsPendingElevation()
    {
        CancellationToken observedToken = default;
        var runner = new FakeCommandRunner
        {
            AsynchronousImplementation = async cancellationToken =>
            {
                observedToken = cancellationToken;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new NativeModeCommandResult(0, string.Empty, string.Empty);
            },
        };
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false);
        using var cancellation = new CancellationTokenSource();

        Task<NativeModeAttachResult> attach = broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe", cancellation.Token);
        cancellation.Cancel();

        NativeModeAttachResult result = await attach;

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.CommandFailed,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "canceled");
        Assert.IsNotNull(result.PendingCommandCompletion);
        Assert.IsTrue(observedToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task RunAttach_IgnoredCancellationStillHasBoundedTimeout()
    {
        var pending = new TaskCompletionSource<NativeModeCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCommandRunner
        {
            AsynchronousImplementation = _ => pending.Task,
        };
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false,
            commandTimeout: TimeSpan.FromMilliseconds(20));

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe").WaitAsync(
                TimeSpan.FromSeconds(1));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.CommandFailed,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "did not finish within");
        Assert.AreSame(pending.Task, result.PendingCommandCompletion);
        Assert.IsTrue(runner.AsyncCalls[0].CancellationToken
            .IsCancellationRequested);

        pending.SetResult(new NativeModeCommandResult(
            0, string.Empty, string.Empty));
        await result.PendingCommandCompletion;
    }

    [TestMethod]
    public async Task RunAttach_PresenceQueryFailureReturnsSafeAttachFailure()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: true,
            devicePresent: () => throw new Win32Exception(5, "access denied"));

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.DeviceArrivalTimeout,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "Could not query present devices");
    }

    [TestMethod]
    public void PresentEnumeration_UsesAllClassesAndExactVirtualParentOnly()
    {
        int observedFlags = 0;

        IReadOnlyList<string> matches = NativeModeDevicePresence
            .GetPresentVirtualDualSenseInstanceIds(flags =>
            {
                observedFlags = flags;
                return new[]
                {
                    NativeModeDevicePresence.VirtualDualSenseParentInstanceId,
                    @"USB\VID_054C&PID_0CE6\E82712345678",
                    @"USB\VID_054C&PID_0CE6&MI_01\DS4WSPKCOMP001&0001",
                };
            });

        Assert.AreEqual(NativeMethods.DIGCF_PRESENT |
            NativeMethods.DIGCF_ALLCLASSES, observedFlags);
        CollectionAssert.AreEqual(new[]
        {
            NativeModeDevicePresence.VirtualDualSenseParentInstanceId,
        }, matches.ToArray());
    }

    [TestMethod]
    public void DirectPresenceEnumeration_PropagatesFailureForFailClosedCleanup()
    {
        Assert.ThrowsException<Win32Exception>(() =>
            NativeModeDevicePresence.GetPresentVirtualDualSenseInstanceIds(
                _ => throw new Win32Exception(5, "probe failed")));
    }

    [TestMethod]
    public async Task ReadinessAwaiter_CompletesOnServingTransition()
    {
        NativeModeState state = NativeModeState.Starting;
        string detail = "Starting";
        EventHandler<NativeModeStateChangedEventArgs> stateChanged = null;
        Task waitTask = NativeModeReadinessAwaiter.WaitForServingAsync(
            () => (state, detail),
            handler => stateChanged += handler,
            handler => stateChanged -= handler,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        state = NativeModeState.Serving;
        detail = "USB/IP server listening";
        stateChanged?.Invoke(this,
            new NativeModeStateChangedEventArgs(state, detail));
        await waitTask;

        Assert.IsNull(stateChanged);
    }

    [TestMethod]
    public async Task ReadinessAwaiter_PropagatesTerminalFailureDetail()
    {
        Task waitTask = NativeModeReadinessAwaiter.WaitForServingAsync(
            () => (NativeModeState.Faulted, "No physical Bluetooth DualSense"),
            _ => { }, _ => { }, TimeSpan.FromSeconds(1),
            CancellationToken.None);

        InvalidOperationException exception =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await waitTask);
        StringAssert.Contains(exception.Message,
            "No physical Bluetooth DualSense");
    }

    [TestMethod]
    public async Task ReadinessAwaiter_ReportsBoundedTimeout()
    {
        Task waitTask = NativeModeReadinessAwaiter.WaitForServingAsync(
            () => (NativeModeState.Starting, "Starting"),
            _ => { }, _ => { }, TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        TimeoutException exception = await Assert.ThrowsExceptionAsync<TimeoutException>(
            async () => await waitTask);
        StringAssert.Contains(exception.Message, "did not become ready");
    }

    private static NativeModeElevationBroker CreateBroker(
        FakeCommandRunner runner, bool administrator,
        Func<bool> devicePresent, Func<string, bool> fileExists = null,
        Func<IReadOnlyList<string>> trustedRoots = null,
        Func<bool> legacyTaskPresent = null,
        TimeSpan? commandTimeout = null)
    {
        return new NativeModeElevationBroker(fileExists ?? (_ => true),
            trustedRoots ?? (() => new[]
            {
                @"C:\Program Files",
                @"C:\Program Files (x86)",
            }), legacyTaskPresent ?? (() => false),
            () => administrator, runner, devicePresent,
            (_, _) => Task.CompletedTask,
            @"C:\Windows\System32\schtasks.exe", commandTimeout);
    }

    private sealed class FakeCommandRunner : INativeModeCommandRunner
    {
        public NativeModeCommandResult AsynchronousResult { get; set; } =
            new NativeModeCommandResult(0, string.Empty, string.Empty);
        public Func<CancellationToken, Task<NativeModeCommandResult>>
            AsynchronousImplementation { get; set; }
        public List<CommandCall> AsyncCalls { get; } = new List<CommandCall>();

        public Task<NativeModeCommandResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken)
        {
            AsyncCalls.Add(new CommandCall(executable, arguments.ToArray(),
                elevate, cancellationToken));
            return AsynchronousImplementation?.Invoke(cancellationToken) ??
                Task.FromResult(AsynchronousResult);
        }
    }

    private sealed record CommandCall(string Executable, string[] Arguments,
        bool Elevate, CancellationToken CancellationToken);
}
