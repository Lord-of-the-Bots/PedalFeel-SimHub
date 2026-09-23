# Contributing

Useful contributions include driving reports, documented profiles, translations, reproducible bug reports and focused code changes.

The telemetry reader and haptic renderers originate from [PedalFeel 0.19.0 by UdaraJay](https://github.com/UdaraJay/PedalFeel). Keep that attribution and the supplied licence notices when changing or redistributing this integration.

## Profiles and feedback

Use the [profile form](https://github.com/Lord-of-the-Bots/PedalFeel-SimHub/issues/new?template=profile.yml) and [sharing guide](docs/PROFILE-SHARING.md). Include the complete effect settings and motor calibration, car and hardware, driving scenario and limitations. Reports of weak, overwhelming or misleading effects are useful even when you have not found better settings yet.

The aim is to collect repeatable recommendations for particular cars or classes and pedal setups. A setting tried in one car should not be presented as validated for an entire class. Identify preview-only results separately from actual driving.

For faults, use the [bug report form](https://github.com/Lord-of-the-Bots/PedalFeel-SimHub/issues/new?template=bug_report.yml). Include versions, reproduction steps and the relevant **Status details** from the PedalFeel tab.

## Build on Windows

You need an installed copy of SimHub, a .NET SDK with the .NET Framework 4.8 build requirements, and LLVM-MinGW for the native engine. See [native/README.md](native/README.md) for native dependencies and architecture details.

From the repository root in PowerShell:

```powershell
./build.ps1 -SimHubDirectory 'C:\Program Files (x86)\SimHub' -Dotnet 'dotnet' -Toolchain 'C:\Tools\llvm-mingw'
```

The build script builds the native libraries and managed integration and runs the associated checks. Release development uses the SimHub 9.12.8 API, with compatibility checks against 9.11.21 and 9.12.8. A successful x64 native build does not establish compatibility with another generation of SimHub.

Automated checks use synthetic telemetry and host fixtures; they do not replace a drive with physical motors. Report hardware validation separately when a change affects output or feel. Use `-SkipNative` only for managed changes after the corresponding native libraries have been built.

## Code changes

Describe the user problem, the resulting behaviour and how you checked it. Keep each change focused enough to review.

- Preserve existing profiles, car assignments and device calibration when migrating settings. Do not overwrite tuned values with new defaults.
- Keep the SimHub device as the physical transport. The integration should not open a competing USB connection.
- Account for preview expiry, cancellation, disconnects and shutdown when changing output ownership.
- Keep zero-strength effects off. Preserve the distinction between profile strength, physical calibration and direct frequency tests.
- Add focused checks for behaviour that can regress, including state transitions and error paths where relevant.
- Update all seven interface languages when changing user-facing text. Translation must not change custom profile names or settings.
- Document changes to vendored rendering code in [LOCAL_CHANGES.md](native/vendor/pedalfeel/LOCAL_CHANGES.md), and retain the upstream source reference.

Do not commit installed SimHub files, generated build output or personal device configuration. Use generic examples in documentation and avoid local usernames and machine-specific paths in checked-in artifacts.

## Scope

Support for another game or motor requires an implementation and evidence that its telemetry and output behave correctly. Profiles alone do not add support for another device or turn the underlying estimates into a validated model for every car.

Code contributions are covered by the repository's licence; retain third-party notices. Profile recommendations should keep their tested car, hardware and limitations alongside the values so others can decide whether they apply to their setup.
