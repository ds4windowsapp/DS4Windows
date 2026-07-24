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
using System.Linq;
using System.Text;

namespace DS4Windows
{
    /// <summary>
    /// Non-sensitive environment facts shown in the report header. Passed in so
    /// <see cref="NativeModeDriverReportFormatter"/> stays pure and testable.
    /// </summary>
    public sealed class NativeModeDriverReportContext
    {
        public DateTimeOffset TimestampUtc { get; init; }

        public string AppVersion { get; init; }

        public string OsVersion { get; init; }

        public string ProcessArchitecture { get; init; }

        /// <summary>
        /// Whether the diagnostic ran elevated. Reported because a few driver
        /// reads can be restricted; the command never requests elevation.
        /// </summary>
        public bool Elevated { get; init; }

        /// <summary>
        /// usbip.exe path being checked, already redacted of user paths by
        /// <see cref="NativeModeDriverReportFormatter.RedactUserPath"/>.
        /// </summary>
        public string UsbipExecutablePath { get; init; }

        /// <summary>Display form of the saved report location, or null.</summary>
        public string ReportFilePath { get; init; }
    }

    /// <summary>
    /// Composes the human-readable <c>-validatedriver</c> report from a
    /// <see cref="NativeModeDriverValidationReport"/>. Pure: no OS access, no
    /// I/O, no static state, so the formatting is unit-tested with fabricated
    /// observations.
    ///
    /// The report deliberately prints observed versus expected values for every
    /// component even on success. The gate's OS probes (driver-store INF
    /// resolution, DriverVer parsing, architecture detection, the expected
    /// publisher common name) can only be confirmed against a real install, so a
    /// bare pass/fail would hide a gate that fails closed against a good driver.
    ///
    /// Nothing sensitive is emitted: no device instance paths, serials, radio
    /// addresses, driver-store paths, or user paths.
    /// </summary>
    public static class NativeModeDriverReportFormatter
    {
        private const string Ok = "[OK]";
        private const string Mismatch = "[MISMATCH]";
        private const string Info = "[INFO]";
        private const int LabelWidth = 22;
        private const int ObservedWidth = 34;
        private const string NotReported = "(not reported)";
        private const string NotEvaluated = "(not evaluated)";

        public static string Format(NativeModeDriverValidationReport report,
            NativeModeDriverReportContext context)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var body = new StringBuilder();
            int mismatches = 0;

            AppendHostSection(body, report, ref mismatches);
            AppendFilterSection(body, report, ref mismatches);
            AppendClientSection(body, report, context, ref mismatches);
            AppendFooter(body);

            var text = new StringBuilder();
            AppendHeader(text, report, context, mismatches);
            text.Append(body);
            return text.ToString();
        }

        /// <summary>
        /// Replaces a user account name in a Windows path with a placeholder so
        /// report output carries no user profile paths.
        /// </summary>
        public static string RedactUserPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "(not set)";

            const string usersSegment = @"\Users\";
            int index = path.IndexOf(usersSegment,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return path;

            int nameStart = index + usersSegment.Length;
            int nameEnd = path.IndexOf('\\', nameStart);
            string tail = nameEnd < 0 ? string.Empty : path.Substring(nameEnd);
            return path.Substring(0, nameStart) + "<user>" + tail;
        }

