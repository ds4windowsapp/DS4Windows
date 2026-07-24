# Native Mode driver risk, validation, and lifecycle policy

Status: **partially implemented.** The §4 validation model (package enumeration,
signature/trust verification, and the supported-release manifest) is now
implemented and fail-closed; see §4.2 and the driver validation gate section of
[the Native Mode design note](native_dualsense_mode.md). The §5 install/repair
and §6 uninstall policies remain proposals. This document addresses work toward
production gates 1 and 3. It does not declare any usbip-win2 release safe or
authorize DS4Windows to install, repair, or remove a kernel driver.

The upstream report for gate 1 has been filed as usbip-win2 issue 181, which
also cross-references usbip-win2 issue 180 — the same pool-corruption signature
on the same driver builds with a non-audio, vendor-class WinUSB trigger. That
supports §1's statement that the observed trigger was ISO audio but the
underlying transfer engine is not audio-exclusive. Gate 1 closes only on a
signed fixed release or an accepted alternative driver strategy.

usbip-win2 is an external BSD-2-Clause dependency. DS4Windows does not bundle
it. The controlled tests used the upstream 0.9.7.8 installer while Secure Boot
and Memory Integrity were enabled.

## 1. Driver risk

The reproduced failure involved usbip2_ude.sys request lifetime during USB
Audio endpoint teardown: an endpoint purge overlapped successful ISO request
completions and Windows later detected kernel heap corruption.

The private investigation also identified request-completion and WSK/MDL
lifetime problems in the common UDE transport machinery. The observed trigger
was ISO audio, but the underlying transfer engine is not exclusive to ISO.
Removing audio therefore reduces exposure to the known trigger; it does not
prove the remaining HID path or the kernel driver safe.

No successful game session or user-mode test can retire a kernel use-after-free
risk. Full Native Mode remains experimental until maintainers accept a signed
fixed driver or a different driver architecture.

The crash dumps and full debugger logs are deliberately not included in this
repository. A source-level issue draft and an uncompiled proof-of-concept patch
are retained privately for review. They must not be published, signed, installed,
or loaded without explicit owner approval and upstream review.

## 2. Risk-reduction options

### Option A: upstream signed fix

Submit a redacted source-level report to usbip-win2 and request maintainer review
plus a properly signed diagnostic or release build. Public material should
describe the request-lifetime evidence without uploading dumps, full WinDbg
logs, machine identifiers, or unrelated device history.

Only an upstream-reviewed, appropriately signed build that passes the project's
driver and Native Mode stress plan can become a candidate for the audio tier.
The local proof patch has not been compiled and is not a binary candidate.

This is the preferred durable fix because it addresses the driver rather than
one application trigger.

### Option B: future reduced-risk HID-only experiment

The helper already has an offline-only scaffold that derives a one-interface
HID configuration from the captured descriptors. Production serve currently
rejects every configuration except the composite audio-plus-HID topology.

A future HID-only experiment could expose native controller input and relay the
game's adaptive-trigger blocks while omitting USB Audio interfaces. It would
avoid usbaudio.sys and the reproduced ISO pin-close trigger.

Important limits:

- it would still depend on usbip-win2 and its common kernel transfer machinery;
- it is not proven safe, production-ready, or exempt from driver validation;
- the current helper relays adaptive-trigger HID blocks, not native haptic audio
  or a general rumble substitute;
- the standard DS4Windows Bluetooth haptics streamer is not active while Native
  Mode has released the pad to the helper; and
- there is currently no approved live-validation plan for this topology.

For those reasons HID-only remains a hidden offline scaffold or future
controls-and-triggers experiment. It must not be described as shippable or as
closing the driver blocker.

### Option C: current full-audio posture

Keep composite Native Mode experimental and off by default. The parent
keepalive and strict ISO generation quiescing reduce the reproduced trigger, and
the proposed redundant-lease containment further reduces single-process
failure exposure. None repairs usbip2_ude.sys.

## 3. Observed 0.9.7.8 package identity

The upstream release label is not the same value Windows reports for each
installed driver package. On the controlled test machine, the 0.9.7.8 install
reported:

| Component | Original INF | Provider | Windows DriverVer | Signing observation |
|---|---|---|---|---|
| UDE host controller | usbip2_ude.inf | USBIP-WIN2 | 1.45.29.368 | Valid Microsoft Hardware Compatibility Publisher signature; package reported Attested |
| USB hub filter extension | usbip2_filter.inf | USBIP-WIN2 | 1.45.28.868 | Valid Microsoft Hardware Compatibility Publisher signature; package reported Attested |
| Userspace client | usbip.exe | Cloudyne Systems | ProductVersion 0.9.7.8 | Valid Authenticode signature on the tested file |

The driver-store query reported WHCP Version as unknown. Documentation therefore
uses **Microsoft attestation-signed**, not WHQL-certified.

These values record one tested installation; they are not a permanent allowlist.
A supported release manifest must deliberately map one upstream release to both
driver packages, catalog identities, expected userspace client, architecture,
and accepted hashes or signer policy.

## 4. Required validation model

Validation must complete before DS4Windows releases the physical controller or
requests elevation. It must fail closed with a specific diagnostic.

### 4.1 Enumerate the correct device and packages

Use SetupAPI or Configuration Manager rather than parsing localized pnputil
text.

- Locate the present emulated host controller by hardware ID
  ROOT\USBIP_WIN2\UDE and expected provider, not by a machine-specific instance
  such as ROOT\USB\0002.
- Read the bound INF, provider, driver date/version, service, and catalog
  identity for the UDE host controller.
