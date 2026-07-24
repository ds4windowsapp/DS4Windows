using System;
using System.Collections.Generic;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeDriverValidatorTests
{
    private const string CanonicalUsbipPath = @"C:\Program Files\USBip\usbip.exe";
    private const string StableHardwareId = @"ROOT\USBIP_WIN2\UDE";

    // ---- Manifest structure --------------------------------------------

    [TestMethod]
    public void Manifest_ExposesSingleExperimentalBaselineRelease()
    {
        NativeModeDriverManifest manifest = NativeModeDriverManifest.Supported;

        Assert.AreEqual(1, manifest.Releases.Count);
        NativeModeDriverRelease release = manifest.Releases[0];
        Assert.AreEqual("0.9.7.8", release.ReleaseLabel);
        Assert.AreEqual(NativeModeDriverTier.ExperimentalBaseline, release.Tier);
        Assert.IsTrue(release.IsRunAllowed,
            "The tested baseline must be allowed to run.");
        Assert.AreEqual(
            NativeModeDriverSignerPolicy.MicrosoftHardwareCompatibilityPublisher,
            release.DriverSignerPolicy);
    }

    [TestMethod]
    public void Manifest_ReleaseLabelDiffersFromComponentDriverVersions()
    {
        NativeModeDriverRelease release = NativeModeDriverManifest.Supported.Releases[0];

        // §3: the upstream release label is not the value Windows reports per
        // package. Keep the three identities distinct.
        Assert.AreEqual("0.9.7.8", release.ReleaseLabel);
        Assert.AreEqual(new Version(1, 45, 29, 368),
            release.UdeHostController.DriverVersion);
        Assert.AreEqual(new Version(1, 45, 28, 868),
            release.FilterExtension.DriverVersion);
        Assert.AreEqual(new Version(0, 9, 7, 8),
            release.UserspaceClient.ProductVersion);
        Assert.AreEqual("usbip2_ude.inf", release.UdeHostController.InfName);
        Assert.AreEqual("usbip2_filter.inf", release.FilterExtension.InfName);
        Assert.AreEqual("USBIP-WIN2", release.UdeHostController.Provider);
        Assert.AreEqual("USBIP-WIN2", release.FilterExtension.Provider);
    }

    [TestMethod]
    public void Manifest_SupportsBothX64AndX86()
    {
        NativeModeDriverRelease release = NativeModeDriverManifest.Supported.Releases[0];

        Assert.IsTrue(release.SupportsArchitecture(NativeModeDriverArchitecture.X64));
        Assert.IsTrue(release.SupportsArchitecture(NativeModeDriverArchitecture.X86));
    }

    // ---- Valid matches --------------------------------------------------

    [DataTestMethod]
    [DataRow(NativeModeDriverArchitecture.X64)]
    [DataRow(NativeModeDriverArchitecture.X86)]
    public void Validate_ValidBaseline_PassesForArchitecture(
        NativeModeDriverArchitecture architecture)
    {
        var inspector = new FakeInspector
        {
            Host = ValidHost(architecture),
            Filter = ValidFilter(architecture),
        };
        NativeModeDriverValidator validator = CreateValidator(inspector,
            new FakeVerifier());

        NativeModeDriverValidationResult result = validator.Validate(CanonicalUsbipPath);

        Assert.IsTrue(result.Passed, result.Diagnostic);
        Assert.AreEqual("0.9.7.8", result.ReleaseLabel);
        Assert.AreEqual(NativeModeDriverTier.ExperimentalBaseline, result.Tier);
        Assert.IsTrue(result.RequiresExperimentalConfirmation,
            "0.9.7.8 must remain gated behind the experimental confirmation.");
    }

    [TestMethod]
    public void Validate_QueriesStableHardwareId_NotInstancePath()
    {
        var inspector = new FakeInspector
        {
            Host = ValidHost(),
            Filter = ValidFilter(),
        };
        CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        Assert.AreEqual(StableHardwareId, inspector.RequestedHostHardwareId);
        Assert.AreEqual("usbip2_filter.inf", inspector.RequestedFilterInf);
    }

    // ---- Hardware ID / presence ----------------------------------------

    [TestMethod]
    public void Validate_HostFoundOnlyUnderChangedRootInstance_FailsNotFound()
    {
        // The device exists on the machine, but only under a machine-specific
        // instance such as ROOT\USB\0002; the stable hardware ID query misses.
        var inspector = new FakeInspector
        {
            HostResolver = hardwareId =>
                string.Equals(hardwareId, @"ROOT\USB\0002",
                    StringComparison.OrdinalIgnoreCase)
                    ? ValidHost()
                    : NotFoundPackage(),
            Filter = ValidFilter(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.NotFound);
    }

    [TestMethod]
    public void Validate_MissingHost_FailsNotFound()
    {
        var inspector = new FakeInspector
        {
            Host = NotFoundPackage(),
            Filter = ValidFilter(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.NotFound);
    }

    [TestMethod]
    public void Validate_MissingFilter_FailsNotFound()
    {
        var inspector = new FakeInspector
        {
            Host = ValidHost(),
            Filter = NotFoundPackage(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.FilterExtension,
            NativeModeDriverFailureReason.NotFound);
    }

    [TestMethod]
    public void Validate_MissingBothPackages_FailsNotFound()
    {
        var inspector = new FakeInspector
        {
            Host = NotFoundPackage(),
            Filter = NotFoundPackage(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.NotFound);
    }

    // ---- Provider / version / mixed pairs ------------------------------

    [TestMethod]
    public void Validate_WrongHostProvider_FailsWrongProvider()
    {
        NativeModeDriverPackageInfo host = ValidHost();
        var inspector = new FakeInspector
        {
            Host = With(host, provider: "Contoso Drivers"),
            Filter = ValidFilter(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.WrongProvider);
    }

    [TestMethod]
    public void Validate_WrongFilterProvider_FailsWrongProvider()
    {
        var inspector = new FakeInspector
        {
            Host = ValidHost(),
            Filter = With(ValidFilter(), provider: "Contoso Drivers"),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.FilterExtension,
            NativeModeDriverFailureReason.WrongProvider);
    }

    [TestMethod]
    public void Validate_MixedVersionPair_FilterFromDifferentRelease_FailsMixedPair()
    {
        var inspector = new FakeInspector
        {
            Host = ValidHost(),
            Filter = With(ValidFilter(), driverVersion: new Version(1, 46, 0, 0)),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.FilterExtension,
            NativeModeDriverFailureReason.MixedPair);
    }

    [TestMethod]
    public void Validate_MixedVersionPair_HostFromDifferentRelease_FailsMixedPair()
    {
        var inspector = new FakeInspector
        {
            Host = With(ValidHost(), driverVersion: new Version(1, 46, 0, 0)),
            Filter = ValidFilter(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.MixedPair);
    }

    [TestMethod]
    public void Validate_BothPackagesUnknownVersion_FailsWrongVersion()
    {
        var inspector = new FakeInspector
        {
            Host = With(ValidHost(), driverVersion: new Version(1, 40, 0, 0)),
            Filter = With(ValidFilter(), driverVersion: new Version(1, 40, 0, 0)),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.WrongVersion);
    }

    // ---- Health ---------------------------------------------------------

    [TestMethod]
    public void Validate_HostPresentButNotStarted_FailsUnhealthy()
    {
        var inspector = new FakeInspector
        {
            Host = With(ValidHost(), started: false),
            Filter = ValidFilter(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.Unhealthy);
    }

    // ---- Signatures (fake verifier) ------------------------------------

    [TestMethod]
    public void Validate_HostUntrustedSignature_Fails()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => NativeModeSignatureTrust.Untrusted("not trusted"),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.UntrustedSignature);
    }

    [TestMethod]
    public void Validate_HostExpiredSignature_Fails()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust { Expired = true },
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.UntrustedSignature);
        StringAssert.Contains(result.Diagnostic, "expired");
    }

    [TestMethod]
    public void Validate_HostRevokedSignature_Fails()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust { Revoked = true },
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.UntrustedSignature);
        StringAssert.Contains(result.Diagnostic, "revoked");
    }

    [TestMethod]
    public void Validate_HostDeveloperSigned_Fails()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust { DeveloperSigned = true },
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.UntrustedSignature);
    }

    [TestMethod]
    public void Validate_HostTestSigned_Fails()
    {
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust { TestSigned = true },
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.UntrustedSignature);
        StringAssert.Contains(result.Diagnostic, "test-signed");
    }

    [TestMethod]
    public void Validate_HostTrustedButNotMicrosoftHwcp_Fails()
    {
        // Trusted under normal chain policy, but NOT the Microsoft Hardware
        // Compatibility Publisher. Must be refused even though the signer chain
        // is trusted (guards against a "contains Microsoft" style acceptance).
        var verifier = new FakeVerifier
        {
            DriverTrust = _ => new NativeModeSignatureTrust
            {
                Trusted = true,
                IsMicrosoftHardwareCompatibilityPublisher = false,
            },
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.UntrustedSignature);
        StringAssert.Contains(result.Diagnostic, "Microsoft Hardware Compatibility Publisher");
    }

    [TestMethod]
    public void Validate_FilterUntrustedSignature_Fails()
    {
        // Host is trusted HWCP; only the filter is untrusted.
        var verifier = new FakeVerifier
        {
            DriverTrust = package =>
                package.InfName == "usbip2_filter.inf"
                    ? NativeModeSignatureTrust.Untrusted("bad")
                    : TrustedHwcp(),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.FilterExtension,
            NativeModeDriverFailureReason.UntrustedSignature);
    }

    // ---- usbip.exe client ----------------------------------------------

    [TestMethod]
    public void Validate_UsbipWrongProductVersion_Fails()
    {
        var inspector = ValidInspector();
        inspector.ClientResolver = _ => new NativeModeUsbipClientInfo
        {
            Found = true,
            FileName = "usbip.exe",
            ProductVersion = new Version(0, 9, 7, 7),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UsbipClient,
            NativeModeDriverFailureReason.WrongVersion);
    }

    [TestMethod]
    public void Validate_UsbipMissing_Fails()
    {
        var inspector = ValidInspector();
        inspector.ClientResolver = _ => new NativeModeUsbipClientInfo { Found = false };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UsbipClient,
            NativeModeDriverFailureReason.NotFound);
    }

    [TestMethod]
    public void Validate_UsbipUntrustedAuthenticode_Fails()
    {
        var verifier = new FakeVerifier
        {
            FileTrust = _ => NativeModeSignatureTrust.Untrusted("no signature"),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(ValidInspector(), verifier).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UsbipClient,
            NativeModeDriverFailureReason.UntrustedSignature);
    }

    [TestMethod]
    public void Validate_UsbipCanonicalPath_Passes_NonCanonicalPath_FailsClosed()
    {
        var inspector = ValidInspector();
        // The real inspector resolves/reads the file; a non-canonical path
        // reads as not-found and fails closed.
        inspector.ClientResolver = path =>
            string.Equals(path, CanonicalUsbipPath, StringComparison.OrdinalIgnoreCase)
                ? ValidClient()
                : new NativeModeUsbipClientInfo { Found = false };
        NativeModeDriverValidator validator =
            CreateValidator(inspector, new FakeVerifier());

        Assert.IsTrue(validator.Validate(CanonicalUsbipPath).Passed);

        NativeModeDriverValidationResult nonCanonical = validator.Validate(
            @"C:\Program Files\USBip\..\USBip\usbip.exe");
        AssertFail(nonCanonical, NativeModeDriverComponent.UsbipClient,
            NativeModeDriverFailureReason.NotFound);
    }

    [TestMethod]
    public void Validate_PassesUsbipPathToInspectorAndVerifier()
    {
        var inspector = ValidInspector();
        var verifier = new FakeVerifier();

        CreateValidator(inspector, verifier).Validate(CanonicalUsbipPath);

        Assert.AreEqual(CanonicalUsbipPath, inspector.ClientPathSeen);
        Assert.AreEqual(CanonicalUsbipPath, verifier.FilePathSeen);
    }

    // ---- Inspection failure fails closed -------------------------------

    [TestMethod]
    public void Validate_InspectorThrows_FailsClosed()
    {
        var inspector = new FakeInspector
        {
            HostResolver = _ => throw new InvalidOperationException("setupapi failure"),
        };

        NativeModeDriverValidationResult result =
            CreateValidator(inspector, new FakeVerifier()).Validate(CanonicalUsbipPath);

        AssertFail(result, NativeModeDriverComponent.UdeHostController,
            NativeModeDriverFailureReason.InspectionFailed);
    }

    // ---- Gate + fail-closed before release / elevation -----------------

    [TestMethod]
    public void Gate_ValidateBeforeElevation_ReturnsNull_WhenValid()
    {
        var gate = new NativeModeDriverGate(
            CreateValidator(ValidInspector(), new FakeVerifier()));

        Assert.IsNull(gate.ValidateBeforeElevation(CanonicalUsbipPath));
    }

    [TestMethod]
    public void Gate_ValidateBeforeElevation_ReturnsFailClosedResult_WhenInvalid()
    {
        var inspector = new FakeInspector
        {
            Host = NotFoundPackage(),
            Filter = NotFoundPackage(),
        };
        var gate = new NativeModeDriverGate(
            CreateValidator(inspector, new FakeVerifier()));

        NativeModeAttachResult result = gate.ValidateBeforeElevation(CanonicalUsbipPath);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.DriverValidationFailed,
            result.FailureKind);
    }

    [TestMethod]
    public void FailClosed_ControllerReleaseAndElevationAreSkipped_OnValidationFailure()
    {
        // Mirrors the ControlService ordering contract: nothing is released or
        // elevated unless validation passes.
        var inspector = new FakeInspector
        {
            Host = NotFoundPackage(),
            Filter = ValidFilter(),
        };
        NativeModeDriverValidator validator =
            CreateValidator(inspector, new FakeVerifier());

        bool controllerReleased = false;
        bool elevationRequested = false;

        NativeModeDriverValidationResult result = validator.Validate(CanonicalUsbipPath);
        if (result.Passed)
        {
            controllerReleased = true;
            elevationRequested = true;
        }

        Assert.IsFalse(result.Passed);
        Assert.IsFalse(controllerReleased,
            "The controller must not be released when validation fails.");
        Assert.IsFalse(elevationRequested,
            "Elevation must not be requested when validation fails.");
    }

    [TestMethod]
    public async Task Broker_FailsClosedBeforeAnyElevation_WhenDriverGateRejects()
    {
        // Wire the real broker to a gate backed by a failing validator: the
        // broker must return before running any (elevated) command.
        var inspector = new FakeInspector
        {
            Host = NotFoundPackage(),
            Filter = NotFoundPackage(),
        };
        var gate = new NativeModeDriverGate(
            CreateValidator(inspector, new FakeVerifier()));
        var runner = new RecordingCommandRunner();
        var broker = new NativeModeElevationBroker(
            fileExists: _ => true,
            trustedProgramFilesRoots: () => new[] { @"C:\Program Files" },
            legacyAttachTaskPresent: () => true,
            isAdministrator: () => false,
            commandRunner: runner,
            virtualDevicePresent: () => true,
            delay: (_, _) => Task.CompletedTask,
            taskSchedulerExecutable: @"C:\Windows\System32\schtasks.exe",
            commandTimeout: null,
            driverGate: gate);

        NativeModeAttachResult result = await broker.RunAttachAsync(CanonicalUsbipPath);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.DriverValidationFailed,
            result.FailureKind);
        Assert.AreEqual(0, runner.Calls,
            "No command (elevated or otherwise) may run once the driver gate rejects.");
    }

    // ---- Helpers --------------------------------------------------------

    private static NativeModeDriverValidator CreateValidator(
        IDriverPackageInspector inspector, IAuthenticodeVerifier verifier) =>
        new NativeModeDriverValidator(NativeModeDriverManifest.Supported,
            inspector, verifier);

    private static FakeInspector ValidInspector() => new FakeInspector
    {
        Host = ValidHost(),
        Filter = ValidFilter(),
    };

    private static NativeModeDriverPackageInfo ValidHost(
        NativeModeDriverArchitecture architecture = NativeModeDriverArchitecture.X64) =>
        new NativeModeDriverPackageInfo
        {
            Found = true,
            HardwareId = StableHardwareId,
            InfName = "usbip2_ude.inf",
            Provider = "USBIP-WIN2",
            DriverVersion = new Version(1, 45, 29, 368),
            Service = "usbip2_ude",
            CatalogFile = "usbip2_ude.cat",
            Architecture = architecture,
            DeviceNodePresent = true,
            Started = true,
            TrustEvaluationPath = @"C:\store\usbip2_ude.cat",
        };

    private static NativeModeDriverPackageInfo ValidFilter(
        NativeModeDriverArchitecture architecture = NativeModeDriverArchitecture.X64) =>
        new NativeModeDriverPackageInfo
        {
            Found = true,
            InfName = "usbip2_filter.inf",
            Provider = "USBIP-WIN2",
            DriverVersion = new Version(1, 45, 28, 868),
            Service = "usbip2_filter",
            CatalogFile = "usbip2_filter.cat",
            Architecture = architecture,
            DeviceNodePresent = true,
            Started = true,
            TrustEvaluationPath = @"C:\store\usbip2_filter.cat",
        };

    private static NativeModeUsbipClientInfo ValidClient() =>
        new NativeModeUsbipClientInfo
        {
            Found = true,
            FileName = "usbip.exe",
            ProductVersion = new Version(0, 9, 7, 8),
        };

    private static NativeModeDriverPackageInfo NotFoundPackage() =>
        new NativeModeDriverPackageInfo { Found = false };

    private static NativeModeSignatureTrust TrustedHwcp() =>
        new NativeModeSignatureTrust
        {
            Trusted = true,
            IsMicrosoftHardwareCompatibilityPublisher = true,
        };

    private static NativeModeDriverPackageInfo With(
        NativeModeDriverPackageInfo source, string provider = null,
        Version driverVersion = null, bool? started = null) =>
        new NativeModeDriverPackageInfo
        {
            Found = source.Found,
            HardwareId = source.HardwareId,
            InfName = source.InfName,
            Provider = provider ?? source.Provider,
            DriverVersion = driverVersion ?? source.DriverVersion,
            Service = source.Service,
            CatalogFile = source.CatalogFile,
            Architecture = source.Architecture,
            DeviceNodePresent = source.DeviceNodePresent,
            Started = started ?? source.Started,
            TrustEvaluationPath = source.TrustEvaluationPath,
        };

    private static void AssertFail(NativeModeDriverValidationResult result,
        NativeModeDriverComponent component, NativeModeDriverFailureReason reason)
    {
        Assert.IsFalse(result.Passed, "Expected validation to fail: " +
            result.Diagnostic);
        Assert.AreEqual(component, result.FailedComponent, result.Diagnostic);
        Assert.AreEqual(reason, result.Reason, result.Diagnostic);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Diagnostic),
            "A specific diagnostic is required.");
        Assert.IsNull(result.Tier);
        Assert.IsNull(result.ReleaseLabel);
    }

    private sealed class FakeInspector : IDriverPackageInspector
    {
        public NativeModeDriverPackageInfo Host { get; set; }
        public NativeModeDriverPackageInfo Filter { get; set; }
        public Func<string, NativeModeDriverPackageInfo> HostResolver { get; set; }
        public Func<string, NativeModeUsbipClientInfo> ClientResolver { get; set; }
        public string RequestedHostHardwareId { get; private set; }
        public string RequestedFilterInf { get; private set; }
        public string ClientPathSeen { get; private set; }

        public NativeModeDriverPackageInfo InspectHostController(string hardwareId)
        {
            RequestedHostHardwareId = hardwareId;
            if (HostResolver != null)
                return HostResolver(hardwareId);
            return Host ?? new NativeModeDriverPackageInfo { Found = false };
        }

        public NativeModeDriverPackageInfo InspectFilterExtension(string infName)
        {
            RequestedFilterInf = infName;
            return Filter ?? new NativeModeDriverPackageInfo { Found = false };
        }

        public NativeModeUsbipClientInfo InspectUsbipClient(string executablePath)
        {
            ClientPathSeen = executablePath;
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
        public string FilePathSeen { get; private set; }

        public NativeModeSignatureTrust VerifyDriverPackage(
            NativeModeDriverPackageInfo package)
        {
            return DriverTrust != null ? DriverTrust(package) : TrustedHwcp();
        }

        public NativeModeSignatureTrust VerifyFile(string filePath)
        {
            FilePathSeen = filePath;
            return FileTrust != null
                ? FileTrust(filePath)
                : new NativeModeSignatureTrust { Trusted = true };
        }
    }

    private sealed class RecordingCommandRunner : INativeModeCommandRunner
    {
        public int Calls { get; private set; }

        public Task<NativeModeCommandResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(
                new NativeModeCommandResult(0, string.Empty, string.Empty));
        }
    }
}
