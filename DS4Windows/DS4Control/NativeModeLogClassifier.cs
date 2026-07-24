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
    public enum NativeModeLogKind
    {
        Other,
        ServerListening,
        RenderKeepaliveReady,
        RenderKeepaliveFailure,
        PadOpenFailure,
        PadLost,
        FatalUsbIpSession,
        IsochronousOutStats,
        AudioStats,
        SpeakerRebuffer,
    }

    /// <summary>
    /// Classifies VirtualDualSenseUsbip console output without owning any
    /// process or application state. Keep marker text synchronized with the
    /// emulator; tests intentionally use complete production-shaped lines.
    /// </summary>
    public static class NativeModeLogClassifier
    {
        public static NativeModeLogKind Classify(string line)
        {
            if (string.IsNullOrEmpty(line))
                return NativeModeLogKind.Other;

            if (line.Contains("The physical pad is gone", StringComparison.Ordinal))
                return NativeModeLogKind.PadLost;

            if (line.Contains("No physical Bluetooth DualSense", StringComparison.Ordinal))
                return NativeModeLogKind.PadOpenFailure;

            if (line.Contains("NativeRenderKeepaliveReady:",
                StringComparison.Ordinal))
            {
                return NativeModeLogKind.RenderKeepaliveReady;
            }

            if (line.Contains("NativeRenderKeepaliveFailed:",
                StringComparison.Ordinal))
            {
                return NativeModeLogKind.RenderKeepaliveFailure;
            }

            if (line.Contains(nameof(NativeModeLogKind.FatalUsbIpSession),
                StringComparison.Ordinal))
            {
                return NativeModeLogKind.FatalUsbIpSession;
            }

            if (line.Contains("USB/IP server listening", StringComparison.Ordinal))
                return NativeModeLogKind.ServerListening;

            if (line.Contains(" ISO OUT ", StringComparison.Ordinal))
                return NativeModeLogKind.IsochronousOutStats;

            if (line.Contains(" AUDIO ", StringComparison.Ordinal))
                return NativeModeLogKind.AudioStats;

            if (line.Contains("Speaker rebuffer #", StringComparison.Ordinal))
                return NativeModeLogKind.SpeakerRebuffer;

            return NativeModeLogKind.Other;
        }
    }

    /// <summary>
    /// Keeps redundant audio summaries out of the WPF log while retaining
    /// them in the manager snapshot. The periodic ISO snapshot is forwarded
    /// because its four-channel levels distinguish speaker-routing failures
    /// from haptic-relay failures without exposing device identifiers.
    /// </summary>
    public static class NativeModeLogPolicy
    {
        public static bool ShouldForwardToGui(NativeModeLogKind kind,
            bool fromStandardError)
        {
            if (kind == NativeModeLogKind.AudioStats)
            {
                return false;
            }

            return fromStandardError || kind == NativeModeLogKind.ServerListening ||
                kind == NativeModeLogKind.RenderKeepaliveReady ||
                kind == NativeModeLogKind.RenderKeepaliveFailure ||
                kind == NativeModeLogKind.PadOpenFailure ||
                kind == NativeModeLogKind.PadLost ||
                kind == NativeModeLogKind.FatalUsbIpSession ||
                kind == NativeModeLogKind.IsochronousOutStats ||
                kind == NativeModeLogKind.SpeakerRebuffer;
        }
    }
}
