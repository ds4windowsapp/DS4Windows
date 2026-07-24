using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeSessionCancellationTests
{
    [TestMethod]
    public void StopCancelsRegisteredStartupBeforeLifecycleGateWait()
    {
        using var owner = new NativeModeSessionCancellation();
        CancellationTokenSource session = owner.Begin(CancellationToken.None);
        CancellationToken token = session.Token;

        owner.RequestStop();

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.Begin(CancellationToken.None));

        owner.CompleteSession(session);
        owner.CompleteStop(allowFutureStarts: true);
        CancellationTokenSource next = owner.Begin(CancellationToken.None);
        owner.CompleteSession(next);
    }

    [TestMethod]
    public void StopRegisteredFirstRejectsRacingStartup()
    {
        using var owner = new NativeModeSessionCancellation();

        owner.RequestStop();

        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.Begin(CancellationToken.None));
        owner.CompleteStop(allowFutureStarts: true);
        CancellationTokenSource session = owner.Begin(CancellationToken.None);
        owner.CompleteSession(session);
    }

    [TestMethod]
    public void CallerCancellationFlowsIntoSessionToken()
    {
        using var caller = new CancellationTokenSource();
        using var owner = new NativeModeSessionCancellation();
        CancellationTokenSource session = owner.Begin(caller.Token);
        CancellationToken sessionToken = session.Token;

        caller.Cancel();

        Assert.IsTrue(sessionToken.IsCancellationRequested);
        owner.CompleteSession(session);
    }

    [TestMethod]
    public void ShutdownStopBarrierIsNotCleared()
    {
        using var owner = new NativeModeSessionCancellation();

        owner.RequestStop();
        owner.CompleteStop(allowFutureStarts: false);

        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.Begin(CancellationToken.None));
        owner.ResetForServiceStart();
        CancellationTokenSource session = owner.Begin(CancellationToken.None);
        owner.CompleteSession(session);
    }

    [TestMethod]
    public void ServiceRestartDoesNotBypassDeferredSessionBarrier()
    {
        using var owner = new NativeModeSessionCancellation();
        CancellationTokenSource session = owner.Begin(CancellationToken.None);

        owner.RequestStop();
        owner.CompleteStop(allowFutureStarts: false);
        owner.ResetForServiceStart();

        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.Begin(CancellationToken.None));
        owner.CompleteSession(session);
        owner.ResetForServiceStart();
        CancellationTokenSource next = owner.Begin(CancellationToken.None);
        owner.CompleteSession(next);
    }
}
