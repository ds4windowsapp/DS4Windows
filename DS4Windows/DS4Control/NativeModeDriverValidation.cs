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
    /// The component that failed validation. Non-sensitive; safe for logs.
    /// </summary>
    public enum NativeModeDriverComponent
    {
        None,
        UdeHostController,
        FilterExtension,
        UsbipClient,
    }

    /// <summary>
    /// Why a component failed. Non-sensitive; safe for logs.
    /// </summary>
    public enum NativeModeDriverFailureReason
    {
        None,
        NotFound,
        WrongProvider,
        WrongInf,
        WrongVersion,
        WrongArchitecture,
        MixedPair,
        Unhealthy,
        UntrustedSignature,
        InspectionFailed,
    }

    /// <summary>
    /// Read-only view of one installed driver package, produced by an
    /// <see cref="IDriverPackageInspector"/>. Kept free of device instance
    /// paths, serials, or user paths so results can be logged.
    /// </summary>
    public sealed class NativeModeDriverPackageInfo
    {
        public bool Found { get; init; }

        /// <summary>The hardware ID the package was located by (host controller).</summary>
        public string HardwareId { get; init; }

        /// <summary>Original INF name resolved from the driver store, e.g. usbip2_ude.inf.</summary>
        public string InfName { get; init; }

        public string Provider { get; init; }

        public Version DriverVersion { get; init; }

        public string Service { get; init; }

        /// <summary>Catalog identity (file name) for diagnostics; not identity.</summary>
        public string CatalogFile { get; init; }

        public NativeModeDriverArchitecture Architecture { get; init; }

        /// <summary>True when a matching device node is present.</summary>
        public bool DeviceNodePresent { get; init; }

        /// <summary>True when the device node is started with no problem code.</summary>
        public bool Started { get; init; }

        /// <summary>
        /// Path the trust verifier should evaluate (the driver-store INF or its
        /// catalog). Never surfaced in diagnostics.
        /// </summary>
        public string TrustEvaluationPath { get; init; }
    }

    /// <summary>
    /// Read-only view of the userspace usbip.exe client.
    /// </summary>
    public sealed class NativeModeUsbipClientInfo
    {
        public bool Found { get; init; }
        public string FileName { get; init; }
        public Version ProductVersion { get; init; }
    }

    /// <summary>
    /// Outcome of a trust evaluation performed with the Windows trust APIs and
    /// normal chain policy. All flags are derived from the trust chain, not
    /// from a substring match on any signer string.
    /// </summary>
    public sealed class NativeModeSignatureTrust
    {
        /// <summary>WinVerifyTrust succeeded under normal chain policy.</summary>
        public bool Trusted { get; init; }
        public bool Revoked { get; init; }
        public bool Expired { get; init; }
        public bool TestSigned { get; init; }

        /// <summary>Self-signed or not chained to a trusted root.</summary>
        public bool DeveloperSigned { get; init; }

        /// <summary>
        /// The signing certificate obtained from the verified chain is the
        /// Microsoft Windows Hardware Compatibility Publisher.
        /// </summary>
        public bool IsMicrosoftHardwareCompatibilityPublisher { get; init; }

        /// <summary>Short, non-sensitive diagnostic (e.g. an error mnemonic).</summary>
        public string Diagnostic { get; init; }

        /// <summary>
        /// Common name of the signing certificate found on the chain, when one
        /// could be read. Diagnostic only: it is never part of the pass/fail
        /// decision, which uses
        /// <see cref="IsMicrosoftHardwareCompatibilityPublisher"/>. Reported so
        /// a wrong expected common name is visible instead of silently failing
        /// closed against a good install.
        /// </summary>
        public string ObservedSignerCommonName { get; init; }

        public static NativeModeSignatureTrust Untrusted(string diagnostic,
            string observedSignerCommonName = null) =>
            new NativeModeSignatureTrust
            {
                Trusted = false,
                Diagnostic = diagnostic,
                ObservedSignerCommonName = observedSignerCommonName,
            };
    }

    /// <summary>
    /// Enumerates and reads driver-package and userspace-client identity via
    /// SetupAPI / Configuration Manager. The interface exists so the
    /// manifest-matching and fail-closed decision logic is unit-testable with
    /// no driver installed (policy §4.3, §7).
    /// </summary>
    public interface IDriverPackageInspector
    {
        /// <summary>
        /// Locate the present emulated UDE host controller by hardware ID and
        /// read its bound INF, provider, DriverVer, service, catalog, arch, and
        /// health. Never located by a machine-specific instance path.
        /// </summary>
        NativeModeDriverPackageInfo InspectHostController(string hardwareId);

        /// <summary>
        /// Locate the companion filter extension package by original INF name
        /// as a separate component.
        /// </summary>
        NativeModeDriverPackageInfo InspectFilterExtension(string infName);

        /// <summary>
        /// Read the userspace usbip.exe file identity (file name and product
        /// version) at an already path-validated location.
        /// </summary>
        NativeModeUsbipClientInfo InspectUsbipClient(string executablePath);
    }

    /// <summary>
    /// Verifies Authenticode / catalog trust via the Windows trust APIs. Behind
    /// an interface so the decision logic can be exercised with fabricated
    /// trust results (valid, expired, revoked, developer, test).
    /// </summary>
    public interface IAuthenticodeVerifier
    {
        /// <summary>Verify a driver package (catalog-backed) under normal chain policy.</summary>
        NativeModeSignatureTrust VerifyDriverPackage(NativeModeDriverPackageInfo package);

        /// <summary>Verify a stand-alone signed file such as usbip.exe.</summary>
        NativeModeSignatureTrust VerifyFile(string filePath);
    }

    /// <summary>
    /// Fail-closed result of a Native Mode driver validation pass.
    /// Diagnostics are non-sensitive by construction.
    /// </summary>
    public sealed class NativeModeDriverValidationResult
    {
        private NativeModeDriverValidationResult(bool passed,
            NativeModeDriverComponent failedComponent,
            NativeModeDriverFailureReason reason, string diagnostic,
            string releaseLabel, NativeModeDriverTier? tier)
        {
            Passed = passed;
            FailedComponent = failedComponent;
            Reason = reason;
            Diagnostic = diagnostic;
            ReleaseLabel = releaseLabel;
            Tier = tier;
        }

        public bool Passed { get; }
        public NativeModeDriverComponent FailedComponent { get; }
        public NativeModeDriverFailureReason Reason { get; }
        public string Diagnostic { get; }

        /// <summary>Matched release label on success; otherwise null.</summary>
        public string ReleaseLabel { get; }

        /// <summary>Matched tier on success; otherwise null.</summary>
        public NativeModeDriverTier? Tier { get; }

        /// <summary>
        /// True when the matched release is only allowed as an experimental
        /// baseline, so the caller must keep the existing warning and per-Start
        /// confirmation.
        /// </summary>
        public bool RequiresExperimentalConfirmation =>
            Passed && Tier == NativeModeDriverTier.ExperimentalBaseline;

        public static NativeModeDriverValidationResult Pass(string releaseLabel,
            NativeModeDriverTier tier) =>
            new NativeModeDriverValidationResult(true,
                NativeModeDriverComponent.None,
                NativeModeDriverFailureReason.None,
                $"Validated usbip-win2 release {releaseLabel} ({tier}).",
                releaseLabel, tier);

        public static NativeModeDriverValidationResult Fail(
            NativeModeDriverComponent component,
            NativeModeDriverFailureReason reason, string diagnostic) =>
            new NativeModeDriverValidationResult(false, component, reason,
                diagnostic, null, null);
    }

    /// <summary>
    /// Every observation a validation pass consumed, paired with the
    /// authoritative <see cref="NativeModeDriverValidationResult"/>. Purely
    /// diagnostic: nothing here participates in the fail-closed decision, which
    /// is still produced by <see cref="NativeModeDriverValidator.Validate"/>.
    /// Exists so a tester can see observed-vs-expected values on real hardware
    /// instead of only a pass/fail verdict. Non-sensitive by construction: no
    /// instance paths, serials, addresses, or user paths.
    /// </summary>
    public sealed class NativeModeDriverValidationReport
    {
        /// <summary>The authoritative fail-closed outcome.</summary>
        public NativeModeDriverValidationResult Result { get; init; }

        /// <summary>Release the observations are described against.</summary>
        public NativeModeDriverRelease ExpectedRelease { get; init; }

        public NativeModeDriverPackageInfo HostController { get; init; }

        public NativeModeDriverPackageInfo FilterExtension { get; init; }

        public NativeModeUsbipClientInfo UsbipClient { get; init; }

        public NativeModeSignatureTrust HostControllerTrust { get; init; }

        public NativeModeSignatureTrust FilterExtensionTrust { get; init; }

        public NativeModeSignatureTrust UsbipClientTrust { get; init; }

        /// <summary>
        /// Message from an inspection that threw while enumerating driver
        /// packages; null when enumeration completed.
        /// </summary>
        public string PackageInspectionError { get; init; }

        /// <summary>
        /// Message from an inspection that threw while reading the userspace
        /// client; null when the read completed.
        /// </summary>
        public string UsbipClientInspectionError { get; init; }

        /// <summary>
        /// True when a driver-store target could be resolved for the host
        /// controller. False means SetupGetInfDriverStoreLocation (or the INF
        /// read) produced nothing, which makes the reported INF name and
        /// architecture unreliable. The path itself is never surfaced.
        /// </summary>
        public bool HostControllerStoreTargetResolved { get; init; }

        /// <summary>
        /// True when a driver-store target could be resolved for the filter
        /// extension package.
        /// </summary>
        public bool FilterExtensionStoreTargetResolved { get; init; }
    }

    /// <summary>
    /// Pure manifest-matching and fail-closed decision logic. All OS access is
    /// delegated to <see cref="IDriverPackageInspector"/> and
    /// <see cref="IAuthenticodeVerifier"/> so this class is fully unit-testable.
    /// </summary>
    public sealed class NativeModeDriverValidator
    {
        private readonly NativeModeDriverManifest manifest;
        private readonly IDriverPackageInspector inspector;
        private readonly IAuthenticodeVerifier verifier;

        public NativeModeDriverValidator(NativeModeDriverManifest manifest,
            IDriverPackageInspector inspector, IAuthenticodeVerifier verifier)
        {
            this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
            this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        }

        public NativeModeDriverValidationResult Validate(string usbipExecutablePath)
        {
            NativeModeDriverPackageInfo host;
            NativeModeDriverPackageInfo filter;
            try
            {
                host = inspector.InspectHostController(
                    NativeModeDriverManifest.UdeHostControllerHardwareId);
                filter = inspector.InspectFilterExtension(
                    manifest.ReferenceRelease.FilterExtension.InfName);
            }
            catch (Exception ex)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UdeHostController,
                    NativeModeDriverFailureReason.InspectionFailed,
                    "Native Mode could not enumerate usbip-win2 driver " +
                    $"packages: {ex.Message}");
            }

            // §4.2: both packages must match one supported release entry.
            NativeModeDriverRelease matched = FindMatchingRelease(host, filter);
            if (matched == null)
            {
                return DescribeIdentityFailure(host, filter,
                    manifest.ReferenceRelease);
            }

            // §4.1: the host controller must be present, started, and healthy.
            if (!host.DeviceNodePresent || !host.Started)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UdeHostController,
                    NativeModeDriverFailureReason.Unhealthy,
                    "The usbip-win2 UDE host controller is present but not " +
                    "started/healthy. Native Mode is blocked until the driver " +
                    "reports a started, problem-free state.");
            }

            NativeModeDriverValidationResult hostTrust = ValidateDriverTrust(host,
                NativeModeDriverComponent.UdeHostController,
                matched.DriverSignerPolicy);
            if (hostTrust != null)
                return hostTrust;

            NativeModeDriverValidationResult filterTrust = ValidateDriverTrust(filter,
                NativeModeDriverComponent.FilterExtension,
                matched.DriverSignerPolicy);
            if (filterTrust != null)
                return filterTrust;

            NativeModeDriverValidationResult clientResult =
                ValidateUsbipClient(usbipExecutablePath, matched.UserspaceClient);
            if (clientResult != null)
                return clientResult;

            return NativeModeDriverValidationResult.Pass(matched.ReleaseLabel,
                matched.Tier);
        }

        /// <summary>
        /// Read-only diagnostic pass. Gathers the same observations
        /// <see cref="Validate"/> consumes and pairs them with the authoritative
        /// <see cref="Validate"/> outcome, so a report can show observed versus
        /// expected values for every component even when validation passes.
        /// Additive only: the fail-closed decision is unchanged, and every
        /// observation is gathered defensively so a throwing inspector still
        /// yields a report.
        /// </summary>
        public NativeModeDriverValidationReport Inspect(string usbipExecutablePath)
        {
            NativeModeDriverPackageInfo host = null;
            NativeModeDriverPackageInfo filter = null;
            string packageError = null;
            try
            {
                host = inspector.InspectHostController(
                    NativeModeDriverManifest.UdeHostControllerHardwareId);
                filter = inspector.InspectFilterExtension(
                    manifest.ReferenceRelease.FilterExtension.InfName);
            }
            catch (Exception ex)
            {
                packageError = ex.Message;
            }

            NativeModeUsbipClientInfo client = null;
            string clientError = null;
            try
            {
                client = inspector.InspectUsbipClient(usbipExecutablePath);
            }
            catch (Exception ex)
            {
                clientError = ex.Message;
            }

            return new NativeModeDriverValidationReport
            {
                Result = Validate(usbipExecutablePath),
                ExpectedRelease = manifest.ReferenceRelease,
                HostController = host,
                FilterExtension = filter,
                UsbipClient = client,
                HostControllerTrust = InspectPackageTrust(host),
                FilterExtensionTrust = InspectPackageTrust(filter),
                UsbipClientTrust = InspectFileTrust(usbipExecutablePath, client),
                PackageInspectionError = packageError,
                UsbipClientInspectionError = clientError,
                HostControllerStoreTargetResolved =
                    !string.IsNullOrWhiteSpace(host?.TrustEvaluationPath),
                FilterExtensionStoreTargetResolved =
                    !string.IsNullOrWhiteSpace(filter?.TrustEvaluationPath),
            };
        }

        private NativeModeSignatureTrust InspectPackageTrust(
            NativeModeDriverPackageInfo package)
        {
            if (package == null || !package.Found)
                return null;

            try
            {
                return verifier.VerifyDriverPackage(package);
            }
            catch (Exception ex)
            {
                return NativeModeSignatureTrust.Untrusted(
                    "trust verification threw: " + ex.Message);
            }
        }

        private NativeModeSignatureTrust InspectFileTrust(string filePath,
            NativeModeUsbipClientInfo client)
        {
            if (client == null || !client.Found)
                return null;

            try
            {
                return verifier.VerifyFile(filePath);
            }
            catch (Exception ex)
            {
                return NativeModeSignatureTrust.Untrusted(
                    "trust verification threw: " + ex.Message);
            }
        }

        private NativeModeDriverRelease FindMatchingRelease(
            NativeModeDriverPackageInfo host, NativeModeDriverPackageInfo filter)
        {
            foreach (NativeModeDriverRelease release in manifest.Releases)
            {
                if (IdentityMatches(host, release.UdeHostController, release) &&
                    IdentityMatches(filter, release.FilterExtension, release))
                {
                    return release;
                }
            }

            return null;
        }

        private static bool IdentityMatches(NativeModeDriverPackageInfo package,
            NativeModeDriverPackageSpec spec, NativeModeDriverRelease release)
        {
            return package != null && package.Found &&
                spec.MatchesProvider(package.Provider) &&
                spec.MatchesInf(package.InfName) &&
                spec.MatchesVersion(package.DriverVersion) &&
                release.SupportsArchitecture(package.Architecture);
        }

        /// <summary>
        /// Produces the most specific non-matching diagnostic against a
        /// reference release: missing, wrong provider, wrong INF, mixed pair,
        /// wrong version, or wrong architecture.
        /// </summary>
        private static NativeModeDriverValidationResult DescribeIdentityFailure(
            NativeModeDriverPackageInfo host, NativeModeDriverPackageInfo filter,
            NativeModeDriverRelease reference)
        {
            NativeModeDriverFailureReason hostCheck =
                CheckIdentity(host, reference.UdeHostController, reference);
            NativeModeDriverFailureReason filterCheck =
                CheckIdentity(filter, reference.FilterExtension, reference);

            if (hostCheck == NativeModeDriverFailureReason.NotFound &&
                filterCheck == NativeModeDriverFailureReason.NotFound)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UdeHostController,
                    NativeModeDriverFailureReason.NotFound,
                    "No usbip-win2 driver packages were found. Install the " +
                    $"supported release {reference.ReleaseLabel} using the " +
                    "official usbip-win2 installer.");
            }

            if (hostCheck == NativeModeDriverFailureReason.NotFound)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UdeHostController,
                    NativeModeDriverFailureReason.NotFound,
                    "The usbip-win2 UDE host controller was not found. Install " +
                    $"the supported release {reference.ReleaseLabel}.");
            }

            if (filterCheck == NativeModeDriverFailureReason.NotFound)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.FilterExtension,
                    NativeModeDriverFailureReason.NotFound,
                    "The usbip-win2 filter extension package was not found. " +
                    "Reinstall the supported release so both packages are " +
                    "present.");
            }

            if (hostCheck == NativeModeDriverFailureReason.WrongProvider)
                return ProviderFailure(NativeModeDriverComponent.UdeHostController);
            if (filterCheck == NativeModeDriverFailureReason.WrongProvider)
                return ProviderFailure(NativeModeDriverComponent.FilterExtension);

            if (hostCheck == NativeModeDriverFailureReason.WrongInf)
                return InfFailure(NativeModeDriverComponent.UdeHostController);
            if (filterCheck == NativeModeDriverFailureReason.WrongInf)
                return InfFailure(NativeModeDriverComponent.FilterExtension);

            bool hostVersionWrong = hostCheck == NativeModeDriverFailureReason.WrongVersion;
            bool filterVersionWrong = filterCheck == NativeModeDriverFailureReason.WrongVersion;
            if (hostVersionWrong ^ filterVersionWrong)
            {
                NativeModeDriverComponent mixedComponent = hostVersionWrong
                    ? NativeModeDriverComponent.UdeHostController
                    : NativeModeDriverComponent.FilterExtension;
                return NativeModeDriverValidationResult.Fail(mixedComponent,
                    NativeModeDriverFailureReason.MixedPair,
                    "The usbip-win2 UDE host controller and filter extension " +
                    "are from different releases. Reinstall a single supported " +
                    "release so both packages match.");
            }

            if (hostVersionWrong && filterVersionWrong)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UdeHostController,
                    NativeModeDriverFailureReason.WrongVersion,
                    "The installed usbip-win2 driver packages are not a " +
                    $"supported release. Install release {reference.ReleaseLabel}; " +
                    "older, newer, or unknown packages are refused.");
            }

            if (hostCheck == NativeModeDriverFailureReason.WrongArchitecture)
                return ArchitectureFailure(NativeModeDriverComponent.UdeHostController);
            if (filterCheck == NativeModeDriverFailureReason.WrongArchitecture)
                return ArchitectureFailure(NativeModeDriverComponent.FilterExtension);

            // Should not happen: no release matched yet no component-level
            // difference was identified. Fail closed.
            return NativeModeDriverValidationResult.Fail(
                NativeModeDriverComponent.UdeHostController,
                NativeModeDriverFailureReason.WrongVersion,
                "The installed usbip-win2 driver packages do not match a " +
                "supported release.");
        }

        private static NativeModeDriverFailureReason CheckIdentity(
            NativeModeDriverPackageInfo package, NativeModeDriverPackageSpec spec,
            NativeModeDriverRelease release)
        {
            if (package == null || !package.Found)
                return NativeModeDriverFailureReason.NotFound;
            if (!spec.MatchesProvider(package.Provider))
                return NativeModeDriverFailureReason.WrongProvider;
            if (!spec.MatchesInf(package.InfName))
                return NativeModeDriverFailureReason.WrongInf;
            if (!spec.MatchesVersion(package.DriverVersion))
                return NativeModeDriverFailureReason.WrongVersion;
            if (!release.SupportsArchitecture(package.Architecture))
                return NativeModeDriverFailureReason.WrongArchitecture;
            return NativeModeDriverFailureReason.None;
        }

        private NativeModeDriverValidationResult ValidateDriverTrust(
            NativeModeDriverPackageInfo package,
            NativeModeDriverComponent component,
            NativeModeDriverSignerPolicy policy)
        {
            NativeModeSignatureTrust trust;
            try
            {
                trust = verifier.VerifyDriverPackage(package);
            }
            catch (Exception ex)
            {
                return NativeModeDriverValidationResult.Fail(component,
                    NativeModeDriverFailureReason.InspectionFailed,
                    $"Could not verify the {Describe(component)} signature: " +
                    ex.Message);
            }

            string reject = RejectTrust(trust);
            if (reject != null)
            {
                return NativeModeDriverValidationResult.Fail(component,
                    NativeModeDriverFailureReason.UntrustedSignature,
                    $"The {Describe(component)} signature is not acceptable: " +
                    reject);
            }

            if (policy ==
                NativeModeDriverSignerPolicy.MicrosoftHardwareCompatibilityPublisher &&
                !trust.IsMicrosoftHardwareCompatibilityPublisher)
            {
                return NativeModeDriverValidationResult.Fail(component,
                    NativeModeDriverFailureReason.UntrustedSignature,
                    $"The {Describe(component)} is trusted but is not signed by " +
                    "the Microsoft Hardware Compatibility Publisher required for " +
                    "this release.");
            }

            if (policy == NativeModeDriverSignerPolicy.MicrosoftWhqlCertified &&
                !trust.IsMicrosoftHardwareCompatibilityPublisher)
            {
                return NativeModeDriverValidationResult.Fail(component,
                    NativeModeDriverFailureReason.UntrustedSignature,
                    $"The {Describe(component)} does not satisfy the required " +
                    "WHQL certification policy.");
            }

            return null;
        }

        private NativeModeDriverValidationResult ValidateUsbipClient(
            string usbipExecutablePath, NativeModeUsbipClientSpec spec)
        {
            NativeModeUsbipClientInfo client;
            try
            {
                client = inspector.InspectUsbipClient(usbipExecutablePath);
            }
            catch (Exception ex)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UsbipClient,
                    NativeModeDriverFailureReason.InspectionFailed,
                    $"Could not read the usbip.exe client: {ex.Message}");
            }

            if (client == null || !client.Found)
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UsbipClient,
                    NativeModeDriverFailureReason.NotFound,
                    "The usbip.exe userspace client was not found at the " +
                    "configured location.");
            }

            if (!spec.MatchesFileName(client.FileName))
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UsbipClient,
                    NativeModeDriverFailureReason.WrongInf,
                    "The configured client is not named usbip.exe.");
            }

            if (!spec.MatchesProductVersion(client.ProductVersion))
            {
                return NativeModeDriverValidationResult.Fail(
                    NativeModeDriverComponent.UsbipClient,
                    NativeModeDriverFailureReason.WrongVersion,
                    "The usbip.exe client product version does not match the " +
                    $"supported release {spec.ProductVersion}.");
            }

            if (spec.RequireAuthenticode)
            {
                NativeModeSignatureTrust trust;
                try
                {
                    trust = verifier.VerifyFile(usbipExecutablePath);
                }
                catch (Exception ex)
                {
                    return NativeModeDriverValidationResult.Fail(
                        NativeModeDriverComponent.UsbipClient,
                        NativeModeDriverFailureReason.InspectionFailed,
                        $"Could not verify the usbip.exe signature: {ex.Message}");
                }

                string reject = RejectTrust(trust);
                if (reject != null)
                {
                    return NativeModeDriverValidationResult.Fail(
                        NativeModeDriverComponent.UsbipClient,
                        NativeModeDriverFailureReason.UntrustedSignature,
                        "The usbip.exe Authenticode signature is not " +
                        $"acceptable: {reject}");
                }
            }

            return null;
        }

        /// <summary>
        /// Returns a non-null rejection reason when the trust result is not
        /// clean under normal chain policy; null when acceptable.
        /// </summary>
        private static string RejectTrust(NativeModeSignatureTrust trust)
        {
            if (trust == null)
                return "no trust result was produced";
            if (trust.Revoked)
                return "the signing certificate is revoked";
            if (trust.Expired)
                return "the signing certificate is expired";
            if (trust.TestSigned)
                return "the package is test-signed";
            if (trust.DeveloperSigned)
                return "the package is developer-signed / not chained to a trusted root";
            if (!trust.Trusted)
            {
                return string.IsNullOrWhiteSpace(trust.Diagnostic)
                    ? "the signature is not trusted"
                    : trust.Diagnostic;
            }

            return null;
        }

        private static string Describe(NativeModeDriverComponent component) =>
            component switch
            {
                NativeModeDriverComponent.UdeHostController => "UDE host controller",
                NativeModeDriverComponent.FilterExtension => "filter extension",
                NativeModeDriverComponent.UsbipClient => "usbip.exe client",
                _ => "component",
            };

        private static NativeModeDriverValidationResult ProviderFailure(
            NativeModeDriverComponent component) =>
            NativeModeDriverValidationResult.Fail(component,
                NativeModeDriverFailureReason.WrongProvider,
                $"The {Describe(component)} reports an unexpected provider. " +
                "Only the supported usbip-win2 packages are accepted.");

        private static NativeModeDriverValidationResult InfFailure(
            NativeModeDriverComponent component) =>
            NativeModeDriverValidationResult.Fail(component,
                NativeModeDriverFailureReason.WrongInf,
                $"The {Describe(component)} is bound to an unexpected INF.");

        private static NativeModeDriverValidationResult ArchitectureFailure(
            NativeModeDriverComponent component) =>
            NativeModeDriverValidationResult.Fail(component,
                NativeModeDriverFailureReason.WrongArchitecture,
                $"The {Describe(component)} architecture is not supported for " +
                "this release.");
    }

    /// <summary>
    /// Composition + wiring point for the driver validation gate. The gate must
    /// PASS before DS4Windows releases the physical controller or requests
    /// elevation to attach (policy §4). Holds all OS-touching implementations
    /// behind the two interfaces so it is trivially replaced in tests.
    /// </summary>
    public sealed class NativeModeDriverGate
    {
        private readonly NativeModeDriverValidator validator;

        public NativeModeDriverGate(NativeModeDriverValidator validator)
        {
            this.validator = validator ??
                throw new ArgumentNullException(nameof(validator));
        }

        /// <summary>The shared production gate wired to the real OS inspectors.</summary>
        public static NativeModeDriverGate Default { get; } = CreateDefault();

        public static NativeModeDriverGate CreateDefault()
        {
            var validator = new NativeModeDriverValidator(
                NativeModeDriverManifest.Supported,
                new SetupApiDriverPackageInspector(),
                new WinTrustAuthenticodeVerifier());
            return new NativeModeDriverGate(validator);
        }

        /// <summary>
        /// Full validation with rich result. Callers must not release the
        /// controller unless <see cref="NativeModeDriverValidationResult.Passed"/>
        /// is true.
        /// </summary>
        public NativeModeDriverValidationResult Validate(string usbipExecutablePath) =>
            validator.Validate(usbipExecutablePath);

        /// <summary>
        /// Read-only diagnostic pass used by the <c>-validatedriver</c> command.
        /// Nothing is released, elevated, attached, or modified; the gate only
        /// reads device, driver, and file state.
        /// </summary>
        public NativeModeDriverValidationReport Inspect(string usbipExecutablePath) =>
            validator.Inspect(usbipExecutablePath);

        /// <summary>
        /// Elevation-path guard. Returns null when validation passes; otherwise
        /// a fail-closed <see cref="NativeModeAttachResult"/> the broker returns
        /// before requesting elevation.
        /// </summary>
        public NativeModeAttachResult ValidateBeforeElevation(string usbipExecutablePath)
        {
            NativeModeDriverValidationResult result = Validate(usbipExecutablePath);
            return result.Passed
                ? null
                : NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.DriverValidationFailed,
                    result.Diagnostic);
        }
    }
}
