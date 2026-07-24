/*
DS4Windows
Copyright (C) 2023  Travis Nickles

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
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels.Util;
using LEDBarMode = DS4Windows.DualSenseControllerOptions.LEDBarMode;
using MuteLEDMode = DS4Windows.DualSenseControllerOptions.MuteLEDMode;
using LinkMode = DS4Windows.JoyConDeviceOptions.LinkMode;
using JoinedGyroProvider = DS4Windows.JoyConDeviceOptions.JoinedGyroProvider;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class ControllerRegDeviceOptsViewModel : IDisposable
    {
        private ControlServiceDeviceOptions serviceDeviceOpts;
        private ControlService service;

        public bool EnableDS4 { get => serviceDeviceOpts.DS4DeviceOpts.Enabled; }

        public bool EnableDualSense { get => serviceDeviceOpts.DualSenseOpts.Enabled; }

        public bool EnableSwitchPro { get => serviceDeviceOpts.SwitchProDeviceOpts.Enabled; }

        public bool EnableJoyCon { get => serviceDeviceOpts.JoyConDeviceOpts.Enabled; }

        public bool EnableDS3 { get => serviceDeviceOpts.DS3DeviceOpts.Enabled; }

        public DS4DeviceOptions DS4DeviceOpts { get => serviceDeviceOpts.DS4DeviceOpts; }
        public DS3DeviceOptions DS3DeviceOpts { get => serviceDeviceOpts.DS3DeviceOpts; }
        public DualSenseDeviceOptions DSDeviceOpts { get => serviceDeviceOpts.DualSenseOpts; }
        public SwitchProDeviceOptions SwitchProDeviceOpts { get => serviceDeviceOpts.SwitchProDeviceOpts; }
        public JoyConDeviceOptions JoyConDeviceOpts { get => serviceDeviceOpts.JoyConDeviceOpts; }

        public bool UseMoonlightChanged
        {
            get;
            private set;
        }

        public bool UseMoonlight
        {
            get => Global.UseMoonlight;
            set
            {
                UseMoonlightChanged = value != Global.UseMoonlight;
                Global.UseMoonlight = value;
            }
        }

        public bool UseAdvancedMoonlight
        {
            get => Global.UseAdvancedMoonlight;
            set
            {
                UseMoonlightChanged = value != Global.UseAdvancedMoonlight;
                Global.UseAdvancedMoonlight = value;
            }
        }

        public bool VerboseLogMessages { get => serviceDeviceOpts.VerboseLogMessages; set => serviceDeviceOpts.VerboseLogMessages = value; }

        private List<DeviceListItem> currentInputDevices = new List<DeviceListItem>();
        public List<DeviceListItem> CurrentInputDevices { get => currentInputDevices; }

        // Serial, ControllerOptionsStore instance
        private Dictionary<string, ControllerOptionsStore> inputDeviceSettings = new Dictionary<string, ControllerOptionsStore>();
        private List<ControllerOptionsStore> controllerOptionsStores = new List<ControllerOptionsStore>();

        private int controllerSelectedIndex = -1;
        public int ControllerSelectedIndex
        {
            get => controllerSelectedIndex;
            set
            {
                if (controllerSelectedIndex == value) return;
                controllerSelectedIndex = value;
                ControllerSelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ControllerSelectedIndexChanged;

        public DS4ControllerOptions CurrentDS4Options
        {
            get => controllerOptionsStores[controllerSelectedIndex] as DS4ControllerOptions;
        }

        public DualSenseControllerOptions CurrentDSOptions
        {
            get => controllerOptionsStores[controllerSelectedIndex] as DualSenseControllerOptions;
        }

        public SwitchProControllerOptions CurrentSwitchProOptions
        {
            get => controllerOptionsStores[controllerSelectedIndex] as SwitchProControllerOptions;
        }

        public JoyConControllerOptions CurrentJoyConOptions
        {
            get => controllerOptionsStores[controllerSelectedIndex] as JoyConControllerOptions;
        }

        private int currentTabSelectedIndex = 0;
        public int CurrentTabSelectedIndex
        {
            get => currentTabSelectedIndex;
            set
            {
                if (currentTabSelectedIndex == value) return;
                currentTabSelectedIndex = value;
                CurrentTabSelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler CurrentTabSelectedIndexChanged;

        public ControllerRegDeviceOptsViewModel(ControlServiceDeviceOptions serviceDeviceOpts,
            ControlService service)
        {
            this.serviceDeviceOpts = serviceDeviceOpts;
            this.service = service;

            int idx = 0;
            foreach(DS4Device device in service.DS4Controllers)
            {
                if (device != null)
                {
                    currentInputDevices.Add(new DeviceListItem(device, idx));
                    inputDeviceSettings.Add(device.MacAddress, device.optionsStore);
                    controllerOptionsStores.Add(device.optionsStore);
                }
                idx++;
            }
        }

        private object dataContextObject = null;
        public object DataContextObject { get => dataContextObject; }

        public int FindTabOptionsIndex()
        {
            ControllerOptionsStore currentStore =
                controllerOptionsStores[controllerSelectedIndex];

            int result = 0;
            switch (currentStore.DeviceType)
            {
                case DS4Windows.InputDevices.InputDeviceType.DS3:
                    result = 0;
                    break;
                case DS4Windows.InputDevices.InputDeviceType.DS4:
                    result = 1;
                    break;
                case DS4Windows.InputDevices.InputDeviceType.DualSense:
                    result = 2;
                    break;
                case DS4Windows.InputDevices.InputDeviceType.SwitchPro:
                    result = 3;
                    break;
                case DS4Windows.InputDevices.InputDeviceType.JoyConL:
                case DS4Windows.InputDevices.InputDeviceType.JoyConR:
                    result = 4;
                    break;
                default:
                    // Default to empty control
                    result = 0;
                    break;
            }

            return result;
        }

        public void FindFittingDataContext()
        {
            if (dataContextObject is IDisposable disposable)
                disposable.Dispose();

            ControllerOptionsStore currentStore =
                controllerOptionsStores[controllerSelectedIndex];

            switch (currentStore.DeviceType)
            {
                case DS4Windows.InputDevices.InputDeviceType.DS3:
                    // Does not have device specific options
                    break;
                case DS4Windows.InputDevices.InputDeviceType.DS4:
                    dataContextObject = new DS4ControllerOptionsWrapper(CurrentDS4Options, serviceDeviceOpts.DS4DeviceOpts);
                    break;
                case DS4Windows.InputDevices.InputDeviceType.DualSense:
                    dataContextObject = new DualSenseControllerOptionsWrapper(
                        CurrentDSOptions, serviceDeviceOpts.DualSenseOpts, service,
                        currentInputDevices[controllerSelectedIndex].DeviceIndex);
                    break;
                case DS4Windows.InputDevices.InputDeviceType.SwitchPro:
                    dataContextObject = new SwitchProControllerOptionsWrapper(CurrentSwitchProOptions, serviceDeviceOpts.SwitchProDeviceOpts);
                    break;
                case DS4Windows.InputDevices.InputDeviceType.JoyConL:
                case DS4Windows.InputDevices.InputDeviceType.JoyConR:
                    dataContextObject = new JoyConControllerOptionsWrapper(CurrentJoyConOptions, serviceDeviceOpts.JoyConDeviceOpts);
                    break;
                default:
                    break;
            }
        }

        public void SaveControllerConfigs()
        {
            foreach (DeviceListItem item in currentInputDevices)
            {
                Global.SaveControllerConfigs(item.Device);
            }
        }

        public void Dispose()
        {
            if (dataContextObject is IDisposable disposable)
                disposable.Dispose();
            dataContextObject = null;
        }
    }

    public class DeviceListItem
    {
        private DS4Device device;
        public DS4Device Device { get => device; }
        public int DeviceIndex { get; }

        public string IdText
        {
            get => $"{device.DisplayName} ({device.MacAddress})";
        }

        public DeviceListItem(DS4Device device, int deviceIndex)
        {
            this.device = device;
            DeviceIndex = deviceIndex;
        }
    }


    public class DS4ControllerOptionsWrapper
    {
        private DS4ControllerOptions options;
        public DS4ControllerOptions Options { get => options; }

        private DS4DeviceOptions parentOptions;
        public bool Visible
        {
            get => parentOptions.Enabled;
        }
        public event EventHandler VisibleChanged;

        public DS4ControllerOptionsWrapper(DS4ControllerOptions options, DS4DeviceOptions parentOpts)
        {
            this.options = options;
            this.parentOptions = parentOpts;
            parentOptions.EnabledChanged += (sender, e) => { VisibleChanged?.Invoke(this, EventArgs.Empty); };
        }
    }

    public class DualSenseControllerOptionsWrapper : IDisposable
    {
        private DualSenseControllerOptions options;
        public DualSenseControllerOptions Options { get => options; }

        private readonly ControlService service;
        private readonly int deviceIndex;
        private readonly SynchronizationContext uiContext;
        private bool nativeModeOperationInProgress;
        private string nativeModeStatus;
        private bool nativeHapticsDetected;
        private double nativeHapticsLeftRmsPercent;
        private double nativeHapticsRightRmsPercent;
        private long nativeHapticsBluetoothErrors;

        private DualSenseDeviceOptions parentOptions;
        public bool Visible { get => parentOptions.Enabled; }
        public event EventHandler VisibleChanged;

        private List<EnumChoiceSelection<LEDBarMode>> dsLEDModeOptions = new List<EnumChoiceSelection<LEDBarMode>>()
        {
            new EnumChoiceSelection<LEDBarMode>("Off", LEDBarMode.Off),
            new EnumChoiceSelection<LEDBarMode>("Only for multiple controllers", LEDBarMode.MultipleControllers),
            new EnumChoiceSelection<LEDBarMode>("Battery Percentage", LEDBarMode.BatteryPercentage),
            new EnumChoiceSelection<LEDBarMode>("On", LEDBarMode.On),
        };
        public List<EnumChoiceSelection<LEDBarMode>> DsLEDModes { get => dsLEDModeOptions; }

        private List<EnumChoiceSelection<MuteLEDMode>> dsMuteLEDModes = new List<EnumChoiceSelection<MuteLEDMode>>()
        {
            new EnumChoiceSelection<MuteLEDMode>("Off", MuteLEDMode.Off),
            new EnumChoiceSelection<MuteLEDMode>("On", MuteLEDMode.On),
            new EnumChoiceSelection<MuteLEDMode>("Pulse", MuteLEDMode.Pulse),
        };
        public List<EnumChoiceSelection<MuteLEDMode>> DsMuteLEDModes { get => dsMuteLEDModes; }

        private List<EnumChoiceSelection<DualSenseControllerOptions.HapticsMode>> dsHapticsModes =
            new List<EnumChoiceSelection<DualSenseControllerOptions.HapticsMode>>()
        {
            new EnumChoiceSelection<DualSenseControllerOptions.HapticsMode>(
                Translations.Strings.ControllerRegOptWin_HapticsModeOff,
                DualSenseControllerOptions.HapticsMode.Off),
            new EnumChoiceSelection<DualSenseControllerOptions.HapticsMode>(
                Translations.Strings.ControllerRegOptWin_HapticsModeSystemAudio,
                DualSenseControllerOptions.HapticsMode.SystemAudio),
            new EnumChoiceSelection<DualSenseControllerOptions.HapticsMode>(
                Translations.Strings.ControllerRegOptWin_HapticsModeRumble,
                DualSenseControllerOptions.HapticsMode.RumbleToHaptics),
            new EnumChoiceSelection<DualSenseControllerOptions.HapticsMode>(
                Translations.Strings.ControllerRegOptWin_HapticsModeMix,
                DualSenseControllerOptions.HapticsMode.Mix),
        };
        public List<EnumChoiceSelection<DualSenseControllerOptions.HapticsMode>> DsHapticsModes { get => dsHapticsModes; }

        private List<HapticsAudioDeviceChoice> hapticsAudioDevices = new List<HapticsAudioDeviceChoice>();
        public List<HapticsAudioDeviceChoice> HapticsAudioDevices { get => hapticsAudioDevices; }

        private List<EnumChoiceSelection<DualSenseControllerOptions.AudioOutputRoute>> dsAudioRoutes =
            new List<EnumChoiceSelection<DualSenseControllerOptions.AudioOutputRoute>>()
        {
            new EnumChoiceSelection<DualSenseControllerOptions.AudioOutputRoute>(
                Translations.Strings.ControllerRegOptWin_AudioRouteAuto,
                DualSenseControllerOptions.AudioOutputRoute.Auto),
            new EnumChoiceSelection<DualSenseControllerOptions.AudioOutputRoute>(
                Translations.Strings.ControllerRegOptWin_AudioRouteHeadphones,
                DualSenseControllerOptions.AudioOutputRoute.Headphone),
            new EnumChoiceSelection<DualSenseControllerOptions.AudioOutputRoute>(
                Translations.Strings.ControllerRegOptWin_AudioRouteSpeaker,
                DualSenseControllerOptions.AudioOutputRoute.Speaker),
        };
        public List<EnumChoiceSelection<DualSenseControllerOptions.AudioOutputRoute>> DsAudioRoutes { get => dsAudioRoutes; }

        private List<EnumChoiceSelection<DualSenseControllerOptions.AudioLatencyMode>> dsAudioLatencies =
            new List<EnumChoiceSelection<DualSenseControllerOptions.AudioLatencyMode>>()
        {
            new EnumChoiceSelection<DualSenseControllerOptions.AudioLatencyMode>(
                Translations.Strings.ControllerRegOptWin_LatencySmooth,
                DualSenseControllerOptions.AudioLatencyMode.Smooth),
            new EnumChoiceSelection<DualSenseControllerOptions.AudioLatencyMode>(
                Translations.Strings.ControllerRegOptWin_LatencyBalanced,
                DualSenseControllerOptions.AudioLatencyMode.Balanced),
            new EnumChoiceSelection<DualSenseControllerOptions.AudioLatencyMode>(
                Translations.Strings.ControllerRegOptWin_LatencyLow,
                DualSenseControllerOptions.AudioLatencyMode.LowLatency),
        };
        public List<EnumChoiceSelection<DualSenseControllerOptions.AudioLatencyMode>> DsAudioLatencies { get => dsAudioLatencies; }

        private (string ButtonText, bool CanToggle, bool SettingsEnabled,
            bool SetupCanRun) NativeModeControls =>
            ProjectNativeModeControls(nativeModeOperationInProgress,
                IsNativeModeSessionActive, service.NativeModeManager.State);

        public string NativeModeButtonText => NativeModeControls.ButtonText;
        public event EventHandler NativeModeButtonTextChanged;

        public bool NativeModeCanToggle => NativeModeControls.CanToggle;
        public event EventHandler NativeModeCanToggleChanged;

        public bool NativeModeSettingsEnabled => NativeModeControls.SettingsEnabled;
        public event EventHandler NativeModeSettingsEnabledChanged;

        public bool NativeModeSetupCanRun => NativeModeControls.SetupCanRun;
        public event EventHandler NativeModeSetupCanRunChanged;

        public string NativeModeStatus => nativeModeStatus;
        public event EventHandler NativeModeStatusChanged;

        public DualSenseControllerOptionsWrapper(DualSenseControllerOptions options,
            DualSenseDeviceOptions parentOpts, ControlService service, int deviceIndex)
        {
            this.options = options;
            this.parentOptions = parentOpts;
            this.service = service;
            this.deviceIndex = deviceIndex;
            uiContext = SynchronizationContext.Current;
            parentOptions.EnabledChanged += (sender, e) => { VisibleChanged?.Invoke(this, EventArgs.Empty); };
            service.NativeModeManager.StateChanged += NativeModeManager_StateChanged;
            service.NativeModeManager.StatsChanged += NativeModeManager_StatsChanged;
            service.NativeModeSessionActivityChanged += ControlService_NativeModeSessionActivityChanged;
            nativeModeStatus = StatusForState(service.NativeModeManager.State,
                null, IsNativeModeSessionActive);

            PopulateHapticsAudioDevices();
        }

        public async Task ToggleNativeModeAsync()
        {
            if (nativeModeOperationInProgress)
                return;

            nativeModeOperationInProgress = true;
            NotifyNativeModeProperties();
            try
            {
                if (IsNativeModeSessionActive)
                    await service.StopNativeModeAsync();
                else
                {
                    System.Windows.MessageBoxResult confirmation =
                        System.Windows.MessageBox.Show(
                            NativeModeText("NativeModeSafetyConfirmation"),
                            NativeModeText("NativeModeSafetyConfirmationTitle"),
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning,
                            System.Windows.MessageBoxResult.No);
                    if (confirmation != System.Windows.MessageBoxResult.Yes)
                        return;

                    ApplyNativeSettingsToCurrentController();
                    await service.StartNativeModeAsync(deviceIndex);
                }
            }
            catch (Exception ex)
            {
                NativeModeState state = service.NativeModeManager.State;
                nativeModeStatus = state == NativeModeState.SetupRequired ||
                    state == NativeModeState.Faulted
                    ? StatusForState(state, ex.Message)
                    : NativeModeTextFormat("NativeModeErrorFormat", ex.Message);
                NativeModeStatusChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                nativeModeOperationInProgress = false;
                NotifyNativeModeProperties();
            }
        }

        public async Task SetupNativeModeAsync()
        {
            if (nativeModeOperationInProgress || IsNativeModeSessionActive)
                return;

            nativeModeOperationInProgress = true;
            NotifyNativeModeProperties();
            try
            {
                nativeModeStatus =
                    NativeModeText("NativeModeCheckingRequirements");
                NativeModeStatusChanged?.Invoke(this, EventArgs.Empty);
                NativeModeAttachResult result =
                    await service.EnsureNativeModeAttachTaskAsync();
                nativeModeStatus = result.Success
                    ? NativeModeText("NativeModeRequirementsReady")
                    : NativeModeTextFormat(
                        "NativeModeRequirementsFailedFormat", result.Reason);
                NativeModeStatusChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                nativeModeStatus = NativeModeTextFormat(
                    "NativeModeRequirementsFailedFormat", ex.Message);
                NativeModeStatusChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                nativeModeOperationInProgress = false;
                NotifyNativeModeProperties();
            }
        }

        public void Dispose()
        {
            service.NativeModeManager.StateChanged -= NativeModeManager_StateChanged;
            service.NativeModeManager.StatsChanged -= NativeModeManager_StatsChanged;
            service.NativeModeSessionActivityChanged -= ControlService_NativeModeSessionActivityChanged;
        }

        private bool IsNativeModeSessionActive => service.IsNativeModeSessionActive;

        private void ApplyNativeSettingsToCurrentController()
        {
            if (deviceIndex < 0 || deviceIndex >= service.DS4Controllers.Length ||
                service.DS4Controllers[deviceIndex] is not DS4Windows.InputDevices.DualSenseDevice device)
            {
                return;
            }

            device.NativeOptionsStore.NativeModeSpeakerAudio = options.NativeModeSpeakerAudio;
            device.NativeOptionsStore.NativeModeSpeakerVolume = options.NativeModeSpeakerVolume;
            device.NativeOptionsStore.NativeModeRoute = options.NativeModeRoute;
        }

        private void NativeModeManager_StateChanged(object sender,
            NativeModeStateChangedEventArgs e)
        {
            if (uiContext != null && SynchronizationContext.Current != uiContext)
            {
                uiContext.Post(_ => ApplyNativeModeState(e), null);
            }
            else
            {
                ApplyNativeModeState(e);
            }
        }

        private void NativeModeManager_StatsChanged(object sender, EventArgs e)
        {
            if (uiContext != null && SynchronizationContext.Current != uiContext)
            {
                uiContext.Post(_ => ApplyNativeModeStats(), null);
            }
            else
            {
                ApplyNativeModeStats();
            }
        }

        private void ControlService_NativeModeSessionActivityChanged(object sender, EventArgs e)
        {
            DispatchNativeModePropertyRefresh(uiContext,
                () =>
                {
                    RefreshStoppedNativeModeStatus();
                    NotifyNativeModeProperties();
                });
        }

        internal static (string ButtonText, bool CanToggle,
            bool SettingsEnabled, bool SetupCanRun) ProjectNativeModeControls(
            bool operationInProgress, bool sessionActive,
            NativeModeState managerState)
        {
            bool inactive = !operationInProgress && !sessionActive;
            return (
                sessionActive
                    ? NativeModeText("NativeModeStop")
                    : NativeModeText("NativeModeStart"),
                !operationInProgress && managerState != NativeModeState.Starting,
                inactive,
                inactive);
        }

        internal static void DispatchNativeModePropertyRefresh(
            SynchronizationContext context, Action refresh)
        {
            if (context != null && SynchronizationContext.Current != context)
                context.Post(_ => refresh(), null);
            else
                refresh();
        }

        private void ApplyNativeModeState(NativeModeStateChangedEventArgs e)
        {
            if (e.State == NativeModeState.Starting)
            {
                nativeHapticsDetected = false;
                nativeHapticsLeftRmsPercent = 0.0;
                nativeHapticsRightRmsPercent = 0.0;
                nativeHapticsBluetoothErrors = 0;
            }

            nativeModeStatus = e.State == NativeModeState.Attached
                ? StatusForAttachedHaptics(nativeHapticsDetected,
                    nativeHapticsLeftRmsPercent, nativeHapticsRightRmsPercent,
                    nativeHapticsBluetoothErrors)
                : StatusForState(e.State, e.Detail,
                    IsNativeModeSessionActive);
            NativeModeStatusChanged?.Invoke(this, EventArgs.Empty);
            NotifyNativeModeProperties();
        }

        private void RefreshStoppedNativeModeStatus()
        {
            if (service.NativeModeManager.State != NativeModeState.Stopped)
                return;

            string projected = StatusForState(NativeModeState.Stopped, null,
                IsNativeModeSessionActive);
            if (string.Equals(nativeModeStatus, projected,
                StringComparison.Ordinal))
            {
                return;
            }

            nativeModeStatus = projected;
            NativeModeStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyNativeModeStats()
        {
            if (!NativeModeIsoTelemetryParser.TryParse(
                service.NativeModeManager.LatestStats.IsochronousOut,
                out NativeModeIsoTelemetry telemetry))
            {
                return;
            }

            nativeHapticsBluetoothErrors = telemetry.BluetoothErrorCount;
            if (telemetry.HasNativeHapticSignal)
            {
                nativeHapticsDetected = true;
                nativeHapticsLeftRmsPercent = telemetry.Channel3RmsPercent;
                nativeHapticsRightRmsPercent = telemetry.Channel4RmsPercent;
            }

            if (service.NativeModeManager.State != NativeModeState.Attached)
                return;

            nativeModeStatus = StatusForAttachedHaptics(nativeHapticsDetected,
                nativeHapticsLeftRmsPercent, nativeHapticsRightRmsPercent,
                nativeHapticsBluetoothErrors);
            NativeModeStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void NotifyNativeModeProperties()
        {
            NativeModeButtonTextChanged?.Invoke(this, EventArgs.Empty);
            NativeModeCanToggleChanged?.Invoke(this, EventArgs.Empty);
            NativeModeSettingsEnabledChanged?.Invoke(this, EventArgs.Empty);
            NativeModeSetupCanRunChanged?.Invoke(this, EventArgs.Empty);
        }

        internal static string StatusForAttachedHaptics(bool detected,
            double leftRmsPercent, double rightRmsPercent, long bluetoothErrors)
        {
            return detected
                ? NativeModeTextFormat("NativeModeAttachedDetectedFormat",
                    leftRmsPercent, rightRmsPercent, bluetoothErrors)
                : NativeModeTextFormat("NativeModeAttachedWaitingFormat",
                    bluetoothErrors);
        }

        internal static string StatusForState(NativeModeState state,
            string detail, bool sessionActive = false)
        {
            if (state == NativeModeState.Stopped && sessionActive)
                return NativeModeText("NativeModeCleanupPendingStatus");

            return state switch
            {
                NativeModeState.Starting =>
                    NativeModeText("NativeModeStartingStatus"),
                NativeModeState.Serving =>
                    NativeModeText("NativeModeServingStatus"),
                NativeModeState.Attached =>
                    NativeModeText("NativeModeAttachedStatus"),
                NativeModeState.PadLost =>
                    NativeModeText("NativeModePadLostStatus"),
                NativeModeState.SetupRequired => string.IsNullOrWhiteSpace(detail)
                    ? NativeModeText("NativeModeRequirementsRequiredStatus")
                    : detail,
                NativeModeState.Faulted => string.IsNullOrWhiteSpace(detail)
                    ? NativeModeText("NativeModeFaultedStatus")
                    : NativeModeTextFormat("NativeModeFaultedFormat", detail),
                _ => NativeModeText("NativeModeStoppedStatus"),
            };
        }

        private static string NativeModeText(string suffix) =>
            Translations.Strings.ResourceManager.GetString(
                $"ControllerRegOptWin.{suffix}",
                Translations.Strings.Culture) ?? suffix;

        private static string NativeModeTextFormat(
            string suffix, params object[] arguments) =>
            string.Format(
                Translations.Strings.Culture ??
                    CultureInfo.CurrentUICulture,
                NativeModeText(suffix), arguments);

        private void PopulateHapticsAudioDevices()
        {
            hapticsAudioDevices.Add(new HapticsAudioDeviceChoice(
                Translations.Strings.ControllerRegOptWin_AudioDeviceDefault, string.Empty));
            try
            {
                using NAudio.CoreAudioApi.MMDeviceEnumerator enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                foreach (NAudio.CoreAudioApi.MMDevice dev in enumerator.EnumerateAudioEndPoints(
                    NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                {
                    hapticsAudioDevices.Add(new HapticsAudioDeviceChoice(dev.FriendlyName, dev.ID));
                    dev.Dispose();
                }
            }
            catch (Exception)
            {
                // Endpoint enumeration is best-effort; the default entry always works.
            }

            // Keep the ComboBox binding valid if the saved endpoint disappeared.
            if (!string.IsNullOrEmpty(options.BTHapticsAudioDeviceId) &&
                !hapticsAudioDevices.Exists(item => item.Id == options.BTHapticsAudioDeviceId))
            {
                hapticsAudioDevices.Add(new HapticsAudioDeviceChoice(
                    Translations.Strings.ControllerRegOptWin_AudioDeviceUnavailable,
                    options.BTHapticsAudioDeviceId));
            }
        }
    }

    public class HapticsAudioDeviceChoice
    {
        public string DisplayName { get; }
        public string Id { get; }

        public HapticsAudioDeviceChoice(string displayName, string id)
        {
            DisplayName = displayName;
            Id = id;
        }
    }

    public class SwitchProControllerOptionsWrapper
    {
        private SwitchProControllerOptions options;
        public SwitchProControllerOptions Options { get => options; }

        private SwitchProDeviceOptions parentOptions;
        public bool Visible { get => parentOptions.Enabled; }
        public event EventHandler VisibleChanged;

        public SwitchProControllerOptionsWrapper(SwitchProControllerOptions options,
            SwitchProDeviceOptions parentOpts)
        {
            this.options = options;
            this.parentOptions = parentOpts;
            parentOptions.EnabledChanged += (sender, e) => { VisibleChanged?.Invoke(this, EventArgs.Empty); };
        }
    }

    public class JoyConControllerOptionsWrapper
    {
        private JoyConControllerOptions options;
        public JoyConControllerOptions Options { get => options; }

        private JoyConDeviceOptions parentOptions;
        public JoyConDeviceOptions ParentOptions { get => parentOptions; }

        public bool Visible { get => parentOptions.Enabled; }
        public event EventHandler VisibleChanged;

        private List<EnumChoiceSelection<LinkMode>> linkModes = new List<EnumChoiceSelection<LinkMode>>()
        {
            new EnumChoiceSelection<LinkMode>("Split", LinkMode.Split),
            new EnumChoiceSelection<LinkMode>("Joined", LinkMode.Joined),
        };
        public List<EnumChoiceSelection<LinkMode>> LinkModes { get => linkModes; }

        private List<EnumChoiceSelection<JoinedGyroProvider>> joinGyroOptions = new List<EnumChoiceSelection<JoinedGyroProvider>>()
        {
            new EnumChoiceSelection<JoinedGyroProvider>("Left", JoinedGyroProvider.JoyConL),
            new EnumChoiceSelection<JoinedGyroProvider>("Right", JoinedGyroProvider.JoyConR),
        };
        public List<EnumChoiceSelection<JoinedGyroProvider>> JoinGyroOptions { get => joinGyroOptions; }

        public JoyConControllerOptionsWrapper(JoyConControllerOptions options,
            JoyConDeviceOptions parentOpts)
        {
            this.options = options;
            this.parentOptions = parentOpts;
            parentOptions.EnabledChanged += (sender, e) => { VisibleChanged?.Invoke(this, EventArgs.Empty); };
        }
    }

}
