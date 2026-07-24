using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeLifecycleOrchestrationTests
{
    [TestMethod]
    public async Task Startup_RegistersAudioGuardBeforeProtectedAttachSequence()
    {
        var calls = new List<string>();

        await NativeModeStartupOrchestration.RunWithAudioDefaultProtectionAsync(
            () =>
            {
                calls.Add("capture");
                return CreateSnapshot();
            },
            _ => calls.Add("register"),
            () =>
            {
                calls.Add("attach");
                calls.Add("keepalive-ready");
                return Task.CompletedTask;
            });

        CollectionAssert.AreEqual(new[]
        {
            "capture", "register", "attach", "keepalive-ready",
        }, calls);
    }

    [TestMethod]
    public async Task Startup_NullSnapshotAbortsBeforeGuardAndProtectedStartup()
    {
        int guardStarts = 0;
        int protectedStarts = 0;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            NativeModeStartupOrchestration.RunWithAudioDefaultProtectionAsync(
                () => null,
                _ => guardStarts++,
                () =>
                {
                    protectedStarts++;
                    return Task.CompletedTask;
                }));

        Assert.AreEqual(0, guardStarts);
        Assert.AreEqual(0, protectedStarts);
    }

    [TestMethod]
    public async Task Startup_GuardRegistrationFailureWithSnapshotPreventsProtectedStartup()
    {
        var registrationFailure = new InvalidOperationException(
            "notification registration failed");
        int protectedStarts = 0;

        InvalidOperationException observed =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                NativeModeStartupOrchestration.RunWithAudioDefaultProtectionAsync(
                    CreateSnapshot,
                    _ => throw registrationFailure,
                    () =>
                    {
                        protectedStarts++;
                        return Task.CompletedTask;
                    }));

        Assert.AreSame(registrationFailure, observed);
        Assert.AreEqual(0, protectedStarts);
    }

    [TestMethod]
    public async Task KeepaliveObserver_FaultsOnlyTheCurrentSession()
    {
        var failure = new InvalidOperationException("render stopped");
        int marked = 0;

        await NativeModeStartupOrchestration.ObserveUnexpectedTerminationAsync(
            Task.FromResult<Exception>(failure),
            () => true,
            observed =>
            {
                Assert.AreSame(failure, observed);
                marked++;
                return Task.CompletedTask;
            });

        await NativeModeStartupOrchestration.ObserveUnexpectedTerminationAsync(
            Task.FromResult<Exception>(failure),
            () => false,
            _ =>
            {
                marked++;
                return Task.CompletedTask;
            });

        Assert.AreEqual(1, marked);
    }

    [TestMethod]
    public async Task KeepaliveObserver_ExpectedTeardownCancellationIsNormal()
    {
        int marked = 0;

        await NativeModeStartupOrchestration.ObserveUnexpectedTerminationAsync(
            Task.FromCanceled<Exception>(new CancellationToken(canceled: true)),
            () => true,
            _ =>
            {
                marked++;
                return Task.CompletedTask;
            });

        Assert.AreEqual(0, marked);
    }

    [TestMethod]
    public async Task PendingCommand_BlocksRecoveryUntilItIsTerminal()
    {
        var pending = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool recovered = false;

        Task barrier = NativeModeStartupOrchestration
            .RunAfterCommandTerminationAsync(pending.Task, () =>
            {
                recovered = true;
                return Task.CompletedTask;
            });

        Assert.IsFalse(barrier.IsCompleted);
        Assert.IsFalse(recovered);

        pending.SetResult();
        await barrier;

        Assert.IsTrue(recovered);
    }

    [TestMethod]
    public async Task FailedPendingCommand_StillRunsOrderedRecovery()
    {
        bool recovered = false;

        await NativeModeStartupOrchestration
            .RunAfterCommandTerminationAsync(
                Task.FromException(new InvalidOperationException(
                    "elevated client failed")),
                () =>
                {
                    recovered = true;
                    return Task.CompletedTask;
                });

        Assert.IsTrue(recovered);
    }

    [TestMethod]
    public async Task StopFailure_RetainsEveryProtectionAndReportsFailure()
    {
        var failure = new InvalidOperationException("kill failed");
        int releases = 0;
        bool renderTeardownBegan = false;
        bool renderTeardownCompleted = false;

        NativeModeTeardownAttempt result =
            await NativeModeTeardownOrchestration.StopAndTryReleaseAsync(
                () => renderTeardownBegan = true,
                () => Task.FromException(failure),
                () => true,
                () =>
                {
                    renderTeardownCompleted = true;
                    return Task.CompletedTask;
                },
                () => false,
                () => false,
                () => releases++);

        Assert.IsTrue(renderTeardownBegan);
        Assert.IsTrue(renderTeardownCompleted);
        Assert.IsFalse(result.Released);
        Assert.AreSame(failure, result.Failure);
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task RemovalTimeout_RetainsProtectionWithoutHidingStopSuccess()
    {
        int releases = 0;

        NativeModeTeardownAttempt result =
            await NativeModeTeardownOrchestration.StopAndTryReleaseAsync(
                () => { },
                () => Task.CompletedTask,
                () => false,
                () => Task.CompletedTask,
                () => true,
                () => true,
                () => releases++);

        Assert.IsFalse(result.Released);
        Assert.IsNull(result.Failure);
        StringAssert.Contains(result.DeferredReason, "exact virtual child");
        StringAssert.Contains(result.DeferredReason, "audio endpoint");
        Assert.AreEqual(0, releases);
    }

    [DataTestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, true, true)]
    public void ReleaseRequiresAllThreeIndependentConfirmations(
        bool ownsServer, bool childPresent, bool keepaliveActive)
    {
        int releases = 0;

        NativeModeTeardownAttempt result =
            NativeModeTeardownOrchestration.TryReleaseIfConfirmed(
                () => ownsServer, () => childPresent, () => keepaliveActive,
                () => releases++);

        Assert.IsFalse(result.Released);
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public void ConfirmedRemoval_ReleasesProtectionsExactlyOncePerAttempt()
    {
        int releases = 0;

        NativeModeTeardownAttempt result =
            NativeModeTeardownOrchestration.TryReleaseIfConfirmed(
                () => false, () => false, () => false, () => releases++);

        Assert.IsTrue(result.Released);
        Assert.IsNull(result.Failure);
        Assert.IsNull(result.DeferredReason);
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public void ProbeFailure_IsUnknownAndFailsClosed()
    {
        int releases = 0;

        NativeModeTeardownAttempt result =
            NativeModeTeardownOrchestration.TryReleaseIfConfirmed(
                () => false,
                () => throw new InvalidOperationException("PnP query failed"),
                () => false,
                () => releases++);

        Assert.IsFalse(result.Released);
        Assert.IsNull(result.Failure);
        StringAssert.Contains(result.DeferredReason,
            "could not confirm exact virtual-child removal");
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task DeferredPoll_ReleasesAtFirstConfirmationAndStopsPolling()
    {
        int attempts = 0;
        int delays = 0;

        bool released = await NativeModeTeardownOrchestration
            .WaitForConfirmedReleaseAsync(
                () => Task.FromResult(++attempts == 3),
                (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                },
                maximumAttempts: 5,
                pollInterval: TimeSpan.FromMilliseconds(1),
                CancellationToken.None);

        Assert.IsTrue(released);
        Assert.AreEqual(3, attempts);
        Assert.AreEqual(2, delays);
    }

    [TestMethod]
    public async Task DeferredPoll_StopsAtConfiguredBoundWhenStillUnconfirmed()
    {
        int attempts = 0;
        int delays = 0;

        bool released = await NativeModeTeardownOrchestration
            .WaitForConfirmedReleaseAsync(
                () =>
                {
                    attempts++;
                    return Task.FromResult(false);
                },
                (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                },
                maximumAttempts: 3,
                pollInterval: TimeSpan.FromMilliseconds(1),
                CancellationToken.None);

        Assert.IsFalse(released);
        Assert.AreEqual(3, attempts);
        Assert.AreEqual(2, delays);
    }

    private static NativeModeAudioDefaultsSnapshot CreateSnapshot() => new(
        new Dictionary<
            (NativeModeAudioFlow Flow, NativeModeAudioRole Role), string>(),
        new Dictionary<NativeModeAudioFlow, HashSet<string>>
        {
            [NativeModeAudioFlow.Render] = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase),
            [NativeModeAudioFlow.Capture] = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase),
        });
}
