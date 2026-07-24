using System;
using System.Text.RegularExpressions;
using DS4Windows;

namespace DS4WindowsTests;

/// <summary>
/// Covers the composition of the read-only <c>-validatedriver</c> report. The
/// report must show observed versus expected values for all three components
/// even on success, because the gate's OS probes have to be confirmed against a
/// real install. All observations here are fabricated; the real SetupAPI and
/// WinVerifyTrust paths are deliberately not exercised.
/// </summary>
[TestClass]
public class NativeModeDriverReportFormatterTests
{
    private const string CanonicalUsbipPath = @"C:\Program Files\USBip\usbip.exe";
    private const string ExpectedPublisher =
        NativeModeDriverManifest.MicrosoftHardwareCompatibilityPublisherCommonName;
    private const string HostTrustPath = @"C:\store\usbip2_ude.cat";
    private const string FilterTrustPath = @"C:\store\usbip2_filter.cat";

    // ---- All pass -------------------------------------------------------

    [TestMethod]
    public void Report_AllPass_ShowsObservedEqualsExpectedForEveryComparison()
    {
        string text = Format(ValidInspector(), ValidVerifier());

        StringAssert.Contains(text, "RESULT: PASS");
        StringAssert.Matches(text, new Regex(@"observed mismatches\s+: 0"));
        foreach (string line in ComparisonLines(text))
        {
            StringAssert.EndsWith(line, "[OK]",
                "Every comparison must match on an all-pass report: " + line);
        }
    }

    [TestMethod]
    public void Report_AllPass_ShowsObservedAndExpectedValuesForAllThreeComponents()
    {
        string text = Format(ValidInspector(), ValidVerifier());

        string host = HostSection(text);
        AssertComparison(host, "INF name", "usbip2_ude.inf", "usbip2_ude.inf");
        AssertComparison(host, "provider", "USBIP-WIN2", "USBIP-WIN2");
        AssertComparison(host, "DriverVer", "1.45.29.368", "1.45.29.368");
        AssertComparison(host, "architecture", "X64", "X64 or X86");
        AssertComparison(host, "device node present", "yes", "yes");
        AssertComparison(host, "started, no problem", "yes", "yes");
        AssertComparison(host, "driver store target", "resolved", "resolved");
        AssertComparison(host, "signature trusted", "yes", "yes");
        AssertComparison(host, "publisher accepted", "yes", "yes");
        AssertComparison(host, "signer common name", ExpectedPublisher,
            ExpectedPublisher);

        string filter = FilterSection(text);
        AssertComparison(filter, "INF name", "usbip2_filter.inf",
            "usbip2_filter.inf");
        AssertComparison(filter, "provider", "USBIP-WIN2", "USBIP-WIN2");
        AssertComparison(filter, "DriverVer", "1.45.28.868", "1.45.28.868");
        AssertComparison(filter, "driver store target", "resolved", "resolved");
        AssertComparison(filter, "publisher accepted", "yes", "yes");

        string client = ClientSection(text);
        StringAssert.Contains(client, CanonicalUsbipPath);
        AssertComparison(client, "file name", "usbip.exe", "usbip.exe");
        AssertComparison(client, "ProductVersion", "0.9.7.8", "0.9.7.8");
        AssertComparison(client, "signature trusted", "yes", "yes");
    }

    [TestMethod]
    public void Report_AllPass_StatesTheExperimentalConfirmationStillApplies()
    {
        string text = Format(ValidInspector(), ValidVerifier());

        StringAssert.Contains(text, "usbip-win2 0.9.7.8 (ExperimentalBaseline)");
        StringAssert.Contains(text, "per-Start confirmation");
    }

    // ---- Version mismatch -----------------------------------------------

