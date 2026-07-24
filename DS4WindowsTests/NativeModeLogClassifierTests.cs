using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeLogClassifierTests
{
    [DataTestMethod]
    [DataRow("USB/IP server listening on 127.0.0.1:3240; busid 1-1; input Bluetooth DualSense.",
        NativeModeLogKind.ServerListening)]
    [DataRow("No physical Bluetooth DualSense with streaming reports was found.",
        NativeModeLogKind.PadOpenFailure)]
    [DataRow("NativeRenderKeepaliveReady: active.",
        NativeModeLogKind.RenderKeepaliveReady)]
    [DataRow("NativeRenderKeepaliveFailed: helper render pin could not be maintained.",
        NativeModeLogKind.RenderKeepaliveFailure)]
    [DataRow("Bluetooth input stopped after 750 valid reports (Win32 error 1167). The physical pad is gone; restart serve after it reconnects.",
        NativeModeLogKind.PadLost)]
    [DataRow("FatalUsbIpSession: ISO OUT quiesce watchdog found 2 unlinked transfers.",
        NativeModeLogKind.FatalUsbIpSession)]
    [DataRow("14:10:22.419 ISO OUT total=500 seq=28610 ep=1 urbs/s=98.7 KiB/s=370.1 packets=5000 current=10x384..384 gap-ms=9.1..17.0 rms%=0.00/0.00/15.90/15.90 peak%=0.0/0.0/35.0/35.0 start=5000 interval=1 bt36=461 btq=0 bt-underrun=90 bt-errors=0 bt-audio=420 spkq=5 spk-underrun=0",
        NativeModeLogKind.IsochronousOutStats)]
    [DataRow("14:10:25.000 AUDIO bt-audio=420 spkq=5 spk-underrun=0 spk-dropped=0 mic-rep=0 mic-rate=measuring micq=0 mic-underrun=0 mic-KiB=0.0 mic-decode-err=0 bt-errors=0",
        NativeModeLogKind.AudioStats)]
    [DataRow("Speaker rebuffer #3: ring=3840 samples, iso-age=17.2 ms, since-last=1010.5 ms.",
        NativeModeLogKind.SpeakerRebuffer)]
    public void Classify_RecognizesRealServerMarkers(string line, NativeModeLogKind expected)
    {
        Assert.AreEqual(expected, NativeModeLogClassifier.Classify(line));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Virtual DualSense (composite configuration) is ready for usbip-win2.")]
    [DataRow("19:09:52.504 HID OUT total=1 seq=28614 via=interrupt-out bytes=48 020D1700")]
    public void Classify_IgnoresUnrelatedLines(string line)
    {
        Assert.AreEqual(NativeModeLogKind.Other, NativeModeLogClassifier.Classify(line));
    }

    [DataTestMethod]
    [DataRow(NativeModeLogKind.IsochronousOutStats, false, true)]
    [DataRow(NativeModeLogKind.IsochronousOutStats, true, true)]
    [DataRow(NativeModeLogKind.AudioStats, false, false)]
    [DataRow(NativeModeLogKind.ServerListening, false, true)]
    [DataRow(NativeModeLogKind.RenderKeepaliveReady, false, true)]
    [DataRow(NativeModeLogKind.RenderKeepaliveFailure, false, true)]
    [DataRow(NativeModeLogKind.PadLost, false, true)]
    [DataRow(NativeModeLogKind.FatalUsbIpSession, false, true)]
    [DataRow(NativeModeLogKind.SpeakerRebuffer, false, true)]
    [DataRow(NativeModeLogKind.Other, false, false)]
    [DataRow(NativeModeLogKind.Other, true, true)]
    public void GuiPolicy_ForwardsOnlyStateAndErrorLines(
        NativeModeLogKind kind, bool standardError, bool expected)
    {
        Assert.AreEqual(expected,
            NativeModeLogPolicy.ShouldForwardToGui(kind, standardError));
    }

    [TestMethod]
    public void ProcessLogLine_ForwardsPeriodicIsoStatsAndUpdatesTelemetry()
    {
        const string line =
            "14:10:22.419 ISO OUT total=500 seq=28610 ep=1 urbs/s=98.7 KiB/s=370.1 packets=5000 current=10x384..384 gap-ms=9.1..17.0 rms%=0.00/0.00/15.90/15.90 peak%=0.0/0.0/35.0/35.0 start=5000 interval=1 bt36=461 btq=0 bt-underrun=90 bt-errors=0 bt-audio=420 spkq=5 spk-underrun=0";
        var manager = new NativeModeManager();

        bool forward = manager.ProcessLogLine(line, warning: false);

        Assert.IsTrue(forward);
        Assert.AreEqual(line, manager.LatestStats.IsochronousOut);
    }

    [TestMethod]
    public void ProcessLogLine_FatalSessionMarkerFaultsServingManager()
    {
        var manager = new NativeModeManager();
        NativeModeStateChangedEventArgs lastState = null;
        manager.StateChanged += (_, e) => lastState = e;
        manager.ProcessLogLine(
            "USB/IP server listening on 127.0.0.1:3240; busid 1-1.",
            warning: false);

        const string fatal =
            "FatalUsbIpSession: ISO OUT quiesce watchdog found 2 unlinked transfers.";
        bool forward = manager.ProcessLogLine(fatal, warning: false);

        Assert.IsTrue(forward);
        Assert.AreEqual(NativeModeState.Faulted, manager.State);
        Assert.IsNotNull(lastState);
        Assert.AreEqual(NativeModeState.Faulted, lastState.State);
        Assert.AreEqual(fatal, lastState.Detail);
    }

    [TestMethod]
    public async Task ProcessLogLine_RenderKeepaliveMarkerCompletesReadiness()
    {
        var manager = new NativeModeManager();
        Task wait = manager.WaitForRenderKeepaliveAsync(TimeSpan.FromSeconds(1));

        bool forward = manager.ProcessLogLine(
            "NativeRenderKeepaliveReady: active.", warning: false);

        await wait;
        Assert.IsTrue(forward);
    }

    [TestMethod]
    public async Task ProcessLogLine_RenderKeepaliveFailureFaultsReadiness()
    {
        var manager = new NativeModeManager();
        Task wait = manager.WaitForRenderKeepaliveAsync(TimeSpan.FromSeconds(1));

        manager.ProcessLogLine(
            "NativeRenderKeepaliveFailed: helper render pin could not be maintained.",
            warning: false);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await wait);
        Assert.AreEqual(NativeModeState.Faulted, manager.State);
    }

    [DataTestMethod]
    [DataRow(NativeModeState.PadLost)]
    [DataRow(NativeModeState.SetupRequired)]
    [DataRow(NativeModeState.Faulted)]
    public async Task RenderKeepaliveWait_FailsOnEveryFaultTerminalState(
        NativeModeState terminalState)
    {
        var manager = new NativeModeManager();
        Task wait = manager.WaitForRenderKeepaliveAsync(
            TimeSpan.FromSeconds(5));

        switch (terminalState)
        {
            case NativeModeState.PadLost:
                manager.MarkPadLost("pad lost");
                break;
            case NativeModeState.SetupRequired:
                manager.MarkSetupRequired("setup required");
                break;
            default:
                manager.MarkFaulted("session faulted");
                break;
        }

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await wait);
    }

    [TestMethod]
    public async Task RenderKeepaliveWait_CancelsOnSuccessfulStop()
    {
        var manager = new NativeModeManager();
        Task wait = manager.WaitForRenderKeepaliveAsync(
            TimeSpan.FromSeconds(5));

        await manager.StopAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () => await wait);
    }

    [TestMethod]
    public async Task RenderReadiness_RejectsMarkersFromEarlierGeneration()
    {
        var readiness = new NativeModeRenderReadiness();
        long firstGeneration = readiness.BeginSession();
        Task firstWait = readiness.GetCurrentTask();
        long secondGeneration = readiness.BeginSession();
        Task secondWait = readiness.GetCurrentTask();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () => await firstWait);
        bool staleActionRan = false;
        Assert.IsFalse(readiness.TryRunForCurrent(firstGeneration,
            () => staleActionRan = true));
        Assert.IsFalse(staleActionRan);
        Assert.IsFalse(readiness.TrySetReady(firstGeneration));
        Assert.IsFalse(readiness.TrySetFailure(firstGeneration,
            new InvalidOperationException("stale helper failure")));
        Assert.IsFalse(secondWait.IsCompleted);

        Assert.IsTrue(readiness.TrySetReady(secondGeneration));
        await secondWait;
    }
}
