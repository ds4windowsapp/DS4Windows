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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DS4Windows
{
    /// <summary>
    /// Enumerates usbip-win2 driver packages via SetupAPI / Configuration
    /// Manager, never by parsing localized pnputil text. This is the
    /// OS-touching side of validation; the decision logic lives in
    /// <see cref="NativeModeDriverValidator"/> so it can be tested without the
    /// driver installed.
    /// </summary>
    public sealed class SetupApiDriverPackageInspector : IDriverPackageInspector
    {
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorNoMoreItems = 259;
        private const int InfStyleWin4 = 0x00000002;
        private const uint CrSuccess = 0;
        private const uint DnStarted = 0x00000008;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private static readonly int PresentAllClassesFlags =
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES;

        // DEVPKEY_Device_DriverInfPath: {a8b865dd-2e3d-4094-ad97-e593a70c75d6}, 5.
        private static NativeMethods.DEVPROPKEY DevpkeyDeviceDriverInfPath =
            new NativeMethods.DEVPROPKEY
            {
                fmtid = new Guid(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5,
                    0x93, 0xa7, 0x0c, 0x75, 0xd6),
                pid = 5,
            };

        // DEVPKEY_Device_Service: {a45c254e-df1c-4efd-8020-67d146a850e0}, 6.
        private static NativeMethods.DEVPROPKEY DevpkeyDeviceService =
            new NativeMethods.DEVPROPKEY
            {
                fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67,
                    0xd1, 0x46, 0xa8, 0x50, 0xe0),
                pid = 6,
            };

        public NativeModeDriverPackageInfo InspectHostController(string hardwareId)
        {
            if (string.IsNullOrWhiteSpace(hardwareId))
                throw new ArgumentException("Hardware ID is required.", nameof(hardwareId));

            IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
                IntPtr.Zero, null, 0, PresentAllClassesFlags);
            if (deviceInfoSet == InvalidHandleValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "SetupDiGetClassDevs could not enumerate present devices.");
            }

            try
            {
                for (int index = 0; ; index++)
                {
                    var deviceInfo = new NativeMethods.SP_DEVINFO_DATA
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>(),
                    };
                    if (!NativeMethods.SetupDiEnumDeviceInfo(deviceInfoSet, index,
                        ref deviceInfo))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreItems)
                            break;
                        throw new Win32Exception(error,
                            "SetupDiEnumDeviceInfo failed while locating the UDE host controller.");
                    }

                    IReadOnlyList<string> hardwareIds = GetStringListProperty(
                        deviceInfoSet, ref deviceInfo,
                        ref NativeMethods.DEVPKEY_Device_HardwareIds);
                    if (!ContainsHardwareId(hardwareIds, hardwareId))
                        continue;

                    return ReadHostController(deviceInfoSet, ref deviceInfo,
                        hardwareId);
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return new NativeModeDriverPackageInfo { Found = false };
        }

        private NativeModeDriverPackageInfo ReadHostController(IntPtr deviceInfoSet,
            ref NativeMethods.SP_DEVINFO_DATA deviceInfo, string hardwareId)
        {
            string provider = GetStringProperty(deviceInfoSet, ref deviceInfo,
                ref NativeMethods.DEVPKEY_Device_Provider);
            string driverVersionText = GetStringProperty(deviceInfoSet,
                ref deviceInfo, ref NativeMethods.DEVPKEY_Device_DriverVersion);
            string service = GetStringProperty(deviceInfoSet, ref deviceInfo,
                ref DevpkeyDeviceService);
            string publishedInf = GetStringProperty(deviceInfoSet, ref deviceInfo,
                ref DevpkeyDeviceDriverInfPath);

            string storeInfPath = ResolveDriverStorePath(publishedInf);
            string infName = storeInfPath != null
                ? Path.GetFileName(storeInfPath)
                : publishedInf;
            NativeModeDriverArchitecture architecture =
                ParseArchitecture(storeInfPath);
            string catalogFile = null;
            string trustPath = null;
            if (storeInfPath != null)
            {
                InfVersionValues values = ReadInfVersionValues(storeInfPath);
                catalogFile = values.CatalogFile;
                if (!string.IsNullOrWhiteSpace(values.CatalogFile))
                {
                    trustPath = Path.Combine(
                        Path.GetDirectoryName(storeInfPath) ?? string.Empty,
                        values.CatalogFile);
                }
            }

            (bool present, bool started) = GetDeviceHealth(ref deviceInfo);

            return new NativeModeDriverPackageInfo
            {
                Found = true,
                HardwareId = hardwareId,
                InfName = infName,
                Provider = provider,
                DriverVersion = ParseVersion(driverVersionText),
                Service = service,
                CatalogFile = catalogFile,
                Architecture = architecture,
                DeviceNodePresent = present,
                Started = started,
                TrustEvaluationPath = trustPath ?? storeInfPath,
            };
        }

        public NativeModeDriverPackageInfo InspectFilterExtension(string infName)
        {
            if (string.IsNullOrWhiteSpace(infName))
                throw new ArgumentException("INF name is required.", nameof(infName));

            string repository = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "DriverStore", "FileRepository");
            if (!Directory.Exists(repository))
                return new NativeModeDriverPackageInfo { Found = false };

            NativeModeDriverPackageInfo best = null;
            string prefix = infName + "_";
            foreach (string directory in Directory.EnumerateDirectories(
                repository, prefix + "*"))
            {
                string candidateInf = Path.Combine(directory, infName);
                if (!File.Exists(candidateInf))
                    continue;

                InfVersionValues values = ReadInfVersionValues(candidateInf);
                string catalogPath = string.IsNullOrWhiteSpace(values.CatalogFile)
                    ? candidateInf
                    : Path.Combine(directory, values.CatalogFile);

                var info = new NativeModeDriverPackageInfo
                {
                    Found = true,
                    InfName = infName,
                    Provider = values.Provider,
                    DriverVersion = values.DriverVersion,
                    CatalogFile = values.CatalogFile,
                    Architecture = ParseArchitecture(directory),
                    // Extension packages have no device node of their own; the
                    // host controller carries presence/health.
                    DeviceNodePresent = true,
                    Started = true,
                    TrustEvaluationPath = catalogPath,
                };

                if (best == null || IsHigherVersion(info.DriverVersion,
                    best.DriverVersion))
                {
                    best = info;
                }
            }

            return best ?? new NativeModeDriverPackageInfo { Found = false };
        }

        public NativeModeUsbipClientInfo InspectUsbipClient(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
            {
                return new NativeModeUsbipClientInfo { Found = false };
            }

            Version productVersion = null;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
                productVersion = ParseVersion(info.ProductVersion) ??
                    new Version(info.ProductMajorPart, info.ProductMinorPart,
                        info.ProductBuildPart, info.ProductPrivatePart);
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException || ex is FileNotFoundException)
            {
                return new NativeModeUsbipClientInfo { Found = false };
            }

            return new NativeModeUsbipClientInfo
            {
                Found = true,
                FileName = Path.GetFileName(executablePath),
                ProductVersion = productVersion,
            };
        }

        private (bool present, bool started) GetDeviceHealth(
            ref NativeMethods.SP_DEVINFO_DATA deviceInfo)
        {
            int result = CM_Get_DevNode_Status(out uint status,
                out uint problem, (uint)deviceInfo.DevInst, 0);
            if (result != CrSuccess)
                return (false, false);
            bool started = (status & DnStarted) != 0 && problem == 0;
            return (true, started);
        }

        private static bool ContainsHardwareId(IReadOnlyList<string> hardwareIds,
            string expected)
        {
            if (hardwareIds == null)
                return false;
            foreach (string id in hardwareIds)
            {
                if (string.Equals(id?.Trim(), expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsHigherVersion(Version candidate, Version current)
        {
            if (candidate == null)
                return false;
            if (current == null)
                return true;
            return candidate > current;
        }

        private static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            string trimmed = text.Trim();
            // DriverVer values are "MM/DD/YYYY,x.y.z.w"; keep the numeric part.
            int comma = trimmed.LastIndexOf(',');
            if (comma >= 0 && comma + 1 < trimmed.Length)
                trimmed = trimmed.Substring(comma + 1).Trim();
            // ProductVersion strings can carry a suffix such as "0.9.7.8 (rc)".
            int space = trimmed.IndexOf(' ');
            if (space > 0)
                trimmed = trimmed.Substring(0, space);
            return Version.TryParse(trimmed, out Version parsed) ? parsed : null;
        }

        private static NativeModeDriverArchitecture ParseArchitecture(string storePath)
        {
            if (!string.IsNullOrWhiteSpace(storePath))
            {
                if (storePath.IndexOf("_amd64_", StringComparison.OrdinalIgnoreCase) >= 0)
                    return NativeModeDriverArchitecture.X64;
                if (storePath.IndexOf("_x86_", StringComparison.OrdinalIgnoreCase) >= 0)
                    return NativeModeDriverArchitecture.X86;
            }

            // DS4Windows itself only builds x64/x86, so the running process
            // architecture is a safe fallback when the store decoration is
            // unavailable.
            return RuntimeInformation.ProcessArchitecture == Architecture.X86
                ? NativeModeDriverArchitecture.X86
                : NativeModeDriverArchitecture.X64;
        }

        private static string ResolveDriverStorePath(string publishedInf)
        {
            if (string.IsNullOrWhiteSpace(publishedInf))
                return null;

            int required = 0;
            SetupGetInfDriverStoreLocation(publishedInf, IntPtr.Zero, null, null,
                0, ref required);
            if (required <= 0)
                return null;

            var buffer = new StringBuilder(required);
            if (!SetupGetInfDriverStoreLocation(publishedInf, IntPtr.Zero, null,
                buffer, buffer.Capacity, ref required))
            {
                return null;
            }

            return buffer.ToString();
        }

        private struct InfVersionValues
        {
            public string Provider;
            public Version DriverVersion;
            public string CatalogFile;
        }

        private static InfVersionValues ReadInfVersionValues(string infPath)
        {
            var values = new InfVersionValues();
            if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
                return values;

            IntPtr handle = SetupOpenInfFile(infPath, null, InfStyleWin4,
                out _);
            if (handle == InvalidHandleValue || handle == IntPtr.Zero)
                return values;

            try
            {
                values.Provider = ReadInfLineText(handle, "Version", "Provider");
                values.CatalogFile = ReadInfLineText(handle, "Version", "CatalogFile");
                values.DriverVersion = ParseVersion(
                    ReadInfLineText(handle, "Version", "DriverVer"));
            }
            finally
            {
                SetupCloseInfFile(handle);
            }

            return values;
        }

        private static string ReadInfLineText(IntPtr infHandle, string section,
            string key)
        {
            int required = 0;
            SetupGetLineText(IntPtr.Zero, infHandle, section, key, null, 0,
                out required);
            if (required <= 0)
                return null;

            var buffer = new StringBuilder(required);
            if (!SetupGetLineText(IntPtr.Zero, infHandle, section, key, buffer,
                buffer.Capacity, out required))
            {
                return null;
            }

            string value = buffer.ToString().Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string GetStringProperty(IntPtr deviceInfoSet,
            ref NativeMethods.SP_DEVINFO_DATA deviceInfo,
            ref NativeMethods.DEVPROPKEY key)
        {
            byte[] buffer = GetProperty(deviceInfoSet, ref deviceInfo, ref key);
            if (buffer == null || buffer.Length < 2)
                return null;
            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }

        private static IReadOnlyList<string> GetStringListProperty(
            IntPtr deviceInfoSet, ref NativeMethods.SP_DEVINFO_DATA deviceInfo,
            ref NativeMethods.DEVPROPKEY key)
        {
            byte[] buffer = GetProperty(deviceInfoSet, ref deviceInfo, ref key);
            if (buffer == null || buffer.Length < 2)
                return Array.Empty<string>();
            string raw = Encoding.Unicode.GetString(buffer);
            return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static byte[] GetProperty(IntPtr deviceInfoSet,
            ref NativeMethods.SP_DEVINFO_DATA deviceInfo,
            ref NativeMethods.DEVPROPKEY key)
        {
            ulong propertyType = 0;
            int requiredSize = 0;
            if (NativeMethods.SetupDiGetDeviceProperty(deviceInfoSet,
                ref deviceInfo, ref key, ref propertyType, null, 0,
                ref requiredSize, 0))
            {
                return Array.Empty<byte>();
            }

            int error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer || requiredSize <= 0)
                return null;

            var buffer = new byte[requiredSize];
            if (!NativeMethods.SetupDiGetDeviceProperty(deviceInfoSet,
                ref deviceInfo, ref key, ref propertyType, buffer, buffer.Length,
                ref requiredSize, 0))
            {
                return null;
            }

            return buffer;
        }

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint pulStatus,
            out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupGetInfDriverStoreLocationW")]
        private static extern bool SetupGetInfDriverStoreLocation(string fileName,
            IntPtr alternatePlatformInfo, string localeName,
            StringBuilder returnBuffer, int returnBufferSize, ref int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupOpenInfFileW")]
        private static extern IntPtr SetupOpenInfFile(string fileName,
            string infClass, int infStyle, out uint errorLine);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupGetLineTextW")]
        private static extern bool SetupGetLineText(IntPtr context,
            IntPtr infHandle, string section, string key,
            StringBuilder returnBuffer, int returnBufferSize, out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern void SetupCloseInfFile(IntPtr infHandle);
    }

    /// <summary>
    /// Verifies Authenticode / catalog trust with the Windows trust APIs
    /// (WinVerifyTrust, WINTRUST_ACTION_GENERIC_VERIFY_V2) under normal chain
    /// policy, then reads the signing certificate from the verified chain via
    /// WTHelper to identify the Microsoft Hardware Compatibility Publisher. The
    /// publisher decision uses the chain-derived certificate, never a substring
    /// match on a signer string.
    /// </summary>
    public sealed class WinTrustAuthenticodeVerifier : IAuthenticodeVerifier
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeWholeChain = 1;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdRevocationCheckChain = 0x00000040;
        private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

        private const uint TrustENoSignature = 0x800B0100;
        private const uint CertEExpired = 0x800B0101;
        private const uint CertERevoked = 0x800B010C;
        private const uint CertEUntrustedRoot = 0x800B0109;
        private const uint CertEUntrustedTestRoot = 0x800B010D;
        private const uint TrustEExplicitDistrust = 0x800B0111;

        public NativeModeSignatureTrust VerifyDriverPackage(
            NativeModeDriverPackageInfo package)
        {
            if (package == null || string.IsNullOrWhiteSpace(package.TrustEvaluationPath))
            {
                return NativeModeSignatureTrust.Untrusted(
                    "no catalog or driver file was available to verify");
            }

            return VerifyFile(package.TrustEvaluationPath);
        }

        public NativeModeSignatureTrust VerifyFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return NativeModeSignatureTrust.Untrusted("the file was not found");

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            IntPtr fileInfoPtr = Marshal.AllocHGlobal(
                Marshal.SizeOf<WINTRUST_FILE_INFO>());
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeWholeChain,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = fileInfoPtr,
                    dwStateAction = WtdStateActionVerify,
                    dwProvFlags = WtdRevocationCheckChain | WtdCacheOnlyUrlRetrieval,
                };

                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, dataPtr, false);

                Guid action = WinTrustActionGenericVerifyV2;
                int result = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

                // Re-read the state handle that WinVerifyTrust populated. The
                // signing certificate's common name is read whenever a chain is
                // available, including on failure, so diagnostics can report the
                // certificate that was actually found. Only a verified chain
                // (hr == 0) may satisfy the publisher policy.
                data = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
                string signerCommonName = ReadSignerCommonName(data.hWVTStateData);
                bool publisherOk = result == 0 &&
                    IsHardwareCompatibilityPublisher(signerCommonName);

                return MapResult((uint)result, publisherOk, signerCommonName);
            }
            catch (Exception ex)
            {
                return NativeModeSignatureTrust.Untrusted(
                    "trust verification threw: " + ex.Message);
            }
            finally
            {
                if (dataPtr != IntPtr.Zero)
                {
                    var close = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
                    close.dwStateAction = WtdStateActionClose;
                    Marshal.StructureToPtr(close, dataPtr, true);
                    Guid action = WinTrustActionGenericVerifyV2;
                    WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
                    Marshal.FreeHGlobal(dataPtr);
                }

                if (fileInfoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(fileInfoPtr);
            }
        }

        private static NativeModeSignatureTrust MapResult(uint hr, bool publisherOk,
            string signerCommonName)
        {
            switch (hr)
            {
                case 0:
                    return new NativeModeSignatureTrust
                    {
                        Trusted = true,
                        IsMicrosoftHardwareCompatibilityPublisher = publisherOk,
                        Diagnostic = "trusted",
                        ObservedSignerCommonName = signerCommonName,
                    };
                case CertERevoked:
                    return new NativeModeSignatureTrust
                    {
                        Trusted = false,
                        Revoked = true,
                        Diagnostic = "certificate revoked",
                        ObservedSignerCommonName = signerCommonName,
                    };
                case CertEExpired:
                    return new NativeModeSignatureTrust
                    {
                        Trusted = false,
                        Expired = true,
                        Diagnostic = "certificate expired",
                        ObservedSignerCommonName = signerCommonName,
                    };
                case CertEUntrustedTestRoot:
                    return new NativeModeSignatureTrust
                    {
                        Trusted = false,
                        TestSigned = true,
                        Diagnostic = "test-signed (untrusted test root)",
                        ObservedSignerCommonName = signerCommonName,
                    };
                case CertEUntrustedRoot:
                    return new NativeModeSignatureTrust
                    {
                        Trusted = false,
                        DeveloperSigned = true,
                        Diagnostic = "untrusted root (developer/test signature)",
                        ObservedSignerCommonName = signerCommonName,
                    };
                case TrustENoSignature:
                    return NativeModeSignatureTrust.Untrusted("no valid signature",
                        signerCommonName);
                case TrustEExplicitDistrust:
                    return NativeModeSignatureTrust.Untrusted("explicitly distrusted",
                        signerCommonName);
                default:
                    return NativeModeSignatureTrust.Untrusted(
                        $"WinVerifyTrust hr=0x{hr:X8}", signerCommonName);
            }
        }

        private static bool IsHardwareCompatibilityPublisher(string commonName) =>
            string.Equals(commonName,
                NativeModeDriverManifest
                    .MicrosoftHardwareCompatibilityPublisherCommonName,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the common name of the signing certificate on the chain
        /// WinVerifyTrust built, or null when no certificate is available.
        /// </summary>
        private static string ReadSignerCommonName(IntPtr stateData)
        {
            if (stateData == IntPtr.Zero)
                return null;

            IntPtr provData = WTHelperProvDataFromStateData(stateData);
            if (provData == IntPtr.Zero)
                return null;

            IntPtr signer = WTHelperGetProvSignerFromChain(provData, 0, false, 0);
            if (signer == IntPtr.Zero)
                return null;

            IntPtr providerCert = WTHelperGetProvCertFromChain(signer, 0);
            if (providerCert == IntPtr.Zero)
                return null;

            CRYPT_PROVIDER_CERT cert =
                Marshal.PtrToStructure<CRYPT_PROVIDER_CERT>(providerCert);
            if (cert.pCert == IntPtr.Zero)
                return null;

            try
            {
                using var certificate = new X509Certificate2(cert.pCert);
                string commonName = certificate.GetNameInfo(
                    X509NameType.SimpleName, false);
                return string.IsNullOrWhiteSpace(commonName) ? null : commonName;
            }
            catch (Exception ex) when (ex is CryptographicException ||
                ex is ArgumentException)
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CRYPT_PROVIDER_CERT
        {
            public uint cbStruct;
            public IntPtr pCert;
        }

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId,
            IntPtr pWVTData);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData,
            uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner,
            uint idxCounterSigner);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr,
            uint idxCert);
    }
}