    [TestMethod]
    public void Report_VersionMismatch_ShowsBothObservedAndExpectedVersions()
    {
        var inspector = ValidInspector();
        inspector.Host = With(ValidHost(), driverVersion: new Version(1, 45, 29, 999));

        string text = Format(inspector, ValidVerifier());

        StringAssert.Contains(text, "RESULT: FAIL");
        string line = AssertComparison(HostSection(text), "DriverVer",
            "1.45.29.999", "1.45.29.368");
        StringAssert.EndsWith(line, "[MISMATCH]");
        StringAssert.Matches(text, new Regex(@"observed mismatches\s+: 1"));
        // The filter's own version still reads back so a mixed pair is visible.
        AssertComparison(FilterSection(text), "DriverVer", "1.45.28.868",
            "1.45.28.868");
        StringAssert.Contains(text, "MixedPair");
    }

    [TestMethod]
    public void Report_WrongProviderAndInf_ShowsBothObservedAndExpectedValues()
    {
        var inspector = ValidInspector();
        inspector.Host = With(ValidHost(), provider: "Contoso Drivers",
            infName: "oem42.inf");

        string text = Format(inspector, ValidVerifier());

        StringAssert.Contains(text, "RESULT: FAIL");
        StringAssert.EndsWith(AssertComparison(HostSection(text), "INF name",
            "oem42.inf", "usbip2_ude.inf"), "[MISMATCH]");
        StringAssert.EndsWith(AssertComparison(HostSection(text), "provider",
            "Contoso Drivers", "USBIP-WIN2"), "[MISMATCH]");
    }

    [TestMethod]
    public void Report_DriverStoreTargetUnresolved_IsCalledOut()
    {
        // Mirrors SetupGetInfDriverStoreLocationW returning nothing: the INF
        // name and architecture then come from unreliable fallbacks, so the
        // report has to say the store target never resolved.
        var inspector = ValidInspector();
        inspector.Host = With(ValidHost(), trustEvaluationPath: string.Empty);

        string text = Format(inspector, ValidVerifier());

        StringAssert.EndsWith(AssertComparison(HostSection(text),
            "driver store target", "NOT RESOLVED", "resolved"), "[MISMATCH]");
    }

    // ---- Publisher mismatch ---------------------------------------------

