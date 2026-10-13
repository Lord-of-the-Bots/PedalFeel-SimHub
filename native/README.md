# PedalFeel native bridge

Read-only iRacing telemetry reader and haptic rendering engine, exposed through a C ABI. The managed SimHub integration owns hardware output; this DLL does not open HID or USB devices.

## Source and licences

The telemetry reader and renderers are based on [UdaraJay/PedalFeel](https://github.com/UdaraJay/PedalFeel), commit `06649f3cd7c59abaa5f928d82752ae9d8a3956ef`, under the MIT licence. Renderer adaptations are described in [LOCAL_CHANGES.md](vendor/pedalfeel/LOCAL_CHANGES.md). Retain [the upstream licence](vendor/pedalfeel/LICENSE), [iRacing notices](vendor/pedalfeel/THIRD_PARTY_NOTICES.md) and the wrapper [licence](LICENSE).

Binary distributions also include `licenses/LLVM-LICENSE.TXT` and `licenses/MinGW-w64-runtime.txt` for the statically linked runtimes.

## ABI and output

`include/pedalfeel_api.h` defines ABI version 4. Use cdecl, pack=8, 32-bit integers and IEEE-754 doubles. `PfConfig` is 200 bytes and `PfOutput` is 80 bytes on both x86 and x64. Set structure sizes and versions before calling. Serialize access to each opaque handle, including destruction.

The caller polls the engine and consumes every returned output, including calls where `new_sample` is false. Duplicate telemetry frames do not advance the renderers. Disconnection, inactive driving and telemetry older than 250 ms silence output. Recovery, session rollback and transitions between driving and stationary operation reset effect envelopes. Settings changes preserve driving state and wait for a new sample before sending another command.

At rest, the engine renders only each pedal's configured idle effect. Stationary operation ends above 1 km/h and resumes at or below 0.5 km/h; this hysteresis prevents switching due to small speed fluctuations. Invalid speed is treated as stationary. Brake pressure, tyre feedback, road effects, shifts, limiter and loaded-engine vibration are excluded while stationary.

`effects_gain` defaults to 2.1 and accepts values from 0 to 4.2. The interface uses the fixed 2.1 baseline with independent pedal and effect strengths. Calibration uses four anchors at 16, 25, 35 and 50 Hz, initially 0–35%. Driving cues use the calibrated minimum and maximum. Idle uses its proportional signal and frequency maximum without applying a minimum-power floor. Disabled pedals and zero signals remain silent.

## Effect previews

`pf_preview` renders an isolated effect from simulated input. It uses the same renderers and physical conversion as live output, without reading game telemetry or writing to hardware. It is an illustration, not a recording from a car.

Preview IDs are defined in `PfPreviewEffect`, including separate engine and idle effects on the brake. The host owns preview cancellation and transport. Previews end after two seconds and report `connected=0`. Invalid input is rejected without updating the output structure.

## Build on Windows

Use a C++20 LLVM-MinGW toolchain, such as `llvm-mingw-20260922-ucrt-x86_64` from [LLVM-MinGW releases](https://github.com/mstorsjo/llvm-mingw/releases/tag/20260922).

```powershell
./build.ps1 -Toolchain 'C:\Tools\llvm-mingw-20260922-ucrt-x86_64' -Architecture both
```

Outputs: `build/x86/PedalFeel.Native.dll` and `build/x64/PedalFeel.Native.dll`. The target computer needs Windows UCRT; it does not need the compiler.
