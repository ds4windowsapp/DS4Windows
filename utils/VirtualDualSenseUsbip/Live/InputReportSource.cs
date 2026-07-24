// SPDX-License-Identifier: GPL-3.0-or-later

namespace VirtualDualSenseUsbip.Live;

public interface IInputReportSource
{
    string Description { get; }
    byte[] CreateReport(int requestedLength);
}

internal sealed class NeutralInputReportSource : IInputReportSource
{
    private byte frameCounter;

    public string Description => "synthetic neutral input";

    public byte[] CreateReport(int requestedLength)
    {
        byte[] report = new byte[Math.Clamp(requestedLength, 0, 64)];
        if (report.Length == 0)
        {
            return report;
        }

        report[0] = 0x01;
        for (int i = 1; i <= 4 && i < report.Length; i++)
        {
            report[i] = 0x80; // centered LX, LY, RX, RY
        }
        if (report.Length > 7)
        {
            report[7] = frameCounter++;
        }
        if (report.Length > 8)
        {
            report[8] = 0x08; // neutral d-pad, face buttons released
        }
        return report;
    }
}