    [TestMethod]
    public void Report_PublisherMismatch_IncludesTheObservedCommonName()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust
            {
                Trusted = true,
                IsMicrosoftHardwareCompatibilityPublisher = false,
                ObservedSignerCommonName = "Contoso Attestation Publisher",
                Diagnostic = "trusted",
            },
        };

        string text = Format(ValidInspector(), verifier);

        StringAssert.Contains(text, "RESULT: FAIL");
        StringAssert.Contains(text, "UntrustedSignature");
        StringAssert.Contains(text, "Contoso Attestation Publisher");
        string line = AssertComparison(HostSection(text), "signer common name",
            "Contoso Attestation Publisher", ExpectedPublisher);
        StringAssert.EndsWith(line, "[MISMATCH]");
        StringAssert.EndsWith(AssertComparison(HostSection(text),
            "publisher accepted", "no", "yes"), "[MISMATCH]");
    }

    [TestMethod]
    public void Report_UnreadableCommonName_IsReportedWithoutClaimingAMismatch()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust
            {
                Trusted = true,
                IsMicrosoftHardwareCompatibilityPublisher = true,
                Diagnostic = "trusted",
            },
        };

        string text = Format(ValidInspector(), verifier);

        StringAssert.Contains(text, "RESULT: PASS");
        StringAssert.Contains(HostSection(text),
            "signer common name     observed: (not reported)");
        StringAssert.Matches(text, new Regex(@"observed mismatches\s+: 0"),
            "An unread common name is unknown, not a mismatch.");
    }

    [TestMethod]
    public void Report_UntrustedSignature_ListsTheTrustFlags()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust
            {
                Trusted = false,
                Expired = true,
                Diagnostic = "certificate expired",
                ObservedSignerCommonName = ExpectedPublisher,
            },
        };

        string text = Format(ValidInspector(), verifier);

        StringAssert.Contains(text, "RESULT: FAIL");
        StringAssert.Contains(HostSection(text), "signature flags");
        StringAssert.Contains(HostSection(text), "expired");
        StringAssert.EndsWith(AssertComparison(HostSection(text),
            "signature trusted", "no", "yes"), "[MISMATCH]");
    }

    // ---- Nothing installed ----------------------------------------------

    [TestMethod]
    public void Report_NothingInstalled_IsExplicitForEveryComponent()
    {
        var inspector = new FakeInspector
        {
            Host = new NativeModeDriverPackageInfo { Found = false },
            Filter = new NativeModeDriverPackageInfo { Found = false },
            ClientResolver = _ => new NativeModeUsbipClientInfo { Found = false },
        };

        string text = Format(inspector, ValidVerifier());

        StringAssert.Contains(text, "RESULT: FAIL");
        StringAssert.Contains(text, "NotFound");
        StringAssert.Contains(text, "No usbip-win2 driver packages were found.");
        StringAssert.EndsWith(AssertComparison(HostSection(text), "package found",
            "no", "yes"), "[MISMATCH]");
        StringAssert.EndsWith(AssertComparison(FilterSection(text),
            "package found", "no", "yes"), "[MISMATCH]");
        StringAssert.EndsWith(AssertComparison(ClientSection(text), "file found",
            "no", "yes"), "[MISMATCH]");
        StringAssert.Contains(FilterSection(text),
            @"DriverStore\FileRepository\usbip2_filter.inf_*");
        StringAssert.Matches(text, new Regex(@"observed mismatches\s+: 3"));
        // Nothing is claimed about fields that could not be read at all.
        Assert.AreEqual(3, Occurrences(text, "nothing further could be read"),
            "Each unreadable component must say so explicitly.");
        Assert.IsFalse(HostSection(text).Contains("signature trusted",
            StringComparison.Ordinal),
            "A package that was not found has no signature to report on.");
    }

    [TestMethod]
    public void Report_PackageEnumerationThrew_ReportsTheReadError()
    {
        var inspector = new FakeInspector
        {
            HostResolver = _ => throw new InvalidOperationException("setupapi failure"),
        };

        string text = Format(inspector, ValidVerifier());

        StringAssert.Contains(text, "RESULT: FAIL");
        StringAssert.Contains(text, "InspectionFailed");
        StringAssert.Contains(text, "package read error");
        StringAssert.Contains(text, "setupapi failure");
    }

    // ---- Privacy ---------------------------------------------------------

    [TestMethod]
    public void Report_OmitsPathsAndInstanceIdentity()
    {
        string text = Format(ValidInspector(), ValidVerifier());

        Assert.IsFalse(text.Contains(HostTrustPath, StringComparison.OrdinalIgnoreCase),
            "Driver store / catalog paths must not be surfaced.");
        Assert.IsFalse(text.Contains(FilterTrustPath, StringComparison.OrdinalIgnoreCase),
            "Driver store / catalog paths must not be surfaced.");
        Assert.IsFalse(text.Contains(@"ROOT\USB\0002", StringComparison.OrdinalIgnoreCase),
            "No machine-specific instance path may appear.");
        Assert.IsFalse(text.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase),
            "No user profile path may appear.");
    }

    [DataTestMethod]
    [DataRow(@"C:\Users\somebody\Desktop\usbip\usbip.exe",
        @"C:\Users\<user>\Desktop\usbip\usbip.exe")]
    [DataRow(@"C:\Users\somebody", @"C:\Users\<user>")]
    [DataRow(@"C:\Program Files\USBip\usbip.exe",
        @"C:\Program Files\USBip\usbip.exe")]
    public void RedactUserPath_RemovesTheAccountName(string path, string expected)
    {
        Assert.AreEqual(expected,
            NativeModeDriverReportFormatter.RedactUserPath(path));
    }

    [TestMethod]
    public void RedactUserPath_HandlesMissingPath()
    {
        Assert.AreEqual("(not set)",
            NativeModeDriverReportFormatter.RedactUserPath(null));
    }

    // ---- Header ----------------------------------------------------------

    [TestMethod]
    public void Report_HeaderCarriesEnvironmentAndReportLocation()
    {
        string text = Format(ValidInspector(), ValidVerifier());

        StringAssert.Contains(text, "DS4Windows Native Mode driver validation");
        StringAssert.Contains(text, "1999-12-31 23:59:59Z");
        StringAssert.Contains(text, "3.5.1");
        StringAssert.Contains(text, "X64");
        StringAssert.Contains(text, @"%TEMP%\DS4Windows\report.txt");
        StringAssert.Contains(text, "elevated");
        StringAssert.Contains(text, "read-only");
    }

    [TestMethod]
    public void Format_RequiresReportAndContext()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            NativeModeDriverReportFormatter.Format(null, Context()));
        Assert.ThrowsException<ArgumentNullException>(() =>
            NativeModeDriverReportFormatter.Format(
                new NativeModeDriverValidationReport(), null));
    }

    // ---- Helpers ---------------------------------------------------------

    private static string Format(FakeInspector inspector, FakeVerifier verifier)
    {
        var validator = new NativeModeDriverValidator(
            NativeModeDriverManifest.Supported, inspector, verifier);
        return NativeModeDriverReportFormatter.Format(
            validator.Inspect(CanonicalUsbipPath), Context());
    }

    private static NativeModeDriverReportContext Context() =>
        new NativeModeDriverReportContext
        {
            TimestampUtc = new DateTimeOffset(1999, 12, 31, 23, 59, 59,
                TimeSpan.Zero),
            AppVersion = "3.5.1",
            OsVersion = "Microsoft Windows NT 10.0.99999.0",
            ProcessArchitecture = "X64",
            Elevated = false,
            UsbipExecutablePath = CanonicalUsbipPath,
            ReportFilePath = @"%TEMP%\DS4Windows\report.txt",
        };

    /// <summary>
    /// Asserts one comparison line exists in a section and returns it, so a
    /// caller can also assert its verdict marker.
    /// </summary>
    private static string AssertComparison(string section, string label,
        string observed, string expected)
    {
        foreach (string line in section.Split('\n'))
        {
            string candidate = line.TrimEnd('\r');
            if (!candidate.TrimStart().StartsWith(label + " ",
                StringComparison.Ordinal))
            {
                continue;
            }

            StringAssert.Contains(candidate, "observed: " + observed);
            StringAssert.Contains(candidate, "expected: " + expected);
            return candidate;
        }

        Assert.Fail($"No '{label}' comparison line was found in:{Environment.NewLine}" +
            section);
        return null;
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length,
                StringComparison.Ordinal);
        }

        return count;
    }

    private static string[] ComparisonLines(string text)
    {
        var lines = new System.Collections.Generic.List<string>();
        foreach (string line in text.Split('\n'))
        {
            string candidate = line.TrimEnd('\r');
            if (candidate.Contains(" expected: ", StringComparison.Ordinal))
                lines.Add(candidate);
        }

        Assert.IsTrue(lines.Count > 0, "The report produced no comparison lines.");
        return lines.ToArray();
    }

    private static string HostSection(string text) =>
        Between(text, "-- UDE host controller", "-- filter extension");

    private static string FilterSection(string text) =>
        Between(text, "-- filter extension", "-- usbip.exe");

    private static string ClientSection(string text) =>
        Between(text, "-- usbip.exe", "-- notes");

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(from >= 0, $"Section '{start}' is missing from the report.");
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.IsTrue(to > from, $"Section '{end}' is missing from the report.");
        return text.Substring(from, to - from);
    }

    private static FakeInspector ValidInspector() => new FakeInspector
    {
        Host = ValidHost(),
        Filter = ValidFilter(),
    };

    private static FakeVerifier ValidVerifier() => new FakeVerifier();

    private static NativeModeDriverPackageInfo ValidHost() =>
        new NativeModeDriverPackageInfo
        {
            Found = true,
            HardwareId = NativeModeDriverManifest.UdeHostControllerHardwareId,
            InfName = "usbip2_ude.inf",
            Provider = "USBIP-WIN2",
            DriverVersion = new Version(1, 45, 29, 368),
            Service = "usbip2_ude",
            CatalogFile = "usbip2_ude.cat",
            Architecture = NativeModeDriverArchitecture.X64,
            DeviceNodePresent = true,
            Started = true,
            TrustEvaluationPath = HostTrustPath,
        };

    private static NativeModeDriverPackageInfo ValidFilter() =>
        new NativeModeDriverPackageInfo
        {
            Found = true,
            InfName = "usbip2_filter.inf",
            Provider = "USBIP-WIN2",
            DriverVersion = new Version(1, 45, 28, 868),
            Service = "usbip2_filter",
            CatalogFile = "usbip2_filter.cat",
            Architecture = NativeModeDriverArchitecture.X64,
            DeviceNodePresent = true,
            Started = true,
            TrustEvaluationPath = FilterTrustPath,
        };

    private static NativeModeUsbipClientInfo ValidClient() =>
        new NativeModeUsbipClientInfo
        {
            Found = true,
            FileName = "usbip.exe",
            ProductVersion = new Version(0, 9, 7, 8),
        };

    private static NativeModeDriverPackageInfo With(
        NativeModeDriverPackageInfo source, string provider = null,
        string infName = null, Version driverVersion = null,
        string trustEvaluationPath = null) =>
        new NativeModeDriverPackageInfo
        {
            Found = source.Found,
            HardwareId = source.HardwareId,
            InfName = infName ?? source.InfName,
            Provider = provider ?? source.Provider,
            DriverVersion = driverVersion ?? source.DriverVersion,
            Service = source.Service,
            CatalogFile = source.CatalogFile,
            Architecture = source.Architecture,
            DeviceNodePresent = source.DeviceNodePresent,
            Started = source.Started,
            TrustEvaluationPath = trustEvaluationPath ?? source.TrustEvaluationPath,
        };

    private sealed class FakeInspector : IDriverPackageInspector
    {
        public NativeModeDriverPackageInfo Host { get; set; }
        public NativeModeDriverPackageInfo Filter { get; set; }
        public Func<string, NativeModeDriverPackageInfo> HostResolver { get; set; }
        public Func<string, NativeModeUsbipClientInfo> ClientResolver { get; set; }

        public NativeModeDriverPackageInfo InspectHostController(string hardwareId)
        {
            if (HostResolver != null)
                return HostResolver(hardwareId);
            return Host ?? new NativeModeDriverPackageInfo { Found = false };
        }

        public NativeModeDriverPackageInfo InspectFilterExtension(string infName) =>
            Filter ?? new NativeModeDriverPackageInfo { Found = false };

        public NativeModeUsbipClientInfo InspectUsbipClient(string executablePath)
        {
            if (ClientResolver != null)
                return ClientResolver(executablePath);
            return ValidClient();
        }
    }

    private sealed class FakeVerifier : IAuthenticodeVerifier
    {
        public Func<NativeModeDriverPackageInfo, NativeModeSignatureTrust> DriverTrust
        { get; set; }
        public Func<string, NativeModeSignatureTrust> FileTrust { get; set; }

        public NativeModeSignatureTrust VerifyDriverPackage(
            NativeModeDriverPackageInfo package) =>
            DriverTrust != null
                ? DriverTrust(package)
                : new NativeModeSignatureTrust
                {
                    Trusted = true,
                    IsMicrosoftHardwareCompatibilityPublisher = true,
                    Diagnostic = "trusted",
                    ObservedSignerCommonName = ExpectedPublisher,
                };

        public NativeModeSignatureTrust VerifyFile(string filePath) =>
            FileTrust != null
                ? FileTrust(filePath)
                : new NativeModeSignatureTrust
                {
                    Trusted = true,
                    Diagnostic = "trusted",
                    ObservedSignerCommonName = "usbip-win2 project",
                };
    }
}
