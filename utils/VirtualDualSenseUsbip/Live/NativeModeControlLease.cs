// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace VirtualDualSenseUsbip.Live;

internal enum NativeModeControlSignal
{
    StopRequested,
    ParentPipeClosed,
    ProtocolViolation,
}

internal sealed record NativeModeControlLeaseResult(
    NativeModeControlSignal Signal,
    string? Detail = null);

/// <summary>
/// Treats redirected standard input as a lifetime lease owned by DS4Windows.
/// A normal stop is the single line "stop"; EOF means the parent disappeared.
/// Either condition starts the same ordered child-side containment path.
/// </summary>
internal static class NativeModeControlLease
{
    internal const string StopCommand = "stop";

    /// <summary>
    /// Starts the redirected-stdin lease on a dedicated worker. Console.In is
    /// a synchronized TextReader whose async read can block synchronously
    /// before returning a task, which must never stall helper startup.
    /// </summary>
    public static Task<NativeModeControlLeaseResult> Start(TextReader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.Factory.StartNew(
                () => WaitAsync(input),
                CancellationToken.None,
                TaskCreationOptions.LongRunning |
                    TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
    }

    public static async Task<NativeModeControlLeaseResult> WaitAsync(
        TextReader input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        while (true)
        {
            string? line = await input.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                return new NativeModeControlLeaseResult(
                    NativeModeControlSignal.ParentPipeClosed);
            }

            if (string.Equals(line.Trim(), StopCommand,
                StringComparison.OrdinalIgnoreCase))
            {
                return new NativeModeControlLeaseResult(
                    NativeModeControlSignal.StopRequested);
            }

            return new NativeModeControlLeaseResult(
                NativeModeControlSignal.ProtocolViolation,
                "The Native Mode control pipe received an unsupported command.");
        }
    }
}

/// <summary>
/// Console pipes are owned by DS4Windows. If that process is hard-killed,
/// logging must become a no-op instead of crashing this containment process
/// with a broken-pipe IOException.
/// </summary>
internal sealed class BrokenPipeTolerantTextWriter : TextWriter
{
    private readonly TextWriter inner;
    private int unavailable;

    public BrokenPipeTolerantTextWriter(TextWriter inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value) =>
        TryWrite(writer => writer.Write(value));

    public override void Write(char[] buffer, int index, int count) =>
        TryWrite(writer => writer.Write(buffer, index, count));

    public override void Write(string? value) =>
        TryWrite(writer => writer.Write(value));

    public override void WriteLine(string? value) =>
        TryWrite(writer => writer.WriteLine(value));

    public override void Flush() =>
        TryWrite(writer => writer.Flush());

    private void TryWrite(Action<TextWriter> action)
    {
        if (Volatile.Read(ref unavailable) != 0)
        {
            return;
        }

        try
        {
            action(inner);
        }
        catch (IOException)
        {
            Interlocked.Exchange(ref unavailable, 1);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref unavailable, 1);
        }
    }
}
