/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Globalization;

namespace DS4Windows
{
    public readonly struct NativeModeIsoTelemetry
    {
        public NativeModeIsoTelemetry(double channel3RmsPercent,
            double channel4RmsPercent, long bluetoothErrorCount)
        {
            Channel3RmsPercent = channel3RmsPercent;
            Channel4RmsPercent = channel4RmsPercent;
            BluetoothErrorCount = bluetoothErrorCount;
        }

        public double Channel3RmsPercent { get; }
        public double Channel4RmsPercent { get; }
        public long BluetoothErrorCount { get; }

        /// <summary>
        /// True when either native haptic channel has non-zero RMS after the
        /// producer's two-decimal percentage formatting.
        /// </summary>
        public bool HasNativeHapticSignal =>
            Channel3RmsPercent > 0.0 || Channel4RmsPercent > 0.0;
    }

    /// <summary>
    /// Parses the periodic production-shaped ISO OUT telemetry emitted by
    /// VirtualDualSenseUsbip without owning native-mode process or UI state.
    /// </summary>
    public static class NativeModeIsoTelemetryParser
    {
        public static bool TryParse(string line, out NativeModeIsoTelemetry telemetry)
        {
            telemetry = default;
            if (string.IsNullOrWhiteSpace(line) ||
                !line.Contains(" ISO OUT ", StringComparison.Ordinal) ||
                !TryReadToken(line, "rms%=", out string rmsValue) ||
                !TryReadToken(line, "bt-errors=", out string errorValue))
            {
                return false;
            }

            string[] channels = rmsValue.Split('/');
            if (channels.Length != 4 ||
                !TryParseRmsPercent(channels[2], out double channel3) ||
                !TryParseRmsPercent(channels[3], out double channel4) ||
                !long.TryParse(errorValue, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long bluetoothErrors))
            {
                return false;
            }

            telemetry = new NativeModeIsoTelemetry(
                channel3, channel4, bluetoothErrors);
            return true;
        }

        private static bool TryParseRmsPercent(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Float,
                       CultureInfo.InvariantCulture, out result) &&
                   double.IsFinite(result) && result >= 0.0 && result <= 100.0;
        }

        private static bool TryReadToken(string line, string prefix, out string value)
        {
            value = null;
            int prefixIndex = line.IndexOf(prefix, StringComparison.Ordinal);
            if (prefixIndex < 0)
                return false;

            int valueStart = prefixIndex + prefix.Length;
            int valueEnd = line.IndexOf(' ', valueStart);
            if (valueEnd < 0)
                valueEnd = line.Length;

            if (valueEnd == valueStart)
                return false;

            value = line.Substring(valueStart, valueEnd - valueStart);
            return true;
        }
    }
}