        private static void AppendHeader(StringBuilder text,
            NativeModeDriverValidationReport report,
            NativeModeDriverReportContext context, int mismatches)
        {
            NativeModeDriverRelease expected = report.ExpectedRelease;
            NativeModeDriverValidationResult result = report.Result;

            text.AppendLine("DS4Windows Native Mode driver validation " +
                "(read-only diagnostic)");
            text.AppendLine(new string('=', 62));
            AppendHeaderLine(text, "generated (UTC)",
                context.TimestampUtc.ToUniversalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss'Z'"));
            AppendHeaderLine(text, "DS4Windows version", context.AppVersion);
            AppendHeaderLine(text, "process architecture",
                context.ProcessArchitecture);
            AppendHeaderLine(text, "elevated", YesNo(context.Elevated));
            AppendHeaderLine(text, "operating system", context.OsVersion);
            if (expected != null)
            {
                AppendHeaderLine(text, "expected release",
                    $"usbip-win2 {expected.ReleaseLabel} ({expected.Tier})");
                AppendHeaderLine(text, "signer policy",
                    expected.DriverSignerPolicy.ToString());
            }

            AppendHeaderLine(text, "usbip.exe checked",
                context.UsbipExecutablePath);
            if (!string.IsNullOrWhiteSpace(context.ReportFilePath))
                AppendHeaderLine(text, "report file", context.ReportFilePath);

            text.AppendLine();
            if (result == null)
            {
                text.AppendLine("RESULT: UNKNOWN (no validation result was " +
                    "produced)");
            }
            else if (result.Passed)
            {
                text.AppendLine("RESULT: PASS - Native Mode would be allowed to " +
                    "start on this machine.");
                AppendHeaderLine(text, "matched release",
                    $"{result.ReleaseLabel} ({result.Tier})");
                if (result.RequiresExperimentalConfirmation)
                {
                    AppendHeaderLine(text, "note", "this release is experimental; " +
                        "Native Mode still requires the per-Start confirmation");
                }
            }
            else
            {
                text.AppendLine("RESULT: FAIL - Native Mode would refuse to start " +
                    "on this machine.");
                AppendHeaderLine(text, "failed component",
                    result.FailedComponent.ToString());
                AppendHeaderLine(text, "reason", result.Reason.ToString());
                AppendHeaderLine(text, "diagnostic", result.Diagnostic);
            }

            AppendHeaderLine(text, "observed mismatches",
                mismatches.ToString());
            if (!string.IsNullOrWhiteSpace(report.PackageInspectionError))
            {
                AppendHeaderLine(text, "package read error",
                    report.PackageInspectionError);
            }

            if (!string.IsNullOrWhiteSpace(report.UsbipClientInspectionError))
            {
                AppendHeaderLine(text, "client read error",
                    report.UsbipClientInspectionError);
            }
        }

        private static void AppendHostSection(StringBuilder text,
            NativeModeDriverValidationReport report, ref int mismatches)
        {
            NativeModeDriverPackageSpec spec =
                report.ExpectedRelease?.UdeHostController;
            NativeModeDriverPackageInfo observed = report.HostController;

            AppendSectionHeader(text, "UDE host controller (queried by hardware " +
                "ID " + NativeModeDriverManifest.UdeHostControllerHardwareId + ")");
            if (!AppendPackageIdentity(text, observed, spec, report.ExpectedRelease,
                ref mismatches))
            {
                return;
            }

            AppendComparison(text, "device node present", YesNo(observed.DeviceNodePresent),
                "yes", ref mismatches);
            AppendComparison(text, "started, no problem", YesNo(observed.Started),
                "yes", ref mismatches);
            AppendComparison(text, "driver store target",
                report.HostControllerStoreTargetResolved
                    ? "resolved"
                    : "NOT RESOLVED", "resolved", ref mismatches);
            AppendInfo(text, "service", Text(observed.Service));
            AppendInfo(text, "catalog file", Text(observed.CatalogFile));
            AppendTrust(text, report.HostControllerTrust, requirePublisher: true,
                ref mismatches);
        }

        private static void AppendFilterSection(StringBuilder text,
            NativeModeDriverValidationReport report, ref int mismatches)
        {
            NativeModeDriverPackageSpec spec =
                report.ExpectedRelease?.FilterExtension;
            NativeModeDriverPackageInfo observed = report.FilterExtension;

            AppendSectionHeader(text, "filter extension (located in the driver " +
                "store FileRepository)");
            if (spec != null)
            {
                AppendInfo(text, "store search", @"%SystemRoot%\System32\" +
                    @"DriverStore\FileRepository\" + spec.InfName + "_*");
            }

            if (!AppendPackageIdentity(text, observed, spec, report.ExpectedRelease,
                ref mismatches))
            {
                return;
            }

            AppendComparison(text, "driver store target",
                report.FilterExtensionStoreTargetResolved
                    ? "resolved"
                    : "NOT RESOLVED", "resolved", ref mismatches);
            AppendInfo(text, "catalog file", Text(observed.CatalogFile));
            AppendInfo(text, "device node", "not applicable (extension package; " +
                "the host controller carries presence and health)");
            AppendTrust(text, report.FilterExtensionTrust, requirePublisher: true,
                ref mismatches);
        }

        private static void AppendClientSection(StringBuilder text,
            NativeModeDriverValidationReport report,
            NativeModeDriverReportContext context, ref int mismatches)
        {
            NativeModeUsbipClientSpec spec =
                report.ExpectedRelease?.UserspaceClient;
            NativeModeUsbipClientInfo observed = report.UsbipClient;

            AppendSectionHeader(text, "usbip.exe userspace client");
            AppendInfo(text, "resolved path", Text(context.UsbipExecutablePath));
            bool found = observed != null && observed.Found;
            AppendComparison(text, "file found", YesNo(found), "yes",
                ref mismatches);
            if (!found)
            {
                AppendInfo(text, "note", "nothing further could be read for this " +
                    "component");
                return;
            }

            AppendComparison(text, "file name", Text(observed.FileName),
                Text(spec?.FileName), ref mismatches);
            AppendComparison(text, "ProductVersion",
                Text(observed.ProductVersion?.ToString()),
                Text(spec?.ProductVersion?.ToString()), ref mismatches);
            if (spec == null || spec.RequireAuthenticode)
            {
                AppendTrust(text, report.UsbipClientTrust, requirePublisher: false,
                    ref mismatches);
            }
            else
            {
                AppendInfo(text, "Authenticode", "not required for this release");
            }
        }

        /// <summary>
        /// Emits the shared package identity fields. Returns false when the
        /// package was not found, in which case no further field can be read.
        /// </summary>
        private static bool AppendPackageIdentity(StringBuilder text,
            NativeModeDriverPackageInfo observed, NativeModeDriverPackageSpec spec,
            NativeModeDriverRelease release, ref int mismatches)
        {
            bool found = observed != null && observed.Found;
            AppendComparison(text, "package found", YesNo(found), "yes",
                ref mismatches);
            if (!found)
            {
                AppendInfo(text, "note", "nothing further could be read for this " +
                    "component");
                return false;
            }

            AppendComparison(text, "INF name", Text(observed.InfName),
                Text(spec?.InfName), ref mismatches);
            AppendComparison(text, "provider", Text(observed.Provider),
                Text(spec?.Provider), ref mismatches);
            AppendComparison(text, "DriverVer",
                Text(observed.DriverVersion?.ToString()),
                Text(spec?.DriverVersion?.ToString()), ref mismatches);
            AppendComparison(text, "architecture", observed.Architecture.ToString(),
                DescribeArchitectures(release),
                release != null && release.SupportsArchitecture(observed.Architecture),
                ref mismatches);
            return true;
        }

        private static void AppendTrust(StringBuilder text,
            NativeModeSignatureTrust trust, bool requirePublisher,
            ref int mismatches)
        {
            if (trust == null)
            {
                AppendInfo(text, "signature", NotEvaluated);
                return;
            }

            AppendComparison(text, "signature trusted", YesNo(trust.Trusted), "yes",
                ref mismatches);
            AppendInfo(text, "signature flags", DescribeTrustFlags(trust));
            AppendInfo(text, "trust diagnostic", Text(trust.Diagnostic));

            bool commonNameReported = !string.IsNullOrWhiteSpace(
                trust.ObservedSignerCommonName);
            if (!requirePublisher)
            {
                AppendInfo(text, "signer common name",
                    trust.ObservedSignerCommonName);
                return;
            }

            AppendComparison(text, "publisher accepted",
                YesNo(trust.IsMicrosoftHardwareCompatibilityPublisher), "yes",
                ref mismatches);

            // Only compare a common name that was actually read. An unread name
            // is unknowable rather than wrong, and "publisher accepted" above
            // already carries the decision.
            if (commonNameReported)
            {
                AppendComparison(text, "signer common name",
                    trust.ObservedSignerCommonName,
                    NativeModeDriverManifest
                        .MicrosoftHardwareCompatibilityPublisherCommonName,
                    ref mismatches);
            }
            else
            {
                AppendInfo(text, "signer common name", NotReported);
            }
        }

        private static void AppendSectionHeader(StringBuilder text, string title)
        {
            text.AppendLine();
            text.AppendLine("-- " + title + " " +
                new string('-', Math.Max(3, 62 - title.Length)));
        }

        private static void AppendHeaderLine(StringBuilder text, string label,
            string value)
        {
            text.AppendLine("  " + label.PadRight(LabelWidth) + ": " + Text(value));
        }

        private static void AppendComparison(StringBuilder text, string label,
            string observed, string expected, ref int mismatches)
        {
            AppendComparison(text, label, observed, expected,
                string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase),
                ref mismatches);
        }

        /// <summary>
        /// Comparison line whose verdict is supplied by the caller, for fields
        /// where the expectation is a set rather than one literal.
        /// </summary>
        private static void AppendComparison(StringBuilder text, string label,
            string observed, string expected, bool matches, ref int mismatches)
        {
            if (!matches)
                mismatches++;
            text.AppendLine("  " + label.PadRight(LabelWidth) +
                " observed: " + Text(observed).PadRight(ObservedWidth) +
                " expected: " + Text(expected).PadRight(ObservedWidth) + " " +
                (matches ? Ok : Mismatch));
        }

        private static void AppendInfo(StringBuilder text, string label,
            string observed)
        {
            text.AppendLine("  " + label.PadRight(LabelWidth) +
                " observed: " + Text(observed).PadRight(ObservedWidth) + " " + Info);
        }

        private static void AppendFooter(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("-- notes " + new string('-', 54));
            text.AppendLine("  This command is read-only. It does not release " +
                "the controller, request");
            text.AppendLine("  elevation, attach a device, start a USB/IP server, " +
                "or change any setting.");
            text.AppendLine("  Every [MISMATCH] above is a value the driver " +
                "validation gate refuses. If the");
            text.AppendLine("  usbip-win2 install is known good and a line still " +
                "reads [MISMATCH], the gate's");
            text.AppendLine("  expectation or the way it reads that value is " +
                "wrong; attach this report.");
            text.AppendLine("  No device instance paths, serials, addresses, " +
                "driver store paths, or user");
            text.AppendLine("  paths are included.");
        }

        private static string DescribeTrustFlags(NativeModeSignatureTrust trust)
        {
            string[] flags = new[]
            {
                trust.Revoked ? "revoked" : null,
                trust.Expired ? "expired" : null,
                trust.TestSigned ? "test-signed" : null,
                trust.DeveloperSigned ? "developer-signed" : null,
            }.Where(flag => flag != null).ToArray();
            return flags.Length == 0 ? "none" : string.Join(", ", flags);
        }

        private static string DescribeArchitectures(NativeModeDriverRelease release)
        {
            if (release == null)
                return NotReported;
            return string.Join(" or ",
                release.Architectures.Select(architecture => architecture.ToString()));
        }

        private static string YesNo(bool value) => value ? "yes" : "no";

        private static string Text(string value) =>
            string.IsNullOrWhiteSpace(value) ? NotReported : value;
    }
}
