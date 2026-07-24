using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeHapticsStatusTests
{
    [TestMethod]
    public void AttachedStatus_DistinguishesWaitingFromDetectedHaptics()
    {
        string waiting = DualSenseControllerOptionsWrapper.StatusForAttachedHaptics(
            false, 0.0, 0.0, 0);
        string detected = DualSenseControllerOptionsWrapper.StatusForAttachedHaptics(
            true, 15.9, 8.25, 0);

        StringAssert.Contains(waiting, "waiting for native haptics");
        StringAssert.Contains(waiting, "channels 3/4 silent");
        StringAssert.Contains(waiting, "restart the game");
        StringAssert.Contains(detected, "native haptics detected");
        StringAssert.Contains(detected, "L 15.90%");
        StringAssert.Contains(detected, "R 8.25%");
        StringAssert.Contains(detected, "restart the game after attachment");
        StringAssert.Contains(detected, "BT errors 0");
    }

    [TestMethod]
    public void ProcessIsoTelemetry_RaisesStatsChangedAndForwardsPeriodicSnapshot()
    {
        const string line =
            "14:10:22.419 ISO OUT total=500 rms%=0.00/0.00/15.90/15.90 " +
            "peak%=0.0/0.0/35.0/35.0 bt-errors=0";
        var manager = new NativeModeManager();
        int changes = 0;
        manager.StatsChanged += (_, _) => changes++;

        bool forward = manager.ProcessLogLine(line, warning: false);

        Assert.IsTrue(forward);
        Assert.AreEqual(1, changes);
        Assert.AreEqual(line, manager.LatestStats.IsochronousOut);
    }
}
