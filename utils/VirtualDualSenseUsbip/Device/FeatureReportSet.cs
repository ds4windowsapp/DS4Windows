// SPDX-License-Identifier: GPL-3.0-or-later

namespace VirtualDualSenseUsbip.Device;

/// <summary>
/// Small authenticated feature-report surface needed during HID bring-up.
/// Report sizes follow Sony's public Linux hid-playstation driver: pairing
/// report 0x09 is 20 bytes and firmware report 0x20 is 64 bytes.
///
/// The pairing report uses a deterministic locally administered virtual MAC,
/// never the user's physical controller address. The firmware body was read
/// from the test controller; the final Bluetooth-only CRC bytes are cleared
/// because the virtual device is presented as wired USB. Optional calibration
/// is supplied from the connected physical pad at runtime and is never stored.
/// </summary>
public sealed class FeatureReportSet
{
    private readonly IReadOnlyDictionary<byte, byte[]> reports;

    private FeatureReportSet(IReadOnlyDictionary<byte, byte[]> reports)
    {
        this.reports = reports;
    }

    public static FeatureReportSet CreateVirtualDefaults(byte[]? calibration = null)
    {
        // ReadSerial interprets bytes 1..6 in reverse order. This produces the
        // stable virtual address 02:54:C0:CE:60:01 (locally administered).
        byte[] pairing = new byte[20];
        pairing[0] = 0x09;
        pairing[1] = 0x01;
        pairing[2] = 0x60;
        pairing[3] = 0xCE;
        pairing[4] = 0xC0;
        pairing[5] = 0x54;
        pairing[6] = 0x02;

        byte[] firmware = Convert.FromHexString(
            "204A756C202034203230323531303A31303A333203000400130300002A00100140" +
            "1900000000000000000000300600002A0001000A000200060000000000000000");

        var reports = new Dictionary<byte, byte[]>
        {
            [0x09] = pairing,
            [0x20] = firmware,
        };

        if (calibration != null)
        {
            if (calibration.Length != 41 || calibration[0] != 0x05)
            {
                throw new ArgumentException(
                    "DualSense calibration must be a 41-byte feature report 0x05.",
                    nameof(calibration));
            }
            reports[0x05] = calibration.ToArray();
        }

        return new FeatureReportSet(reports);
    }

    public ControlResult Get(UsbSetupPacket setup)
    {
        byte reportType = (byte)(setup.Value >> 8);
        byte reportId = (byte)setup.Value;
        const byte FeatureReportType = 3;
        if (reportType != FeatureReportType || !reports.TryGetValue(reportId, out byte[]? report))
        {
            return ControlResult.Stalled();
        }

        int take = Math.Min(report.Length, setup.Length);
        return ControlResult.Ok(report.AsSpan(0, take).ToArray());
    }
}
