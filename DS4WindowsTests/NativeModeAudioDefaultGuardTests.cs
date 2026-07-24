using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeAudioDefaultGuardTests
{
    private static readonly NativeModeAudioRole[] Roles =
    {
        NativeModeAudioRole.Console,
        NativeModeAudioRole.Multimedia,
        NativeModeAudioRole.Communications,
    };

    [TestMethod]
    public void Notification_QueuesWorkBeforeRestoringEveryRenderAndCaptureRole()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        NativeModeAudioDefaultsSnapshot snapshot = guard.Capture();
        guard.BeginSession(snapshot);

        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.AddEndpoint(NativeModeAudioFlow.Capture, "virtual-capture",
            "Headset Microphone (DualSense Wireless Controller)");
        foreach (NativeModeAudioRole role in Roles)
        {
            accessor.Defaults[(NativeModeAudioFlow.Render, role)] = "virtual-render";
            accessor.Defaults[(NativeModeAudioFlow.Capture, role)] = "virtual-capture";
        }

        notifications.Signal();

        Assert.AreEqual(1, queue.Count);
        Assert.AreEqual(0, accessor.SetAttempts);
        queue.RunAll();

        foreach (NativeModeAudioRole role in Roles)
        {
            Assert.AreEqual(RenderDefault(role),
                accessor.Defaults[(NativeModeAudioFlow.Render, role)]);
            Assert.AreEqual(CaptureDefault(role),
                accessor.Defaults[(NativeModeAudioFlow.Capture, role)]);
        }
        Assert.AreEqual(6, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void Session_RestoresDefaultChangeLongAfterLegacyFiveSecondWindow()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());

        // Reconciliation before endpoint arrival must not end the session.
        guard.ReconcileNow();
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "late-virtual-render",
            "Speakers (10- DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Multimedia)] = "late-virtual-render";

        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Multimedia)]);
        Assert.AreEqual(1, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void ReconcileNow_CoversAttachNotificationRegistrationRace()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";

        guard.ReconcileNow();

        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(1, accessor.SetAttempts);
        Assert.AreEqual(0, queue.Count);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void EndSession_FinalReconcileClosesLastCallbackBlindSpot()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";

        // No callback is delivered, like a change in the old final delay.
        guard.EndSession(restoreDefaultsNow: true);

        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(1, accessor.SetAttempts);
        Assert.AreEqual(1, notifications.DisposeCount);
    }

    [TestMethod]
    public void EndSession_RemembersVirtualEndpointAfterItBecomesInactive()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        guard.ReconcileNow();

        // Model teardown after the endpoint state changed away from Active
        // but while the policy getter still reports its ID.
        accessor.RemoveEndpoint(NativeModeAudioFlow.Render, "virtual-render");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";
        guard.EndSession(restoreDefaultsNow: true);

        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(1, accessor.SetAttempts);
    }

    [TestMethod]
    public void IntentionalEndpointChangeBecomesTheProtectedBaseline()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "new-headset",
            "New USB Headset");
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");

        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "new-headset";
        notifications.Signal();
        queue.RunAll();
        Assert.AreEqual(0, accessor.SetAttempts);

        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";
        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("new-headset", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual("new-headset", accessor.SetCalls.Single().EndpointId);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void NewUserEndpointSelectedBeforeVirtualIdentificationIsPreserved()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "new-headset",
            "New USB Headset");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "new-headset";

        notifications.Signal();
        queue.RunAll();
        Assert.AreEqual(0, accessor.SetAttempts);

        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";
        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("new-headset", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual("new-headset", accessor.SetCalls.Single().EndpointId);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void PreexistingDualSenseChoiceIsNotMistakenForSessionEndpoint()
    {
        var accessor = CreateAccessor();
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "existing-wired-pad",
            "Speakers (DualSense Wireless Controller)");
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());

        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "existing-wired-pad";
        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("existing-wired-pad", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(0, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void InactiveDesiredEndpointIsNeverRestored()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.RemoveEndpoint(NativeModeAudioFlow.Render, "sonar-gaming");
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";

        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("virtual-render", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(0, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void GenericallyNamedNewEndpointCannotPoisonTheProtectedBaseline()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "USB Audio Device");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";

        notifications.Signal();
        queue.RunAll();
        Assert.AreEqual(0, accessor.SetAttempts);

        accessor.RemoveEndpoint(NativeModeAudioFlow.Render, "virtual-render");
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(1, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void InactiveIntentionalChoiceDoesNotReplaceTheProtectedBaseline()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");

        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "missing-headset";
        notifications.Signal();
        queue.RunAll();
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";
        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(1, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void UserChoiceBetweenQueuedReadAndSetterIsNotOverwritten()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "new-headset",
            "New USB Headset");
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";
        int consoleReads = 0;
        accessor.BeforeGetDefault = (flow, role) =>
        {
            if (flow == NativeModeAudioFlow.Render &&
                role == NativeModeAudioRole.Console && ++consoleReads == 2)
            {
                accessor.Defaults[(flow, role)] = "new-headset";
            }
        };

        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual("new-headset", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(0, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void QueuedAndLateCallbacksCannotRunPolicyAfterSessionEnds()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var guard = CreateGuard(accessor, notifications, queue);
        guard.BeginSession(guard.Capture());
        Action capturedCallback = notifications.LastCallback;
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";
        notifications.Signal();
        Assert.AreEqual(1, queue.Count);

        guard.EndSession(restoreDefaultsNow: false);
        capturedCallback();
        Assert.AreEqual(1, queue.Count);
        queue.RunAll();

        Assert.AreEqual("virtual-render", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(0, accessor.SetAttempts);
        Assert.AreEqual(1, notifications.DisposeCount);
    }

    [TestMethod]
    public void SetterFailureIsLoggedAndALaterNotificationCanRetry()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var warnings = new List<string>();
        var guard = CreateGuard(accessor, notifications, queue,
            (message, warning) =>
            {
                if (warning)
                    warnings.Add(message);
            });
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";
        accessor.ThrowOnSet = true;

        notifications.Signal();
        queue.RunAll();

        Assert.AreEqual(1, accessor.SetAttempts);
        Assert.AreEqual(1, warnings.Count(message =>
            message.Contains("Could not restore", StringComparison.Ordinal)));

        accessor.ThrowOnSet = false;
        notifications.Signal();
        queue.RunAll();
        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(2, accessor.SetAttempts);
        guard.EndSession(restoreDefaultsNow: false);
    }

    [TestMethod]
    public void NotificationRegistrationFailureAbortsAndClearsPartialSession()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource { ThrowOnSubscribe = true };
        var queue = new ManualWorkQueue();
        var warnings = new List<string>();
        var guard = CreateGuard(accessor, notifications, queue,
            (message, warning) =>
            {
                if (warning)
                    warnings.Add(message);
            });

        NativeModeAudioDefaultsSnapshot snapshot = guard.Capture();
        InvalidOperationException failure = Assert.ThrowsException<
            InvalidOperationException>(() => guard.BeginSession(snapshot));

        StringAssert.Contains(failure.Message, "cannot start");
        Assert.AreEqual(1, warnings.Count(message =>
            message.Contains("Could not monitor", StringComparison.Ordinal)));

        // A clean retry proves the failed Session was unpublished and stopped.
        notifications.ThrowOnSubscribe = false;
        guard.BeginSession(snapshot);
        guard.EndSession(restoreDefaultsNow: false);
        Assert.AreEqual(1, notifications.DisposeCount);
    }

    [TestMethod]
    public void UnregisterFailureIsNonfatalAndFinalReconcileStillRuns()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource { ThrowOnDispose = true };
        var queue = new ManualWorkQueue();
        var warnings = new List<string>();
        var guard = CreateGuard(accessor, notifications, queue,
            (message, warning) =>
            {
                if (warning)
                    warnings.Add(message);
            });
        guard.BeginSession(guard.Capture());
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "virtual-render",
            "Speakers (DualSense Wireless Controller)");
        accessor.Defaults[(NativeModeAudioFlow.Render,
            NativeModeAudioRole.Console)] = "virtual-render";

        guard.EndSession(restoreDefaultsNow: true);

        Assert.AreEqual("sonar-gaming", accessor.Defaults[(
            NativeModeAudioFlow.Render, NativeModeAudioRole.Console)]);
        Assert.AreEqual(1, notifications.DisposeCount);
        Assert.AreEqual(1, warnings.Count(message =>
            message.Contains("Could not stop monitoring", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Capture_UnexpectedAudioApiFailureAbortsStartup()
    {
        var accessor = CreateAccessor();
        var notifications = new FakeNotificationSource();
        var queue = new ManualWorkQueue();
        var warnings = new List<string>();
        var guard = CreateGuard(accessor, notifications, queue,
            (message, warning) =>
            {
                if (warning)
                    warnings.Add(message);
            });
        accessor.ThrowOnEnumeration = true;

        InvalidOperationException failure = Assert.ThrowsException<
            InvalidOperationException>(() => guard.Capture());

        StringAssert.Contains(failure.Message, "cannot start");
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "Could not snapshot");
    }

    [TestMethod]
    public void BeginSession_RejectsMissingSnapshot()
    {
        var guard = CreateGuard(CreateAccessor(), new FakeNotificationSource(),
            new ManualWorkQueue());

        Assert.ThrowsException<ArgumentNullException>(() =>
            guard.BeginSession(null));
    }

    private static NativeModeAudioDefaultGuard CreateGuard(FakeAccessor accessor,
        FakeNotificationSource notifications, ManualWorkQueue queue,
        Action<string, bool> log = null) =>
        new NativeModeAudioDefaultGuard(accessor, notifications, queue,
            log ?? ((_, _) => { }));

    private static FakeAccessor CreateAccessor()
    {
        var accessor = new FakeAccessor();
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "sonar-gaming",
            "SteelSeries Sonar - Gaming");
        accessor.AddEndpoint(NativeModeAudioFlow.Render, "sonar-chat",
            "SteelSeries Sonar - Chat");
        accessor.AddEndpoint(NativeModeAudioFlow.Capture, "sonar-mic",
            "SteelSeries Sonar - Microphone");
        foreach (NativeModeAudioRole role in Roles)
        {
            accessor.Defaults[(NativeModeAudioFlow.Render, role)] = RenderDefault(role);
            accessor.Defaults[(NativeModeAudioFlow.Capture, role)] = CaptureDefault(role);
        }
        return accessor;
    }

    private static string RenderDefault(NativeModeAudioRole role) =>
        role == NativeModeAudioRole.Communications ? "sonar-chat" : "sonar-gaming";

    private static string CaptureDefault(NativeModeAudioRole role) => "sonar-mic";

    private sealed class ManualWorkQueue : INativeModeAudioWorkQueue
    {
        private readonly Queue<Action> work = new();

        public int Count => work.Count;

        public void Enqueue(Action action) => work.Enqueue(action);

        public void RunAll()
        {
            while (work.Count > 0)
                work.Dequeue()();
        }
    }

    private sealed class FakeNotificationSource : INativeModeAudioNotificationSource
    {
        private Action currentCallback;

        public bool ThrowOnSubscribe { get; set; }
        public bool ThrowOnDispose { get; set; }
        public int DisposeCount { get; private set; }
        public Action LastCallback { get; private set; }

        public IDisposable Subscribe(Action endpointOrDefaultChanged)
        {
            if (ThrowOnSubscribe)
                throw new InvalidOperationException("registration failed");

            currentCallback = endpointOrDefaultChanged;
            LastCallback = endpointOrDefaultChanged;
            return new CallbackRegistration(this);
        }

        public void Signal() => currentCallback?.Invoke();

        private sealed class CallbackRegistration : IDisposable
        {
            private readonly FakeNotificationSource owner;
            private bool disposed;

            public CallbackRegistration(FakeNotificationSource owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                owner.currentCallback = null;
                owner.DisposeCount++;
                if (owner.ThrowOnDispose)
                    throw new InvalidOperationException("unregistration failed");
            }
        }
    }

    private sealed class FakeAccessor : INativeModeAudioEndpointAccessor
    {
        public Dictionary<NativeModeAudioFlow, List<NativeModeAudioEndpoint>> Endpoints
            { get; } = new()
            {
                [NativeModeAudioFlow.Render] = new(),
                [NativeModeAudioFlow.Capture] = new(),
            };

        public Dictionary<(NativeModeAudioFlow Flow, NativeModeAudioRole Role), string>
            Defaults { get; } = new();

        public List<(string EndpointId, NativeModeAudioRole Role)> SetCalls
            { get; } = new();

        public bool ThrowOnSet { get; set; }
        public bool ThrowOnEnumeration { get; set; }
        public int SetAttempts { get; private set; }
        public Action<NativeModeAudioFlow, NativeModeAudioRole> BeforeGetDefault
            { get; set; }

        public IReadOnlyList<NativeModeAudioEndpoint> GetActiveEndpoints(
            NativeModeAudioFlow flow)
        {
            if (ThrowOnEnumeration)
                throw new ApplicationException("enumeration failed");
            return Endpoints[flow];
        }

        public string GetDefaultEndpointId(NativeModeAudioFlow flow,
            NativeModeAudioRole role)
        {
            BeforeGetDefault?.Invoke(flow, role);
            return Defaults[(flow, role)];
        }

        public void SetDefaultEndpoint(string endpointId, NativeModeAudioRole role)
        {
            SetAttempts++;
            if (ThrowOnSet)
                throw new InvalidOperationException("policy setter failed");

            NativeModeAudioFlow flow = Endpoints
                .Single(pair => pair.Value.Any(endpoint => endpoint.Id == endpointId)).Key;
            Defaults[(flow, role)] = endpointId;
            SetCalls.Add((endpointId, role));
        }

        public void AddEndpoint(NativeModeAudioFlow flow, string id, string name) =>
            Endpoints[flow].Add(new NativeModeAudioEndpoint(id, name));

        public void RemoveEndpoint(NativeModeAudioFlow flow, string id) =>
            Endpoints[flow].RemoveAll(endpoint => endpoint.Id == id);
    }
}
