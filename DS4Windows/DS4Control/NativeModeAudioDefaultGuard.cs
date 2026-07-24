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
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DS4Windows
{
    internal enum NativeModeAudioFlow
    {
        Render = 0,
        Capture = 1,
    }

    internal enum NativeModeAudioRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    internal sealed class NativeModeAudioEndpoint
    {
        public NativeModeAudioEndpoint(string id, string friendlyName,
            string deviceInstanceId = null, Guid? containerId = null)
        {
            Id = id;
            FriendlyName = friendlyName;
            DeviceInstanceId = deviceInstanceId;
            ContainerId = containerId;
        }

        public string Id { get; }
        public string FriendlyName { get; }
        public string DeviceInstanceId { get; }
        public Guid? ContainerId { get; }
    }

    internal sealed class NativeModeAudioDefaultsSnapshot
    {
        public NativeModeAudioDefaultsSnapshot(
            IReadOnlyDictionary<(NativeModeAudioFlow Flow, NativeModeAudioRole Role), string>
                defaultEndpointIds,
            IReadOnlyDictionary<NativeModeAudioFlow, HashSet<string>> activeEndpointIds)
        {
            DefaultEndpointIds = defaultEndpointIds;
            ActiveEndpointIds = activeEndpointIds;
        }

        public IReadOnlyDictionary<
            (NativeModeAudioFlow Flow, NativeModeAudioRole Role), string>
            DefaultEndpointIds { get; }

        public IReadOnlyDictionary<NativeModeAudioFlow, HashSet<string>>
            ActiveEndpointIds { get; }
    }

    internal interface INativeModeAudioEndpointAccessor
    {
        IReadOnlyList<NativeModeAudioEndpoint> GetActiveEndpoints(
            NativeModeAudioFlow flow);
        string GetDefaultEndpointId(NativeModeAudioFlow flow,
            NativeModeAudioRole role);
        void SetDefaultEndpoint(string endpointId, NativeModeAudioRole role);
    }

    internal interface INativeModeAudioNotificationSource
    {
        IDisposable Subscribe(Action endpointOrDefaultChanged);
    }

    internal interface INativeModeAudioWorkQueue
    {
        void Enqueue(Action work);
    }

    /// <summary>
    /// A composite DualSense adds render and capture endpoints after the USB
    /// parent has already arrived. Windows or another audio policy client can
    /// make those endpoints the defaults well after initial enumeration. Keep
    /// observing for the lifetime of native mode instead of relying on a fixed
    /// startup delay.
    /// </summary>
    internal sealed class NativeModeAudioDefaultGuard : IDisposable
    {
        private static readonly NativeModeAudioFlow[] Flows =
        {
            NativeModeAudioFlow.Render,
            NativeModeAudioFlow.Capture,
        };

        private static readonly NativeModeAudioRole[] Roles =
        {
            NativeModeAudioRole.Console,
            NativeModeAudioRole.Multimedia,
            NativeModeAudioRole.Communications,
        };

        private const string DualSenseEndpointMarker =
            "DualSense Wireless Controller";

        private readonly object sessionGate = new object();
        private readonly INativeModeAudioEndpointAccessor accessor;
        private readonly INativeModeAudioNotificationSource notificationSource;
        private readonly INativeModeAudioWorkQueue workQueue;
        private readonly Action<string, bool> log;
        private NativeModeAudioDefaultSession currentSession;

        public NativeModeAudioDefaultGuard() : this(
            new WindowsNativeModeAudioEndpointAccessor(),
            new WindowsNativeModeAudioNotificationSource(),
            new ThreadPoolNativeModeAudioWorkQueue(),
            (message, warning) => AppLogger.LogToGui(message, warning))
        {
        }

        internal NativeModeAudioDefaultGuard(
            INativeModeAudioEndpointAccessor accessor,
            INativeModeAudioNotificationSource notificationSource,
            INativeModeAudioWorkQueue workQueue,
            Action<string, bool> log)
        {
            this.accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            this.notificationSource = notificationSource ??
                throw new ArgumentNullException(nameof(notificationSource));
            this.workQueue = workQueue ?? throw new ArgumentNullException(nameof(workQueue));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public NativeModeAudioDefaultsSnapshot Capture()
        {
            try
            {
                var defaults = new Dictionary<
                    (NativeModeAudioFlow Flow, NativeModeAudioRole Role), string>();
                var active = new Dictionary<NativeModeAudioFlow, HashSet<string>>();

                foreach (NativeModeAudioFlow flow in Flows)
                {
                    active[flow] = accessor.GetActiveEndpoints(flow)
                        .Select(endpoint => endpoint.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (NativeModeAudioRole role in Roles)
                    {
                        try
                        {
                            string endpointId = accessor.GetDefaultEndpointId(flow, role);
                            if (!string.IsNullOrWhiteSpace(endpointId))
                                defaults[(flow, role)] = endpointId;
                        }
                        catch (Exception)
                        {
                            // A role can legitimately have no endpoint. Preserve the
                            // roles that are available instead of disabling the guard.
                        }
                    }
                }

                return new NativeModeAudioDefaultsSnapshot(defaults, active);
            }
            catch (Exception ex)
            {
                log($"[native] Could not snapshot the current audio defaults: {ex.Message}",
                    true);
                throw new InvalidOperationException(
                    "Native Mode cannot start without a complete audio-default " +
                    "endpoint snapshot.", ex);
            }
        }

        /// <summary>
        /// Starts observing after the render keepalive owns the new endpoint,
        /// using the snapshot captured before USB/IP attach. Notification
        /// callbacks only enqueue work; all MMDevice and PolicyConfig calls run
        /// on the queue.
        /// </summary>
        public void BeginSession(NativeModeAudioDefaultsSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            EndSession(restoreDefaultsNow: false);

            var session = new NativeModeAudioDefaultSession(snapshot, accessor,
                notificationSource, workQueue, log);
            lock (sessionGate)
                currentSession = session;

            try
            {
                session.Start();
            }
            catch
            {
                lock (sessionGate)
                {
                    if (ReferenceEquals(currentSession, session))
                        currentSession = null;
                }
                session.Stop(restoreDefaultsNow: false);
                throw;
            }
        }

        /// <summary>
        /// Reconciles once from a normal caller (never from an MMDevice callback).
        /// This covers an attach transition which raced notification registration.
        /// </summary>
        public void ReconcileNow()
        {
            NativeModeAudioDefaultSession session;
            lock (sessionGate)
                session = currentSession;
            session?.ReconcileNow();
        }

        /// <summary>
        /// Unregisters notifications and invalidates queued callbacks. A final
        /// reconciliation, when requested, closes the teardown race before the
        /// virtual endpoint disappears.
        /// </summary>
        public void EndSession(bool restoreDefaultsNow)
        {
            NativeModeAudioDefaultSession session;
            lock (sessionGate)
            {
                session = currentSession;
                currentSession = null;
            }

            session?.Stop(restoreDefaultsNow);
        }

        public void Dispose() => EndSession(restoreDefaultsNow: false);

        private sealed class NativeModeAudioDefaultSession
        {
            private readonly object workGate = new object();
            private readonly NativeModeAudioDefaultsSnapshot snapshot;
            private readonly INativeModeAudioEndpointAccessor accessor;
            private readonly INativeModeAudioNotificationSource notificationSource;
            private readonly INativeModeAudioWorkQueue workQueue;
            private readonly Action<string, bool> log;
            private readonly Dictionary<
                (NativeModeAudioFlow Flow, NativeModeAudioRole Role), string>
                desiredDefaults;
            private readonly Dictionary<
                (NativeModeAudioFlow Flow, NativeModeAudioRole Role), string>
                pendingNewDefaults = new();
            private readonly Dictionary<NativeModeAudioFlow, HashSet<string>>
                sessionEndpointIds;
            private IDisposable registration;
            private int acceptingNotifications = 1;
            private bool disposed;

            public NativeModeAudioDefaultSession(
                NativeModeAudioDefaultsSnapshot snapshot,
                INativeModeAudioEndpointAccessor accessor,
                INativeModeAudioNotificationSource notificationSource,
                INativeModeAudioWorkQueue workQueue,
                Action<string, bool> log)
            {
                this.snapshot = snapshot;
                this.accessor = accessor;
                this.notificationSource = notificationSource;
                this.workQueue = workQueue;
                this.log = log;
                desiredDefaults = snapshot.DefaultEndpointIds.ToDictionary(
                    pair => pair.Key, pair => pair.Value);
                sessionEndpointIds = Flows.ToDictionary(flow => flow,
                    _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            public void Start()
            {
                IDisposable newRegistration;
                try
                {
                    newRegistration = notificationSource.Subscribe(QueueReconcile) ??
                        throw new InvalidOperationException(
                            "The audio notification source returned no registration.");
                }
                catch (Exception ex)
                {
                    log($"[native] Could not monitor Windows audio defaults: {ex.Message}",
                        true);
                    throw new InvalidOperationException(
                        "Native Mode cannot start without Windows audio-default " +
                        "change monitoring.", ex);
                }

                bool discardRegistration;
                lock (workGate)
                {
                    discardRegistration = disposed ||
                        Volatile.Read(ref acceptingNotifications) == 0;
                    if (!discardRegistration)
                        registration = newRegistration;
                }

                if (discardRegistration)
                {
                    try
                    {
                        newRegistration.Dispose();
                    }
                    catch (Exception ex)
                    {
                        log($"[native] Could not stop monitoring Windows audio defaults: " +
                            ex.Message, true);
                    }
                }
            }

            public void ReconcileNow()
            {
                lock (workGate)
                {
                    if (disposed || Volatile.Read(ref acceptingNotifications) == 0)
                        return;
                    TryReconcileCore();
                }
            }

            public void Stop(bool restoreDefaultsNow)
            {
                Interlocked.Exchange(ref acceptingNotifications, 0);

                IDisposable oldRegistration;
                lock (workGate)
                {
                    oldRegistration = registration;
                    registration = null;
                }

                try
                {
                    oldRegistration?.Dispose();
                }
                catch (Exception ex)
                {
                    log($"[native] Could not stop monitoring Windows audio defaults: " +
                        ex.Message, true);
                }

                lock (workGate)
                {
                    if (disposed)
                        return;
                    if (restoreDefaultsNow)
                        TryReconcileCore();
                    disposed = true;
                }
            }

            private void QueueReconcile()
            {
                if (Volatile.Read(ref acceptingNotifications) == 0)
                    return;

                try
                {
                    workQueue.Enqueue(ProcessQueuedReconcile);
                }
                catch (Exception ex)
                {
                    // Never let a managed exception escape the COM callback.
                    log($"[native] Could not queue Windows audio default monitoring: " +
                        ex.Message, true);
                }
            }

            private void ProcessQueuedReconcile()
            {
                lock (workGate)
                {
                    if (disposed || Volatile.Read(ref acceptingNotifications) == 0)
                        return;
                    TryReconcileCore();
                }
            }

            private void TryReconcileCore()
            {
                try
                {
                    ReconcileCore();
                }
                catch (Exception ex)
                {
                    // Establishing the snapshot and notification subscription is
                    // mandatory. After that boundary, an individual policy call
                    // remains retryable and must not escape a COM callback.
                    log($"[native] Could not preserve Windows audio defaults: " +
                        ex.Message, true);
                }
            }

            private void ReconcileCore()
            {
                bool restoredAny = false;

                foreach (NativeModeAudioFlow flow in Flows)
                {
                    IReadOnlyList<NativeModeAudioEndpoint> activeEndpoints;
                    try
                    {
                        activeEndpoints = accessor.GetActiveEndpoints(flow);
                    }
                    catch (Exception ex)
                    {
                        log($"[native] Could not enumerate {flow.ToString().ToLowerInvariant()} " +
                            $"audio endpoints: {ex.Message}", true);
                        continue;
                    }

                    HashSet<string> activeEndpointIds = activeEndpoints
                        .Select(endpoint => endpoint.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> previouslyActive =
                        snapshot.ActiveEndpointIds.TryGetValue(flow, out HashSet<string> ids)
                            ? ids
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> newlyRecognizedSessionEndpoints = activeEndpoints
                        .Where(endpoint => !previouslyActive.Contains(endpoint.Id) &&
                            IsDualSenseEndpoint(endpoint))
                        .Select(endpoint => endpoint.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> knownSessionEndpoints = sessionEndpointIds[flow];
                    knownSessionEndpoints.UnionWith(newlyRecognizedSessionEndpoints);

                    foreach (NativeModeAudioRole role in Roles)
                    {
                        var key = (flow, role);
                        if (pendingNewDefaults.TryGetValue(key,
                            out string pendingDefault))
                        {
                            if (knownSessionEndpoints.Contains(pendingDefault) ||
                                !activeEndpointIds.Contains(pendingDefault))
                            {
                                pendingNewDefaults.Remove(key);
                            }
                            else if (knownSessionEndpoints.Count > 0)
                            {
                                // A newly attached endpoint selected before the
                                // virtual pad was identifiable is now known to be
                                // a separate user choice.
                                desiredDefaults[key] = pendingDefault;
                                pendingNewDefaults.Remove(key);
                            }
                        }

                        string currentDefault;
                        try
                        {
                            currentDefault = accessor.GetDefaultEndpointId(flow, role);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (!knownSessionEndpoints.Contains(currentDefault))
                        {
                            // A current, active non-session endpoint is an
                            // intentional user or application choice. Protect it
                            // from any later virtual-pad takeover in this session.
                            NativeModeAudioEndpoint currentEndpoint = activeEndpoints
                                .FirstOrDefault(endpoint => string.Equals(endpoint.Id,
                                    currentDefault, StringComparison.OrdinalIgnoreCase));
                            if (currentEndpoint != null &&
                                (previouslyActive.Contains(currentDefault) ||
                                    knownSessionEndpoints.Count > 0))
                            {
                                desiredDefaults[key] = currentDefault;
                                pendingNewDefaults.Remove(key);
                            }
                            else if (currentEndpoint != null)
                            {
                                // Defer promotion until the session endpoint is
                                // identified. If this candidate is that endpoint,
                                // it will be discarded instead of poisoning the
                                // trusted baseline.
                                pendingNewDefaults[key] = currentDefault;
                            }
                            continue;
                        }

                        if (!desiredDefaults.TryGetValue(key, out string desiredDefault) ||
                            !activeEndpointIds.Contains(desiredDefault) ||
                            knownSessionEndpoints.Contains(desiredDefault) ||
                            string.Equals(currentDefault, desiredDefault,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            // Close the user-choice race between the first read
                            // and PolicyConfig. A change to any other active
                            // endpoint becomes the new protected baseline.
                            string latestDefault = accessor.GetDefaultEndpointId(
                                flow, role);
                            if (!knownSessionEndpoints.Contains(latestDefault))
                            {
                                if (activeEndpointIds.Contains(latestDefault))
                                    desiredDefaults[key] = latestDefault;
                                continue;
                            }

                            // The setter is never called from an
                            // IMMNotificationClient callback and only replaces
                            // this session's virtual endpoint.
                            accessor.SetDefaultEndpoint(desiredDefault, role);
                            restoredAny = true;
                        }
                        catch (Exception ex)
                        {
                            log($"[native] Could not restore the " +
                                $"{flow.ToString().ToLowerInvariant()} audio default for " +
                                $"{role.ToString().ToLowerInvariant()}: {ex.Message}", true);
                        }
                    }
                }

                if (restoredAny)
                {
                    log("[native] Kept Windows audio on the selected non-virtual " +
                        "endpoints while the virtual controller is attached.", false);
                }
            }

            private static bool IsDualSenseEndpoint(NativeModeAudioEndpoint endpoint) =>
                endpoint.FriendlyName?.Contains(DualSenseEndpointMarker,
                    StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    internal sealed class ThreadPoolNativeModeAudioWorkQueue :
        INativeModeAudioWorkQueue
    {
        public void Enqueue(Action work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));
            ThreadPool.QueueUserWorkItem(state => ((Action)state)(), work);
        }
    }

    internal sealed class WindowsNativeModeAudioNotificationSource :
        INativeModeAudioNotificationSource
    {
        public IDisposable Subscribe(Action endpointOrDefaultChanged) =>
            new Registration(endpointOrDefaultChanged);

        private sealed class Registration : IDisposable
        {
            private readonly MMDeviceEnumerator enumerator;
            private readonly NotificationClient client;
            private int disposed;

            public Registration(Action endpointOrDefaultChanged)
            {
                if (endpointOrDefaultChanged == null)
                    throw new ArgumentNullException(nameof(endpointOrDefaultChanged));

                enumerator = new MMDeviceEnumerator();
                client = new NotificationClient(endpointOrDefaultChanged);
                try
                {
                    int result = enumerator.RegisterEndpointNotificationCallback(client);
                    Marshal.ThrowExceptionForHR(result);
                }
                catch
                {
                    enumerator.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                try
                {
                    int result = enumerator.UnregisterEndpointNotificationCallback(client);
                    Marshal.ThrowExceptionForHR(result);
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
        }

        private sealed class NotificationClient : IMMNotificationClient
        {
            private readonly Action changed;

            public NotificationClient(Action changed)
            {
                this.changed = changed;
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
                Notify();

            public void OnDeviceAdded(string pwstrDeviceId) => Notify();

            public void OnDeviceRemoved(string deviceId) => Notify();

            public void OnDefaultDeviceChanged(DataFlow flow, Role role,
                string defaultDeviceId) => Notify();

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
                Notify();

            private void Notify()
            {
                try
                {
                    changed();
                }
                catch
                {
                    // COM notification methods must never propagate managed errors.
                }
            }
        }
    }

    internal sealed class WindowsNativeModeAudioEndpointAccessor :
        INativeModeAudioEndpointAccessor
    {
        private static readonly PropertyKey DeviceContainerIdProperty =
            new PropertyKey(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

        public IReadOnlyList<NativeModeAudioEndpoint> GetActiveEndpoints(
            NativeModeAudioFlow flow)
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
                (DataFlow)(int)flow, DeviceState.Active);
            var result = new List<NativeModeAudioEndpoint>(devices.Count);
            foreach (MMDevice device in devices)
            {
                using (device)
                {
                    result.Add(new NativeModeAudioEndpoint(device.ID,
                        device.FriendlyName ?? string.Empty,
                        TryGetStringProperty(device,
                            PropertyKeys.PKEY_Device_InstanceId),
                        TryGetGuidProperty(device, DeviceContainerIdProperty)));
                }
            }
            return result;
        }

        private static string TryGetStringProperty(MMDevice device,
            PropertyKey propertyKey)
        {
            try
            {
                return device.Properties.Contains(propertyKey)
                    ? device.Properties[propertyKey]?.Value as string
                    : null;
            }
            catch
            {
                // Identity correlation has a parent/container fallback. A
                // missing optional endpoint property must not abort enumeration.
                return null;
            }
        }

        private static Guid? TryGetGuidProperty(MMDevice device,
            PropertyKey propertyKey)
        {
            try
            {
                if (!device.Properties.Contains(propertyKey))
                    return null;

                object value = device.Properties[propertyKey]?.Value;
                if (value is Guid guid)
                    return guid;
                return value is string text && Guid.TryParse(text, out guid)
                    ? guid
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public string GetDefaultEndpointId(NativeModeAudioFlow flow,
            NativeModeAudioRole role)
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(
                (DataFlow)(int)flow, (Role)(int)role);
            return device.ID;
        }

        public void SetDefaultEndpoint(string endpointId, NativeModeAudioRole role)
        {
            IPolicyConfig policy = null;
            try
            {
                policy = (IPolicyConfig)(object)new PolicyConfigClient();
                int result = policy.SetDefaultEndpoint(endpointId, (int)role);
                Marshal.ThrowExceptionForHR(result);
            }
            finally
            {
                if (policy != null && Marshal.IsComObject(policy))
                    Marshal.FinalReleaseComObject(policy);
            }
        }

        [ComImport]
        [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private sealed class PolicyConfigClient
        {
        }

        // Windows exposes no public setter beside its settings UI. This stable
        // shell interface is isolated here so a COM failure remains a harmless
        // best-effort warning rather than a native-mode startup failure.
        [ComImport]
        [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig]
            int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                out IntPtr format);
            [PreserveSig]
            int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                int defaultFormat, out IntPtr format);
            [PreserveSig]
            int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig]
            int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig]
            int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                int defaultPeriod, out long period, out long minimumPeriod);
            [PreserveSig]
            int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                ref long period);
            [PreserveSig]
            int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                IntPtr mode);
            [PreserveSig]
            int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                IntPtr mode);
            [PreserveSig]
            int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                IntPtr key, IntPtr value);
            [PreserveSig]
            int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                IntPtr key, IntPtr value);
            [PreserveSig]
            int SetDefaultEndpoint(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig]
            int SetEndpointVisibility(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
        }
    }
}
