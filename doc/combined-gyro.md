# Combined yaw + roll gyro aiming

## Setup

1. Back up your profiles and close any other DS4Windows instance.
2. For a Switch Pro controller, enable Switch Pro support in DS4Windows device options
   if it is not already enabled. Connect over USB or Bluetooth.
3. Edit a profile and select **Gyro Mouse** or **Gyro Mouse Joystick**.
4. Set **X Axis** (or **X-Axis**) to **Combined**.
5. Adjust **Yaw sensitivity** and **Roll sensitivity** using the sliders or numeric
   controls. Each accepts whole numbers from `-100` through `100`.
6. For Xbox right-stick aiming, use Mouse Joystick, Right Stick output, and the
   existing Xbox 360 virtual-controller configuration. Save the profile.

`0` disables an axis; a negative value reverses that contribution. For example:

| Yaw | Roll | Horizontal input |
| ---: | ---: | --- |
| 100 | 0 | Yaw only |
| 0 | 100 | Roll only |
| 100 | 100 | Yaw plus roll |
| 100 | -100 | Yaw minus roll |
| 0 | 0 | No new horizontal gyro input |

The calculation is `yaw * (yawSensitivity / 100.0) + roll * (rollSensitivity / 100.0)`.
It runs before the existing deadzone, sensitivity, jitter compensation, smoothing,
anti-deadzone, and output conversion for the selected mode. Those stages run once
on the combined input, not separately on each contribution. Smoothing may retain
prior motion briefly when weights change. Two full contributions can reach the
existing joystick saturation limit sooner; reduce weights or adjust Max Zone.

**Invert X is disabled and ignored in Combined mode** because signs are specified
per axis. Invert Y, vertical sensitivity, output-stick selection, and activation
triggers retain their existing behavior. The existing radial deadzone/anti-deadzone
still uses the horizontal/vertical direction together; blending can therefore
affect its direction-dependent scaling, just as changing the old axis selection did.

## Compatibility and XML

The original horizontal modes remain `0 = Yaw`, `1 = Roll`; `2 = Combined` is new.
Yaw/Roll modes ignore the new weights and retain their original late-stage
horizontal inversion, including joystick rounding. Horizontal inversion flags are
not cleared when selecting Combined, so switching back preserves them.

Profiles store independent settings for each output mode:

```xml
<GyroMouseHAxis>2</GyroMouseHAxis>
<GyroMouseYawSensitivity>100</GyroMouseYawSensitivity>
<GyroMouseRollSensitivity>-50</GyroMouseRollSensitivity>
<GyroMouseStickHAxis>2</GyroMouseStickHAxis>
<GyroMouseStickYawSensitivity>100</GyroMouseStickYawSensitivity>
<GyroMouseStickRollSensitivity>-50</GyroMouseStickRollSensitivity>
```

When a new weight is missing, it is derived from that mode's saved legacy axis and
horizontal inversion, rather than unconditionally using positive yaw:

| Legacy axis | Horizontal inversion | Initial yaw | Initial roll |
| --- | --- | ---: | ---: |
| Yaw | Off | 100 | 0 |
| Yaw | On | -100 | 0 |
| Roll | Off | 0 | 100 |
| Roll | On | 0 | -100 |

Mouse uses bit `2` of `GyroInvert` for horizontal inversion; Mouse Joystick uses bit
`1` of `GyroMouseStickInvert`. Vertical inversion bits are untouched. Explicit
weights, including zero, take precedence over these defaults. Signed weights are
clamped to `[-100, 100]` when mapped into the runtime settings, including after XML
loading. Non-integer XML values follow the existing profile deserializer's error
handling. Brand-new/reset profiles start with yaw `100`, roll `-100`.

Legacy modes are not automatically changed to Combined. The weights are initialized
when loading a profile with missing fields, then saved independently; they are not
continually synchronized with subsequent edits to the legacy selector or inversion.
Back up profiles before opening them in an older DS4Windows build: older versions
do not understand Combined mode and may discard its settings when saving.

## Sensor conventions and implementation

