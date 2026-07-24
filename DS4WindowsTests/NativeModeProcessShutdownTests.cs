using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeProcessShutdownTests
{
    [TestMethod]
    public async Task RequestAsync_SendsOneStopAndWaitsForExit()
    {
        int stopCount = 0;
        int waitCount = 0;
        bool exited = false;

        await NativeModeProcessShutdown.RequestAsync(
            () => exited,
            () =>
            {
                stopCount++;
                return Task.CompletedTask;
            },
            () =>
            {
                waitCount++;
                exited = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, stopCount);
        Assert.AreEqual(1, waitCount);
        Assert.IsTrue(exited);
    }

    [TestMethod]
    public async Task RequestAsync_AlreadyExitedDoesNotWriteToPipe()
    {
        int stopCount = 0;

        await NativeModeProcessShutdown.RequestAsync(
            () => true,
            () =>
            {
                stopCount++;
                return Task.CompletedTask;
            },
            () => throw new AssertFailedException("Wait should not run."),
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, stopCount);
    }

    [TestMethod]
    public async Task RequestAsync_TimeoutLeavesHelperOwned()
    {
        var neverExits = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TimeoutException failure =
            await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                NativeModeProcessShutdown.RequestAsync(
                    () => false,
                    () => Task.CompletedTask,
                    async () =>
                    {
                        await neverExits.Task;
                    },
                    TimeSpan.FromMilliseconds(20)));

        StringAssert.Contains(failure.Message, "left running");
    }

    [TestMethod]
    public async Task RequestAsync_BrokenControlPipeFailsClosed()
    {
        IOException failure =
            await Assert.ThrowsExceptionAsync<IOException>(() =>
                NativeModeProcessShutdown.RequestAsync(
                    () => false,
                    () => throw new IOException("broken pipe"),
                    () => Task.CompletedTask,
                    TimeSpan.FromSeconds(1)));

        StringAssert.Contains(failure.Message, "left running");
    }
}
