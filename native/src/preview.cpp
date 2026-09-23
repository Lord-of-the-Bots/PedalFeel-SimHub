#include "preview.hpp"
#include <algorithm>
#include <cmath>

namespace pedalfeel_plugin {
namespace {
double smooth(double value) noexcept {
    const double x = std::clamp(value, 0.0, 1.0);
    return x * x * (3.0 - 2.0 * x);
}

// These are illustrative input events, not recordings or predictions for a car.
// They drive the same stateful renderers as live iRacing telemetry.
pedalfeel::VehicleState fixture(int32_t effect, double t) noexcept {
    pedalfeel::VehicleState s{};
    s.time = 1.0 + t;
    s.speedKph = 100;
    s.gear = 3;
    s.engineRpm = 1100;
    const double envelope = smooth(t / .2) * (1.0 - smooth((t - 1.55) / .4));
    switch (effect) {
    case PF_PREVIEW_BRAKE_LOADING:
        s.brake = s.brakeRaw = .65 * envelope;
        break;
    case PF_PREVIEW_THRESHOLD:
        s.brake = s.brakeRaw = .8 * envelope;
        s.tires[0].combinedUtilization = s.tires[1].combinedUtilization =
            (.55 + .55 * smooth(t / 1.4)) * envelope;
        break;
    case PF_PREVIEW_LOCKING:
        s.brake = s.brakeRaw = .8 * envelope;
        s.tires[0].slipRatio = s.tires[1].slipRatio = .34 * envelope;
        s.tires[0].locking = s.tires[1].locking = s.tires[0].slipRatio > .18;
        break;
    case PF_PREVIEW_ABS:
        s.absSeverity = .9 * envelope;
        break;
    case PF_PREVIEW_DOWNSHIFT:
        s.gear = t < .15 ? 4 : 3;
        s.longitudinalG = -.3;
        break;
    case PF_PREVIEW_TRACTION:
        s.throttle = s.throttleRaw = .9 * envelope;
        s.rearSlipSeverity = .8 * envelope;
        s.tires[2].spinRatio = s.tires[3].spinRatio = .18 * envelope;
        break;
    case PF_PREVIEW_ENGINE:
        s.throttle = s.throttleRaw = .8 * envelope;
        s.engineRpm = 1800 + 6800 * smooth(t / 1.65);
        break;
    case PF_PREVIEW_LIMITER:
        s.throttle = s.throttleRaw = 1;
        s.engineRpm = 9000;
        s.engineWarningsAvailable = true;
        s.revLimiterActive = t >= .15 && t < 1.55;
        break;
    case PF_PREVIEW_IDLE:
        s.engineRpm = 1100 + 2400 * smooth((t - 1.55) / .4);
        break;
    case PF_PREVIEW_UPSHIFT:
        s.gear = t < .15 ? 3 : 4;
        s.throttle = s.throttleRaw = .9;
        s.engineRpm = t < .15 ? 6500 : 4800;
        break;
    case PF_PREVIEW_SURFACE:
        s.surfaceTelemetryAvailable = true;
        s.surfaceImpactSeverity = t >= .15 && t < .17 ? .8 : 0;
        break;
    case PF_PREVIEW_RUMBLE:
        s.rumbleStripTelemetryAvailable = true;
        s.rumbleStripSeverity = .75 * envelope;
        s.rumbleStripFrequencyHz = 12 + 95 * smooth(t / 1.65);
        break;
    }
    return s;
}
}

bool validPreviewEffect(int32_t effect) noexcept {
    return effect >= PF_PREVIEW_BRAKE_LOADING && effect <= PF_PREVIEW_RUMBLE;
}

PfOutput preview(const PfConfig& config, int32_t effect, double elapsedSeconds) noexcept {
    PfOutput output{};
    output.size = sizeof(output);
    output.version = PF_ABI_VERSION;
    output.new_sample = 1;
    output.session_time = elapsedSeconds;
    if (elapsedSeconds >= 2.0) return output;
    output.driving_active = 1;
    pedalfeel::BrakeRenderer brakeRenderer;
    pedalfeel::ThrottleRenderer throttleRenderer;
    pedalfeel::SurfaceRenderer surfaceRenderer;
    pedalfeel::HapticFrame brake{}, throttle{};
    pedalfeel::RenderSettings brakeSettings{config.grip_threshold, config.brake_strength,
        effect == PF_PREVIEW_THRESHOLD ? config.brake_texture : 0,
        effect == PF_PREVIEW_ABS ? config.abs_punch : 0,
        effect == PF_PREVIEW_DOWNSHIFT ? config.downshift_kick : 0};
    const int lastFrame = static_cast<int>(std::floor(elapsedSeconds * 60.0 + 1e-9));
    for (int i = 0; i <= lastFrame; ++i) {
        const auto state = fixture(effect, i / 60.0);
        if (effect <= PF_PREVIEW_DOWNSHIFT) {
            brake = brakeRenderer.render(state, brakeSettings, effect == PF_PREVIEW_BRAKE_LOADING);
        } else if (effect <= PF_PREVIEW_UPSHIFT) {
            throttle = throttleRenderer.render(state,
                effect == PF_PREVIEW_TRACTION ? config.traction_strength : 0,
                effect == PF_PREVIEW_ENGINE ? config.engine_texture : 0,
                effect == PF_PREVIEW_UPSHIFT ? config.shift_kick : 0,
                effect == PF_PREVIEW_IDLE ? config.idle_texture : 0,
                effect == PF_PREVIEW_LIMITER ? config.limiter_strength : 0);
        } else {
            const auto road = surfaceRenderer.render(state, config.surface_strength);
            brake = road.brake;
            throttle = road.throttle;
        }
    }
    // Preview the chosen pedal(s) directly; an engine preview must not also feel
    // like a brake test because live chassis crossfeed happened to be enabled.
    if (!config.brake_enabled) brake = {};
    if (!config.throttle_enabled) throttle = {};
    output.brake_raw = brake.output;
    output.throttle_raw = throttle.output;
    output.brake_mode = static_cast<int32_t>(brake.mode);
    output.throttle_mode = static_cast<int32_t>(throttle.mode);
    physical(brake, config.effects_gain, config.brake_minimum, config.brake_maximum,
             output.brake_hz, output.brake_intensity);
    physical(throttle, config.effects_gain, config.throttle_minimum, config.throttle_maximum,
             output.throttle_hz, output.throttle_intensity);
    return output;
}
}
