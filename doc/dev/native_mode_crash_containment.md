# Native Mode parent/helper crash containment

Status: **implementation under development; offline validation only and not
live safety-validated.** This document describes work toward the second
production gate in [the Native Mode design note](native_dualsense_mode.md). It
does not close that gate, repair usbip-win2, or authorize live fault-injection
testing.

The design protects the ordering between three lifetimes:

1. the virtual wired DualSense and its USB Audio endpoint;
2. the USB/IP helper that supplies input and ISO traffic; and
3. a silent shared-mode render client that prevents an uncontrolled speaker-pin
   close while the virtual device is present.

The confirmed crash path involved an audio endpoint purge racing successful ISO
request completions in usbip2_ude.sys. The detailed dumps and WinDbg records
remain private. No dump or full debugger log is part of this repository.

## 1. Baseline gap

The published draft baseline made DS4Windows the only render-keepalive owner,
while
VirtualDualSenseUsbip.exe owns the USB/IP socket, ISO pacing, and physical
Bluetooth controller handle. Normal teardown starts the parent's deferred
keepalive release, terminates the helper, waits for the exact virtual device to
disappear, and only then releases the render client.

That ordering has been stable in controlled testing, but it depends on managed
DS4Windows cleanup running. A hard parent termination can:

- close the only render keepalive immediately;
- leave the helper and USB/IP session alive; and
- expose the same pin-close-versus-ISO condition that the keepalive was added to
  avoid.

Application exit, session ending, and some managed exception paths attempt
synchronous cleanup. They are best-effort protections, not crash containment:
fail-fast, access violations, stack exhaustion, forced termination, deadlock,
or a failure inside the cleanup path can bypass them.

Windows does not automatically end a process merely because the process that
created it exits. The helper therefore needs an explicit parent-lifetime
channel.

## 2. Implementation status

The working branch now implements the single-process-failure design described
below:

- the existing DS4Windows keepalive remains active;
- the helper opens an independent keepalive on the exact virtual endpoint and
  reports readiness before DS4Windows publishes Attached;
- protocol v2 redirects standard input, recognizes one Stop command, and treats
  pipe EOF as parent death;
- both signals enter the same cooperative helper cancellation path;
- helper/session shutdown and exact removal happen before either side releases
  its final pin protection; and
- transient render-endpoint discovery failures are retried with rate-limited,
  capped backoff while the shutdown barrier remains fail-closed;
- a timed-out or canceled elevation request remains an explicit lifetime
  barrier until the already-dispatched command is confirmed terminal; and
- the parent waits for cooperative exit and leaves a timed-out helper running
  rather than force-killing the process that may hold the final lease.

If a successful import reply never produces a discoverable render endpoint,
the helper intentionally remains alive and Stop reports incomplete. That is a
fail-closed containment state, not proof that detach completed; recovery still
requires the separately specified exact-device reconciliation work.

This implementation has only offline validation at this stage. A present-only
exact-device startup preflight now prevents a second attach when the virtual
device is present or the probe cannot prove absence. Automatic detach,
session-token ownership, persisted audio recovery, approved live parent/helper
failure drills, and a fixed upstream kernel driver remain open work.

## 3. Safety invariants

1. At least one healthy keepalive lease must remain active until both the exact
   virtual parent and its tracked render endpoint are absent.
2. A normal or parent-death stop must stop new ISO completions and close the
   USB/IP session before releasing the last keepalive.
3. Failure to prove removal must retain a keepalive and block reuse. A timeout
   is not permission to force-kill the process holding the final lease.
4. The parent and helper must each be able to protect a failure of the other.
5. Parent-death handling must not require elevation or a persistent privileged
   service, task, or driver.
6. Cleanup commands and status must refer to one session identity; process names
   or PIDs alone are not sufficient ownership proof.

These invariants deliberately preserve the validated existing rule: the render
pin is released only after endpoint and device removal, never merely because an
alternate setting changed or a stop timer expired.

## 4. Implemented architecture: redundant keepalive leases

### 4.1 Parent lease

Keep the existing NativeModeRenderKeepalive in DS4Windows. It remains the safety
lease when the helper exits or crashes unexpectedly. Its current fail-closed
endpoint and present-device probes remain authoritative for releasing parent
protections.

### 4.2 Helper lease

The working branch adds an independent silent shared-mode render lease to
VirtualDualSenseUsbip.exe. After attach, the helper locates only the render
endpoint owned by the exact virtual parent, starts the lease, proves that its
audio clock advances, and reports helper-lease readiness.

DS4Windows must not publish Attached until both leases are ready. If either
lease cannot start, startup enters ordered teardown while the lease that did
start remains held through confirmed removal.

This is intentional redundancy:

- if DS4Windows dies, the helper lease survives long enough to unplug safely;
- if the helper dies, the DS4Windows lease survives long enough for current
  automatic cleanup to confirm removal.

