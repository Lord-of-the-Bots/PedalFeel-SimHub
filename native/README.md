# PedalFeel native bridge

Read-only iRacing telemetry adapter and original PedalFeel renderers, exposed through
a small C ABI for the SimHub plug-in. **This DLL has no HID/USB device access.**
The managed plug-in exclusively owns device takeover and hardware output.

## Sources and licence

Vendored files are based on UdaraJay/PedalFeel commit
`06649f3cd7c59abaa5f928d82752ae9d8a3956ef` (MIT). Version 0.3.4 adds separate limiter
and brake-downshift controls and corrects ownership/ducking for effects set to zero.
These are explicit local renderer changes, documented in `vendor/pedalfeel/LOCAL_CHANGES.md`. See
`vendor/pedalfeel/LICENSE` and `vendor/pedalfeel/THIRD_PARTY_NOTICES.md`.
Wrapper files are also distributed under MIT, as stated in `LICENSE`.
Binary distributions must include `licenses/LLVM-LICENSE.TXT` and
`licenses/MinGW-w64-runtime.txt` for the statically linked C++ and MinGW runtimes.
Do not replace these sources silently when upgrading; run the included tests.

## ABI

`include/pedalfeel_api.h` is authoritative. Use cdecl, sequential layout and pack=8.
`PfConfig` is 176 bytes and `PfOutput` is 80 bytes on x86 and x64. All integers are
32 bits; no strings, objects or booleans cross the boundary. Set size to sizeof the relevant structure and version=3
before calls. Serialize all operations on each opaque handle. No callbacks or
background threads are created by the DLL. Poll every ~4 ms and always inspect
the returned output, even when `new_sample` is false: the telemetry timeout can
stop the output between samples.

The original rendering model, mixing order, integer rounding and four-anchor
calibration are retained, with the explicit per-effect changes listed above.
A repeated iRacing frame does not advance
the renderers. Disconnect, replay/garage state or >=250 ms without a new frame
zero output; filters reset on recovery, configuration changes and session rollback.
The `effects_gain` double at offset 152 accepts finite values from 0 to 4.2
and defaults to 2.1. The UI shows this baseline as ×1 and maps its ×0..×2 slider
to native gain 0..4.2. The new ×1 is 70% of the 0.3.3 default. It scales mixed signals before intensity rounding and calibration;
zero stays off, and calibrated commands retain the configured ceiling. Raw output
fields remain the original signal before amplification. A gain of 1 reproduces
the unamplified mixed output. The `limiter_strength` double at offset 160 and
`downshift_kick` at offset 168 accept finite 0..1 values and default to .20.
Upshift (`shift_kick`) also defaults to .20. A value of 1 for the new per-effect
controls reproduces the original fixed limiter and downshift pulses; zero disables
their amplitude, frequency/mode ownership and any attenuation of other effects.
Intensity calibration defaults to 0..35% per channel, pending user calibration.
Calibration anchor frequencies are 16, 25, 35 and 50 Hz. The remaining effect defaults
match upstream. The DLL only supports iRacing, as does the upstream application.

## Reproducible build on Windows

Requires a portable LLVM-MinGW C++20 toolchain, available from
https://github.com/mstorsjo/llvm-mingw/releases/tag/20260922 . The prototype used
`llvm-mingw-20260922-ucrt-x86_64.zip`, extracted under `.tools`. This is an isolated
workspace dependency and does not alter PATH or install software. Both targets
statically link the C++ runtime; the target computer needs Windows UCRT, not LLVM.
Archive SHA-256:
`e3ad77d117a4bea19a7a3b333341824d79a5a371004a10e25b8504e7b3047666`.

```powershell
./build.ps1 -Toolchain 'C:\path\llvm-mingw-20260922-ucrt-x86_64' -Architecture both
```

Outputs: `build/x86/PedalFeel.Native.dll` and `build/x64/PedalFeel.Native.dll`.
The build runs upstream engine tests and bridge tests on both architectures.
Bridge tests compare 1,500 evolving frames against the direct upstream renderer
and mixer at gain 1 with full limiter/downshift, verify gain 3 and clipping, and
check exact timeout behavior, inactive/disconnected states, filter reset,
conservative calibration, configuration validation and C ABI use. Additional tests
cover limiter/downshift 0/.2/1, independent engine/upshift behavior, direct limiter
telemetry requirements, and the absence of mode/frequency/ducking from disabled
effects. The unchanged upstream engine tests are also run on both architectures.

Tests use synthetic telemetry and do not access or actuate pedals. Real hardware,
SimHub device takeover and real iRacing sessions must be validated separately.

## Isolated effect previews (0.4.0)

The additive `pf_preview(config, effect, elapsed_seconds, output)` export uses the
same ABI v3 structures (176/80 bytes) and does not need a runtime handle. It replays
one scripted synthetic event at 60 Hz through fresh instances of the actual brake,
throttle or surface renderer, then calls the same `physical()` conversion as live
output. Current effect strengths (including zero), overall gain, frequency
calibration and enabled channels are respected. A preview is an illustration of
the algorithm; it is not a recording or prediction of a particular car.

Stable IDs in `PfPreviewEffect` select brake loading, threshold, locking, ABS,
downshift, traction, engine, limiter, idle, upshift, a road impact or a kerb. IDs
0–4 target only brake, 5–9 only throttle, and 10–11 both enabled channels. Chassis
crossfeed is intentionally omitted to make the selected pedal/effect clear.
For threshold and lock previews, the pressure foundation is omitted through the
optional C++ `BrakeRenderer::render(..., includeLoading=false)` argument; live
rendering keeps its default true. Other unrelated effects are set to zero.

Each call independently replays from the start up to the requested elapsed time,
so there is no shared preview state and live telemetry/filter state cannot be
changed. Upshift/downshift use one real gear transition at 150 ms; the impact uses
one onset at 150 ms. Continuous effects use ramps and renderer envelopes, and the
limiter retains its renderer pulse/pitch cadence. At elapsed time >=2 seconds the
result is silent. Negative or nonfinite time, invalid configuration/ID, or a bad
output header is rejected atomically without changing the output.

During an active preview `new_sample=1`, `driving_active=1`, `stale=0` and sample age
is zero; `session_time` is preview elapsed time. `connected` remains zero because
this API does not inspect an actual game or device. The host must separately
verify device availability and own/cancel the output. No DLL code writes HID/USB.
The native build includes a separate preview test executable on x86 and x64.
