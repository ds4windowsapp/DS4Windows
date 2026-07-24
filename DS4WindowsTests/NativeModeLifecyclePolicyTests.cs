using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeLifecyclePolicyTests
{
    [TestMethod]
    public void StartupSafety_AllowsConfirmedAbsence()
    {
        NativeModeStartupSafety.EnsureNoExistingVirtualDevice(() => false);
    }

    [TestMethod]
    public void StartupSafety_BlocksPresentEarlierSession()
    {
        InvalidOperationException failure =
            Assert.ThrowsException<InvalidOperationException>(() =>
                NativeModeStartupSafety.EnsureNoExistingVirtualDevice(() => true));

        StringAssert.Contains(failure.Message, "will not attach another");
    }

    [TestMethod]
    public void StartupSafety_BlocksProbeFailure()
    {
        InvalidOperationException failure =
            Assert.ThrowsException<InvalidOperationException>(() =>
                NativeModeStartupSafety.EnsureNoExistingVirtualDevice(
                    () => throw new IOException("SetupAPI failed")));

        Assert.IsInstanceOfType<IOException>(failure.InnerException);
        StringAssert.Contains(failure.Message, "Startup is blocked");
    }

    [DataTestMethod]
    [DataRow(NativeModeState.Starting, false, false, true)]
    [DataRow(NativeModeState.Serving, false, false, true)]
    [DataRow(NativeModeState.Attached, false, false, true)]
    [DataRow(NativeModeState.Stopped, true, false, true)]
    [DataRow(NativeModeState.PadLost, true, false, true)]
    [DataRow(NativeModeState.Faulted, false, true, true)]
    [DataRow(NativeModeState.PadLost, false, false, false)]
    [DataRow(NativeModeState.Faulted, false, false, false)]
    [DataRow(NativeModeState.SetupRequired, false, false, false)]
    [DataRow(NativeModeState.Stopped, false, false, false)]
    public void IsSessionActive_TracksManagerTransitionOrSuppressionOwnership(
        NativeModeState state, bool suppressionActive, bool ownsChildProcess,
        bool expected)
    {
        Assert.AreEqual(expected,
            NativeModeLifecyclePolicy.IsSessionActive(state, suppressionActive,
                ownsChildProcess));
    }

    [DataTestMethod]
    [DataRow(NativeModeState.PadLost, true, true)]
    [DataRow(NativeModeState.Faulted, true, true)]
    [DataRow(NativeModeState.PadLost, false, false)]
    [DataRow(NativeModeState.Faulted, false, false)]
    [DataRow(NativeModeState.Attached, true, false)]
    [DataRow(NativeModeState.Stopped, true, false)]
    public void RequiresAutomaticCleanup_OnlyForOwnedTerminalSessions(
        NativeModeState state, bool suppressionActive, bool expected)
    {
        Assert.AreEqual(expected,
            NativeModeLifecyclePolicy.RequiresAutomaticCleanup(
                state, suppressionActive));
    }

    [TestMethod]
    public void QueuedAutomaticCleanup_RechecksLateCommandBarrierAtExecution()
    {
        bool cleanupWasQueued =
            NativeModeLifecyclePolicy.RequiresAutomaticCleanup(
                NativeModeState.Faulted, suppressionActive: true);

        Assert.IsTrue(cleanupWasQueued);
        Assert.IsFalse(NativeModeLifecyclePolicy.CanBeginTeardown(
            pendingCommandBarrierActive: true));
        Assert.IsTrue(NativeModeLifecyclePolicy.CanBeginTeardown(
            pendingCommandBarrierActive: false));
    }
}