Offline and controlled hardware tests must prove that either shared-mode client
alone keeps the underlying render pin open after the other client exits. That
property is expected from the shared audio engine but is a required test result,
not an assumption.

### 4.3 Parent-lifetime and stop channel

The helper is launched with redirected standard input. The pipe handle is inherited
from the specific DS4Windows launch and provides both control and lifetime
signaling without PID-reuse ambiguity:

- the protocol-v2 helper accepts one Stop command for normal teardown;
- end-of-file means the parent process closed or died and requests the same
  teardown; and
- unsupported input fails closed and teardown remains idempotent.

Only DS4Windows owns the writable end of this anonymous pipe. If a named pipe is
used instead, it requires a per-session random identifier, a current-user ACL,
and mutual session-token validation.

A PID may be recorded for diagnostics, but must not be the authority for session
ownership.

### 4.4 Cooperative helper teardown

Both a Stop command and parent-pipe EOF enter one serialized, idempotent path:

1. Close the listening socket so no second attach or re-attach can enter. Keep
   an existing USB/IP client session alive at this point.
2. Require a healthy helper render lease, or positively confirm that the exact
   virtual parent is already absent *and* no successful import reply can still
   materialize it. After an import reply commits, an early absence observation
   is never sufficient by itself. An inconclusive probe blocks fail-closed.
3. Cancel ISO pacing and the active USB/IP client session under the existing
   lifecycle gates. A retired ISO generation must emit no later successful
   completion. Socket closure drives virtual-device removal while the helper
   keepalive remains active.
4. Poll the exact present virtual parent and the tracked render endpoint. Do not
   release either keepalive merely because the socket-close request was issued.
5. After both are confirmed absent, release the helper keepalive and exit.
6. DS4Windows independently completes the same removal checks before releasing
   its keepalive, audio-default guard, controller suppression, and rescan block.

The baseline NativeModeManager.StopAsync used Process.Kill. The working branch
replaces it with the cooperative command plus a bounded wait. If the wait
expires while the device is present, Stop reports incomplete and retains
protection; it does not kill the holder of the final lease.

### 4.5 Late elevation decisions

Windows can keep Process.Start with the runas verb inside the UAC broker after
the caller's startup timeout or cancellation has fired. A timeout therefore is
not proof that usbip.exe was never launched and is not a safe teardown
boundary.

The attach broker returns the still-running command task with that failure.
DS4Windows records it as a fail-closed barrier and keeps the helper, both render
leases, audio-default guard, controller suppression, and rescan block active.
Manual Stop and automatic fault cleanup do not bypass the barrier. The process
runner tries to terminate the short-lived command client after cancellation,
but does not mark its task terminal until any process that was started is
confirmed exited. Once the task is terminal, DS4Windows runs the normal ordered
helper stop and rechecks exact endpoint and parent absence before releasing
protections.

If DS4Windows itself exits while the UAC decision is still pending, helper pipe
EOF closes the server through the same atomic import gate. A late attach can
either finish against the still-protected helper and then be removed, or fail
because the listener has stopped; it cannot authorize parent-side early
release.

## 5. Why there is no kill-on-close job

[Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
that JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE terminates associated processes as soon
as the last job handle closes. It provides no grace period in which the
helper's parent-death watcher is guaranteed to run.

Killing a helper that owns an audio client would also close its render lease and
USB/IP socket concurrently. That recreates the ordering ambiguity this design is
meant to remove. Native Mode therefore must not use a parent-owned kill-on-close
job or Process.Kill as its containment fallback while the virtual device is
present.

If future architecture separates the USB/IP worker from a dedicated supervisor,
the supervisor could own a worker-only job while the supervisor continues
holding a keepalive. That is a different, larger design and is not proposed as
implemented here.

## 6. Failure-state matrix

| Event | Lease that survives | Required response | Residual status |
|---|---|---|---|
| Normal Stop or app exit | Both until removal | Stop command, cancel ISO, close socket, confirm removal, release both | Implemented; offline validation only |
| DS4Windows hard termination | Helper lease | Pipe EOF drives the same cooperative teardown | Addresses the dominant parent-death gap after testing |
| Helper unexpected exit | Parent lease | Existing exit monitor faults the session; parent confirms removal before release | Requires tests with ISO active |
| One keepalive fails | Other lease | Fault the session and teardown while the other lease remains | Requires independent lease-health reporting |
| Cooperative helper teardown wedges | Parent and/or helper lease remains | Report incomplete, block reuse, retain protection | No force-kill while attached |
| UAC decision outlives startup timeout | Both leases and parent protections | Keep command barrier, then perform ordered stop after confirmed command exit | Implemented; offline validation only |
| Parent and helper fail simultaneously | No guaranteed lease | Socket and pins can close without ordering | Still a residual risk; blocker is not fully closed |
| Host power loss | No live processes or in-flight software after reboot | Present-only startup inspection; block attach on confirmed presence or probe failure | Duplicate attach is guarded; automatic reconciliation is not implemented |

Redundancy materially improves single-process failure containment. It cannot
prove safety for simultaneous process loss or compensate for an unfixed kernel
driver.

## 7. Startup duplicate-attach guard and remaining reconciliation

The parent now performs a present-only probe for the exact virtual device before
it releases the physical controller or begins attach. Confirmed presence, or a
probe failure that cannot prove absence, blocks startup and sets Native Mode to
SetupRequired. Historical phantom/problem devnodes do not count as present.

This fail-closed preflight prevents a second attach. It does not identify the
owner of an existing session, detach one automatically, or restore audio state
left by an interrupted prior session. Those are still additional recovery work.

Before attach, DS4Windows should atomically persist a minimal recovery record
under the current user's protected application-data directory:

- a random Native Mode session identifier;
- the expected virtual instance identity;
- the trusted non-virtual default endpoint IDs captured before attach; and
- enough state to distinguish attach-started from removal-confirmed.

The record is cleared only after confirmed removal and audio-default
reconciliation. Restoration must use the existing guard rule: replace a
session-owned virtual default, but never overwrite a later active non-virtual
choice made by the user.

Full recovery should then:

1. Reuse the exact present-only SetupAPI probe already enforced before attach.
2. Identify a surviving helper only through the session identifier and control
   channel, not by executable name.
3. If ownership or removal cannot be proven, block another Native Mode start and
   present recovery instructions.
4. An automatic elevated detach must not be added until code can map the exact
   synthetic attach identity to the correct usbip-win2 port, validate that
   ownership, execute one fixed command, and confirm present-device removal.

The current broker implements attach and legacy-task removal, not this exact
detach/recovery protocol.

## 8. Implementation boundaries

Implemented working-branch areas:

- NativeModeManager: redirected input, cooperative Stop, EOF semantics, helper
  readiness, and bounded fail-closed waits;
- VirtualDualSenseUsbip: parent-channel watcher, helper keepalive, serialized
  session cancellation, and exact removal probes;
- ControlService: require both lease-ready signals before Attached and retain
  existing parent protections during all failures, including a late UAC
  decision that outlives startup cancellation;
- Device Options UI: project Stopped plus retained session protections as
  cleanup pending with Stop available, instead of claiming teardown completed;
- parent start path: present-only exact-device preflight before controller
  release; confirmed presence or probe failure blocks attach and reports
  SetupRequired; and
- NativeModeManager: protocol-v2 readiness/log classification for the
  cooperative-stop and helper-lease handshake.

Remaining implementation areas:

- audio recovery: atomic persisted snapshot with conservative restoration;
- session-token ownership and automatic exact stale-device detach
  reconciliation; and
- a recovery-required UI with exact stale-session ownership and cleanup
  instructions.

No implementation should log the physical HID path, controller address,
session token, or endpoint IDs at normal verbosity.

## 9. Test plan

Offline tests required before any hardware exercise:

- Stop command and pipe EOF each invoke exactly one cooperative teardown.
- A stale or malformed command cannot control another session.
- Stop-before-import rejects that import, refuses a queued second attach, and
  lets an idle listener finish cleanly.
- A successful import reply remains safety-relevant after client EOF, so
  delayed PnP materialization cannot bypass the helper-render barrier.
- Transient render-endpoint enumeration failures retry with capped backoff and
  can still acquire the helper lease without emitting a terminal failure.
- ISO generations emit no successful completion after retirement.
- Teardown ordering is socket close, exact removal confirmation, then helper
  lease release.
- Parent lease remains after simulated helper exit; helper lease remains after
  simulated parent EOF.
- Either individual lease is sufficient in the endpoint abstraction tests.
- A timeout retains protection and never calls Process.Kill.
- A timed-out or canceled elevation command prevents recovery until its real
  command task is terminal, after which ordered recovery runs.
- Present-only preflight ignores phantom devnodes; confirmed presence or probe
  failure blocks attach and reports SetupRequired.
- Persisted audio recovery never replaces a later non-virtual user choice.

Controlled live tests remain separately approval-gated:

- normal Stop with active game audio;
- forced DS4Windows termination with the helper lease confirmed ready;
- controlled helper termination with the parent lease confirmed ready; and
- restart after an intentionally incomplete but quiescent session.

Do not combine the first containment validation with rapid pin cycling, Driver
Verifier, an unsigned driver, or whole-dump analysis.

## 10. Definition of done

This implementation does **not** close production gate 2 by being documented or
passing offline tests.
The gate can be reconsidered only after:

- the redundant leases and cooperative channel are implemented;
- all offline ordering and failure tests pass;
- explicitly approved hardware fault drills demonstrate removal and recovery;
- no path force-kills the final lease holder while the device is present; and
- maintainers accept the remaining simultaneous-failure risk in conjunction
  with the separate kernel-driver policy.

Until then, full Native Mode remains experimental and off by default.