- Locate and validate the companion usbip2_filter.inf extension package as a
  separate component.
- Confirm the host controller is started and healthy. A display name is useful
  for diagnostics but is not identity.

### 4.2 Validate signatures and the release manifest

- Verify catalog/file trust with the Windows trust APIs and normal chain policy;
  do not accept a package merely because a signer subject string contains
  Microsoft.
- Require both packages to match one supported release-manifest entry. Reject a
  mixed, missing, unknown, developer-signed, test-signed, or revoked pair.
- Validate architecture and the expected service/device bindings.
- Validate the canonical Program Files usbip.exe against the same manifest,
  including filename, resolved path, product version, trust result, and the
  chosen publisher/hash policy.
- Record only non-sensitive component/version results in normal logs.

**Implementation status.** All of the above is implemented. `NativeModeDriverManifest`
holds the single versioned release/tier/signer structure, `NativeModeDriverValidator`
performs the fail-closed matching, `SetupApiDriverPackageInspector` enumerates both
packages through SetupAPI / Configuration Manager without parsing localized text, and
`WinTrustAuthenticodeVerifier` verifies catalog and Authenticode trust through the
Windows trust APIs, deriving the required publisher from the verified chain's
certificate rather than a signer substring. The gate runs before the controller is
released and again before elevation; `NativeModeElevationBroker` keeps its existing
path, filename, canonical Program Files, and existence checks underneath it.

Two caveats. Release 0.9.7.8 is admitted only as an `ExperimentalBaseline` tier entry
so current testers are not blocked; per §4.3 that is not a supported production
minimum, and it does not assert the release is safe. And while the decision logic is
covered by offline tests, the enumeration and trust paths themselves cannot execute
without the driver installed — run the `validatedriver` diagnostic command on a real
installation to confirm them before distributing any build that depends on this gate.
A mistake in those paths would fail closed against a valid install rather than admit
an invalid one, but it would still block Native Mode.

### 4.3 Tier policy

- **Composite audio tier:** require the exact signed release that maintainers
  accept as fixing the request-lifetime defect. Release 0.9.7.8 remains a
  risk-bearing experimental baseline, not a supported production minimum.
- **HID-only scaffold:** no production minimum is defined. Avoiding the known
  audio trigger is insufficient to approve an older kernel driver.

Keep the tier-to-manifest policy in one versioned data structure covered by
tests. Do not scatter version comparisons across UI, broker, and installer code.

## 5. Installation and repair policy

- DS4Windows does not install usbip-win2 automatically.
- Requirements UI may link to the official
  [usbip-win2 releases](https://github.com/vadimgrn/usbip-win2/releases).
- It must identify the exact supported release and explain why an older,
  mismatched, or unsigned package is refused.
- DS4Windows never replaces driver-store files, changes signatures, enables test
  signing, weakens Secure Boot/Memory Integrity, starts a self-signed build, or
  edits another application's driver install.
- If a package is missing or invalid, Native Mode stays disabled and directs the
  user to the driver's own installer/repair workflow.
- The one application-owned repair remains deletion of the exact legacy Native
  Mode scheduled task left by early experimental builds.

Stale Native Mode device recovery belongs to the
[crash-containment design](native_mode_crash_containment.md), not to driver
repair. That recovery path is not implemented and must not guess a usbip port or
detach another application's device.

## 6. Uninstall policy

- DS4Windows does not uninstall usbip-win2. It is a shared system dependency
  that other software may use.
- DS4Windows uninstall removes only DS4Windows-owned files, settings, recovery
  records, and the exact legacy scheduled task if present.
- Driver removal remains an explicit user action through usbip-win2's official
  installer/uninstaller instructions.
- Before uninstalling DS4Windows-owned Native Mode files, the application should
  block or warn if an exact active Native Mode session has not completed
  teardown.

## 7. Tests required

These offline tests are implemented in `DS4WindowsTests\NativeModeDriverValidatorTests.cs`,
using fake inspector and verifier implementations so no driver install is required.
Offline tests cover:

- correct and incorrect hardware IDs, including changing ROOT instance numbers;
- missing, mixed-version, and wrong-provider package pairs;
- invalid, expired, revoked, developer, and test signatures;
- a valid manifest match for both x64 and x86 application packages;
- canonical and non-canonical usbip.exe paths;
- release label versus separate UDE/filter DriverVer values;
- localized Windows installations without localized-text parsing; and
- fail-closed behavior before controller release or elevation.

Any live validation of a newly signed driver remains explicitly approval-gated.
It must begin with ordinary attach/stop, then controlled audio transitions, and
must not combine a first run with Driver Verifier or unrelated stress tools.

## 8. Open decisions

1. Upstream approval and the first signed release accepted for composite audio.
   Reported as usbip-win2 issue 181; awaiting maintainer response.
2. ~~The exact manifest fields~~ — implemented: original INF name, provider,
   Windows DriverVer per package, usbip.exe file name and product version,
   accepted architectures, tier, and signer policy. Still open: whether file
   hashes should supplement publisher and catalog validation.
3. ~~The authoritative SetupAPI/Configuration Manager implementation~~ —
   implemented for both packages, but its OS-touching paths still need
   confirmation on a real installation via `validatedriver`.
4. Whether the HID-only scaffold merits a separate future experimental proposal
   after the common driver risk is reviewed.
5. Maintainer acceptance of the external, never-bundled dependency policy.
6. Install, repair, and uninstall policy (§5, §6) implementation and acceptance.

Until the remaining decisions are implemented and tested, driver gates 1 and 3
remain open. Gate 3's validation half is implemented; its install/repair/uninstall
half is not.
