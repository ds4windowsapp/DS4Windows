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

namespace DS4Windows
{
    /// <summary>
    /// Identifies the one physical controller temporarily owned by native mode.
    /// Device-path matching prevents DS4Windows from opening the HID handle just
    /// to rediscover its serial; MAC matching covers already-created devices.
    /// </summary>
    public sealed class NativeModeDeviceGuard
    {
        private readonly object sync = new object();
        private string macAddress;
        private string devicePath;

        public bool IsActive
        {
            get
            {
                lock (sync)
                    return macAddress != null || devicePath != null;
            }
        }

        public string MacAddress
        {
            get
            {
                lock (sync)
                    return macAddress;
            }
        }

        public void Activate(string macAddress, string devicePath)
        {
            string normalizedMac = Normalize(macAddress);
            string normalizedPath = Normalize(devicePath);
            if (normalizedMac == null)
                throw new ArgumentException("A controller MAC address is required.", nameof(macAddress));
            if (normalizedPath == null)
                throw new ArgumentException("A controller HID path is required.", nameof(devicePath));

            lock (sync)
            {
                if (this.macAddress != null || this.devicePath != null)
                    throw new InvalidOperationException("A controller is already reserved for native mode.");

                this.macAddress = normalizedMac;
                this.devicePath = normalizedPath;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                macAddress = null;
                devicePath = null;
            }
        }

        public bool ShouldSuppress(string candidateMacAddress, string candidateDevicePath)
        {
            string normalizedMac = Normalize(candidateMacAddress);
            string normalizedPath = Normalize(candidateDevicePath);
            lock (sync)
            {
                return (macAddress != null && normalizedMac != null &&
                        string.Equals(macAddress, normalizedMac, StringComparison.OrdinalIgnoreCase)) ||
                    (devicePath != null && normalizedPath != null &&
                        string.Equals(devicePath, normalizedPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool ShouldSuppressPath(string candidateDevicePath)
        {
            string normalizedPath = Normalize(candidateDevicePath);
            lock (sync)
            {
                return devicePath != null && normalizedPath != null &&
                    string.Equals(devicePath, normalizedPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
