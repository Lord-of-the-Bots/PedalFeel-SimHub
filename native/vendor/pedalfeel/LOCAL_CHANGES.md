# Local SimHub renderer changes (0.3.4–0.4.0)

Base: UdaraJay/PedalFeel `06649f3cd7c59abaa5f928d82752ae9d8a3956ef`.
MIT licence and third-party notices are retained. The telemetry reader, protocol,
upstream tests and other source files are unchanged. These files have local edits:

- `include/pedalfeel/renderer.hpp`: optional final `limiterStrength` argument,
  default 1, on `ThrottleRenderer::render`. Version 0.4.0 adds optional final
  `includeLoading=true` to `BrakeRenderer::render` for isolated effect previews.
- `include/pedalfeel/types.hpp`: trailing `RenderSettings::downshiftKick`, default 1.
- `src/renderer.cpp`: limiter strength scales its pulse and engine attenuation;
  zero immediately clears the limiter envelope. Downshift strength scales its
  pulse before the original brake shaping. Zero strength for limiter, upshift,
  downshift, ABS, traction or threshold texture releases that effect's mode and
  frequency ownership and attenuation of other effects. Version 0.4.0 allows the
  pressure/loading foundation to be omitted only when explicitly requested by a
  preview; the live call retains its original true default.

At positive original settings with both new controls at 1, the upstream model is
unchanged. Before/after synthetic traces were compared over 1,500 frames on x86
and x64, including all six frame doubles and the mode on both pedals. The bridge
tests and original upstream engine tests are run by `native/build.ps1`.

## Effect-control audit

| Rendered cue | Controlling setting | Notes |
| --- | --- | --- |
| Brake pressure/loading | `brake_strength` | Part of the primary brake group. |
| Grip threshold texture | `brake_texture`, `grip_threshold`, `brake_strength` | Texture zero leaves loading/lock/ABS available. |
| Front-wheel lock and initial lock transient | `brake_strength` | Primary brake warning; not an unadjustable output. |
| ABS | `abs_punch`, `brake_strength` | Zero no longer ducks loading/threshold/downshift or blocks road feedback via ABS mode. |
| Brake downshift thump | `downshift_kick`, `brake_strength` | New independent control, native default .20. |
| Rear traction / wheelspin | `traction_strength` | One original group; zero no longer ducks engine, shifts or limiter. |
| Engine character, including high-RPM lift | `engine_texture` | RPM lift is part of engine character, distinct from limiter. |
| Idle texture | `idle_texture` | Separate low-RPM layer. |
| Upshift thump | `shift_kick` | Native default .20; zero no longer changes engine frequency. |
| Rev limiter | `limiter_strength` | New independent control, native default .20. Requires actual available iRacing limiter flag, forward gear and sufficient pedal demand; never inferred solely from RPM. |
| Road impacts / kerbs | `surface_strength` | Both cues share the road group, as in upstream. |
| Engine/driveline transmission into brake | Respective engine/idle/upshift/limiter setting | Original fixed transmission proportions follow the source signal; no new independent pulse. |
| Final output scaling | `effects_gain` | Native default 2.1, range 0..4.2; then per-pedal frequency calibration and command ceiling. |

The native wrapper forwards the controls through ABI v3. The upstream public
renderer defaults stay at 1 for the two added controls to preserve existing call
semantics; they are not the plug-in's default preset values.
