// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace VirtualDualSenseUsbip.Live;

/// <summary>
/// Raises Windows' process timer resolution while the live server is running.
/// Without this, a 4 ms PeriodicTimer is quantized to roughly 15.6 ms and the
/// virtual DualSense delivers only about 65 reports/s instead of 250 reports/s.
/// </summary>
internal sealed class WindowsTimerResolution : IDisposable
{
    private readonly uint period;
    private readonly bool active;

    private WindowsTimerResolution(uint period, bool active)
    {
        this.period = period;
        this.active = active;
    }

    public static WindowsTimerResolution Begin(uint milliseconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsTimerResolution(milliseconds, active: false);
        }

        uint result = TimeBeginPeriod(milliseconds);
        if (result != 0)
        {
            throw new InvalidOperationException($"timeBeginPeriod({milliseconds}) failed with {result}.");
        }
        return new WindowsTimerResolution(milliseconds, active: true);
    }

    public void Dispose()
    {
        if (active)
        {
            _ = TimeEndPeriod(period);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