- `DS4Library/InputDevices/SwitchProDevice.cs` calibrates the Nintendo IMU, negates
  its yaw, keeps its roll sign, and normalizes both through `PrepareNonDS4SixAxis`.
  It populates `gyroYawFull` and `gyroRollFull` in matching units; both use the same
  conversion factor for degrees/second. No orientation transform is added here.
- `DS4Control/ProfilePropGroups.cs` contains validated per-mode horizontal settings
  and the shared weighted calculation. Summing full-resolution sensor values is
  equivalent to summing their angular velocities before the existing conversion.
- `DS4Control/MouseCursor.cs` blends at mouse input; `DS4Control/Mouse.cs` blends at
  joystick input. Fractional horizontal contributions survive until downstream
  conversion in Combined mode. Vertical input remains negative `gyroPitchFull`.
- `DS4Control/DTOXml/ProfileDTO.cs` handles active profile loading/saving and legacy
  defaults. The unused pre-DTO XML reader/writer is not extended.
- `DS4Forms/ProfileEditor.xaml` and its settings ViewModel expose the controls.

The joystick path also now uses the current controller's horizontal-mode setting,
rather than always reading controller slot zero. No drivers, controller routing,
or device-hiding settings are changed.

## Build and automated checks

Use Windows x64 with the .NET 8 SDK; Visual Studio IDE/Build Tools are not required
by this build. From the repository root:

```powershell
dotnet restore .\DS4WindowsWPF.sln -p:Platform=x64
dotnet build .\DS4WindowsWPF.sln -c Debug -p:Platform=x64
dotnet test .\DS4WindowsWPF.sln -c Debug -p:Platform=x64
dotnet test .\DS4WindowsTests\DS4WindowsTests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~GyroHorizontalTests
dotnet build .\DS4WindowsWPF.sln -c Release -p:Platform=x64
```

The new tests cover signed math, fractional weights, legacy defaults, independent
controller slots, partial settings, clamping, reset, XML round-trips, both output
pipelines with all smoothing modes, cancellation, vertical inversion, and activation.
At implementation time, all 33 new cases passed. Three full-suite failures already
existed on the unmodified source: `CheckSettingsSave`, `CheckJaysProfileRead`, and
`CheckWriteProfile` have stale XML snapshots for unrelated, existing settings.

## x64 package

The executable project is `DS4Windows/DS4WinWPF.csproj`, targeting `net8.0-windows`.
Publish the entire output, not just the executable:

```powershell
dotnet publish .\DS4Windows\DS4WinWPF.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained false -o .\bin\x64\Release\combined-gyro
Copy-Item .\LICENSE.txt, .\NOTICE.txt .\bin\x64\Release\combined-gyro\
Compress-Archive -Path .\bin\x64\Release\combined-gyro\* -DestinationPath .\bin\x64\Release\DS4Windows-combined-gyro-x64.zip
```

Keep the content, localization folders, managed assemblies, and native dependencies
from publish together. This framework-dependent package requires the .NET 8 Desktop
Runtime x64, Visual C++ redistributable, and the existing ViGEmBus setup. Driver
installation is not part of the package. Retain GPL-3.0 licensing, notices, and
provide corresponding source when distributing a modified binary. The repository's
release workflow uses `utils/post-build.py` to relocate translations and archive
the app; the commands above leave translations in their standard publish locations.

## Manual verification still required

Automated sensor tests do not replace a clean UI launch or real controller testing:

- Launch the published executable after closing the installed copy. Do not test
  against your only copy of a profile. Reopen the editor after saving/restarting and
  verify both exact signed values and the Combined selection.
- Verify yaw-only, roll-only, both positive, and each negative weight with a physical
  Switch Pro controller over USB/Bluetooth. Test flat and upright holds; axes are
  controller-local, with no automatic orientation blending.
- Verify right-stick saturation, center at rest, deadzones, anti-deadzones, both
  smoothing methods, vertical motion, and activation/deactivation/toggle behavior.
- Check legacy yaw/roll/inverted profiles unchanged. Allow at most final-output
  rounding differences when deliberately switching an inverted legacy profile to
  signed Combined input, because inversion then occurs before output conversion.
- Confirm only the existing virtual controller is present and use the existing
  HidHide setup if needed for double input.
