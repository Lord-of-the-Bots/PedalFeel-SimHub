#include "engine.hpp"
#include "pedalfeel/simagic_protocol.hpp"
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
#include <set>

using namespace pedalfeel;
using namespace pedalfeel_plugin;
namespace {
void require(bool condition, const char* text) {
    if (!condition) { std::cerr << "FAILED: " << text << '\n'; std::exit(1); }
}
void equal(double actual, double expected, const char* text) {
    require(std::isfinite(actual) && std::abs(actual - expected) < 1e-12, text);
}
PfOutput run(const PfConfig& c, int effect, double t) {
    PfOutput out{};
    out.size = sizeof(out);
    out.version = PF_ABI_VERSION;
    require(pf_preview(&c, effect, t, &out) == PF_OK, "valid preview request");
    return out;
}
bool silent(const PfOutput& out) {
    return !out.brake_intensity && !out.brake_hz && !out.throttle_intensity && !out.throttle_hz;
}
PfConfig isolate(const PfConfig& c, int effect) {
    auto z = c;
    z.brake_texture = effect == PF_PREVIEW_THRESHOLD ? c.brake_texture : 0;
    z.abs_punch = effect == PF_PREVIEW_ABS ? c.abs_punch : 0;
    z.downshift_kick = effect == PF_PREVIEW_DOWNSHIFT ? c.downshift_kick : 0;
    z.traction_strength = effect == PF_PREVIEW_TRACTION ? c.traction_strength : 0;
    z.engine_texture = effect == PF_PREVIEW_ENGINE ? c.engine_texture : 0;
    z.limiter_strength = effect == PF_PREVIEW_LIMITER ? c.limiter_strength : 0;
    z.idle_texture = effect == PF_PREVIEW_IDLE ? c.idle_texture : 0;
    z.shift_kick = effect == PF_PREVIEW_UPSHIFT ? c.shift_kick : 0;
    z.surface_strength = effect >= PF_PREVIEW_SURFACE ? c.surface_strength : 0;
    if (effect > PF_PREVIEW_DOWNSHIFT) z.brake_strength = 0;
    return z;
}
void disable(PfConfig& c, int effect) {
    switch (effect) {
    case PF_PREVIEW_BRAKE_LOADING:
    case PF_PREVIEW_LOCKING: c.brake_strength = 0; break;
    case PF_PREVIEW_THRESHOLD: c.brake_texture = 0; break;
    case PF_PREVIEW_ABS: c.abs_punch = 0; break;
    case PF_PREVIEW_DOWNSHIFT: c.downshift_kick = 0; break;
    case PF_PREVIEW_TRACTION: c.traction_strength = 0; break;
    case PF_PREVIEW_ENGINE: c.engine_texture = 0; break;
    case PF_PREVIEW_LIMITER: c.limiter_strength = 0; break;
    case PF_PREVIEW_IDLE: c.idle_texture = 0; break;
    case PF_PREVIEW_UPSHIFT: c.shift_kick = 0; break;
    default: c.surface_strength = 0; break;
    }
}
void comparePhysical(const PfOutput& out, const HapticFrame& frame, const PfConfig& c, bool brake) {
    equal(brake ? out.brake_raw : out.throttle_raw, frame.output,
          "preview matches actual renderer signal before calibration");
    require((brake ? out.brake_mode : out.throttle_mode) == static_cast<int>(frame.mode),
            "preview retains actual renderer mode");
    const auto* lo = brake ? c.brake_minimum : c.throttle_minimum;
    const auto* hi = brake ? c.brake_maximum : c.throttle_maximum;
    const int frequency = static_cast<int>(std::lround(std::clamp(frame.frequencyHz, 10.0, 50.0)));
    const int request = static_cast<int>(std::lround(std::clamp(frame.output * c.effects_gain, 0.0, 1.0) * 100));
    const int expected = simagic::calibrateIntensity(request, frequency,
        {lo[0], lo[1], lo[2], lo[3]}, {hi[0], hi[1], hi[2], hi[3]});
    require((brake ? out.brake_intensity : out.throttle_intensity) == expected,
            "preview uses current global gain followed by actual frequency calibration");
    require((brake ? out.brake_hz : out.throttle_hz) == (expected ? frequency : 0),
            "preview frequency is from actual renderer, with complete silence at zero command");
}
void directRendererChecks() {
    auto c = defaults();
    c.effects_gain = 2.1;
    for (int i = 0; i < 4; ++i) {
        c.brake_minimum[i] = c.throttle_minimum[i] = 5 + i * 6;
        c.brake_maximum[i] = c.throttle_maximum[i] = 40 + i * 20;
    }
    BrakeRenderer downshift, abs;
    ThrottleRenderer upshift, limiter, engine;
    SurfaceRenderer road;
    for (int i = 0; i < 120; ++i) {
        const double t = i / 60.0;
        auto smooth = [](double v) { const double x = std::clamp(v, 0.0, 1.0); return x*x*(3-2*x); };
        const double envelope = smooth(t/.2) * (1-smooth((t-1.55)/.4));
        VehicleState s{};
        s.time = 1+t;
        s.gear = t < .15 ? 4 : 3;
        s.longitudinalG = -.3;
        comparePhysical(run(c, PF_PREVIEW_DOWNSHIFT, t),
            downshift.render(s, {.91, c.brake_strength, 0, 0, c.downshift_kick}), c, true);
        s = {};
        s.time = 1+t;
        s.absSeverity = .9 * envelope;
        comparePhysical(run(c, PF_PREVIEW_ABS, t),
            abs.render(s, {.91, c.brake_strength, 0, c.abs_punch, 0}), c, true);
        s = {};
        s.time = 1+t;
        s.gear = t < .15 ? 3 : 4;
        s.engineRpm = t < .15 ? 6500 : 4800;
        s.throttle = s.throttleRaw = .9;
        comparePhysical(run(c, PF_PREVIEW_UPSHIFT, t), upshift.render(s, 0, 0, c.shift_kick, 0, 0), c, false);
        s.gear = 3;
        s.engineRpm = 9000;
        s.throttle = s.throttleRaw = 1;
        s.engineWarningsAvailable = true;
        s.revLimiterActive = t >= .15 && t < 1.55;
        comparePhysical(run(c, PF_PREVIEW_LIMITER, t), limiter.render(s, 0, 0, 0, 0, c.limiter_strength), c, false);
        s = {};
        s.time = 1+t;
        s.gear = 3;
        s.throttle = s.throttleRaw = .8 * envelope;
        s.engineRpm = 1800+6800*smooth(t/1.65);
        comparePhysical(run(c, PF_PREVIEW_ENGINE, t), engine.render(s, 0, c.engine_texture, 0, 0, 0), c, false);
        s = {};
        s.time = 1+t;
        s.surfaceTelemetryAvailable = true;
        s.surfaceImpactSeverity = t >= .15 && t < .17 ? .8 : 0;
        const auto bump = road.render(s, c.surface_strength);
        const auto result = run(c, PF_PREVIEW_SURFACE, t);
        comparePhysical(result, bump.brake, c, true);
        comparePhysical(result, bump.throttle, c, false);
    }
    // A wheel-slip preview omits baseline loading without changing any original
    // lock or threshold math; this option is never used for live rendering.
    BrakeRenderer normal, isolated;
    VehicleState s{};
    s.time = 1;
    s.brake = .8;
    const RenderSettings settings{.91, .7, 0, 0, 0};
    require(normal.render(s, settings).output > 0 && isolated.render(s, settings, false).output == 0,
            "preview-only loading exclusion removes the pressure foundation cleanly");
}
}

int main() {
    directRendererChecks();
    auto c = defaults();
    const auto original = c;
    for (int effect = PF_PREVIEW_BRAKE_LOADING; effect <= PF_PREVIEW_RUMBLE; ++effect) {
        int active = 0;
        auto muted = c;
        disable(muted, effect);
        const auto only = isolate(c, effect);
        auto noGain = c;
        noGain.effects_gain = 0;
        std::fill(std::begin(noGain.brake_minimum), std::end(noGain.brake_minimum), 30);
        std::fill(std::begin(noGain.throttle_minimum), std::end(noGain.throttle_minimum), 30);
        for (int frame = 0; frame < 120; ++frame) {
            const double t = frame / 60.0;
            const auto out = run(c, effect, t);
            const auto isolated = run(only, effect, t);
            equal(out.brake_raw, isolated.brake_raw, "unrelated effect strengths cannot alter the chosen brake preview");
            equal(out.throttle_raw, isolated.throttle_raw, "unrelated effect strengths cannot alter the chosen throttle preview");
            require(out.new_sample && out.driving_active && !out.connected && !out.stale,
                    "synthetic preview status never claims a real connection");
            equal(out.session_time, t, "preview reports requested elapsed time");
            if (!silent(out)) ++active;
            if (effect <= PF_PREVIEW_DOWNSHIFT)
                require(!out.throttle_raw && !out.throttle_mode && !out.throttle_intensity && !out.throttle_hz,
                        "brake preview never drives throttle");
            if (effect >= PF_PREVIEW_TRACTION && effect <= PF_PREVIEW_UPSHIFT)
                require(!out.brake_raw && !out.brake_mode && !out.brake_intensity && !out.brake_hz,
                        "throttle preview never crossfeeds the brake");
            const auto off = run(muted, effect, t);
            require(silent(off) && !off.brake_raw && !off.throttle_raw && !off.brake_mode && !off.throttle_mode,
                    "zero effect strength is never silently replaced for preview");
            require(silent(run(noGain, effect, t)), "zero global gain remains silent with nonzero tactile minima");
            require(out.brake_intensity <= 35 && out.throttle_intensity <= 35,
                    "default preview respects existing calibration ceiling");
        }
        require(active > 0, "each supported effect has an audible command during its scenario");
        for (double elapsed : {2.0, 2.001, 500.0, std::numeric_limits<double>::max()}) {
            const auto stopped = run(c, effect, elapsed);
            require(silent(stopped) && !stopped.brake_raw && !stopped.throttle_raw &&
                    !stopped.brake_mode && !stopped.throttle_mode && !stopped.driving_active,
                    "preview ends precisely at two seconds even with an extreme finite time");
        }
        auto disabled = c;
        disabled.brake_enabled = disabled.throttle_enabled = 0;
        require(silent(run(disabled, effect, .17)), "preview honors disabled physical channels");
        disabled = c;
        disabled.brake_enabled = 0;
        require(!run(disabled, effect, .17).brake_intensity, "individual brake channel disable applies to previews");
        disabled = c;
        disabled.throttle_enabled = 0;
        require(!run(disabled, effect, .17).throttle_intensity, "individual throttle channel disable applies to previews");
        const auto first = run(c, effect, .17);
        (void)run(c, (effect + 1) % 12, 1.5);
        const auto repeat = run(c, effect, .17);
        require(std::memcmp(&first, &repeat, sizeof(first)) == 0,
                "preview output is deterministic and independent of previous preview calls");
    }
    require(std::memcmp(&c, &original, sizeof(c)) == 0, "preview never mutates caller configuration");

    for (int effect : {PF_PREVIEW_DOWNSHIFT, PF_PREVIEW_UPSHIFT, PF_PREVIEW_SURFACE}) {
        require(silent(run(c, effect, .1)) && !silent(run(c, effect, .15)) && silent(run(c, effect, .5)),
                "shift and impact previews are one actual event, not a repeated fixed tone");
    }
    std::set<int> enginePitches, limiterPitches, rumblePitches;
    for (int i = 20; i < 90; ++i) {
        enginePitches.insert(run(c, PF_PREVIEW_ENGINE, i / 60.0).throttle_hz);
        limiterPitches.insert(run(c, PF_PREVIEW_LIMITER, i / 60.0).throttle_hz);
        rumblePitches.insert(run(c, PF_PREVIEW_RUMBLE, i / 60.0).throttle_hz);
    }
    require(enginePitches.size() > 8 && limiterPitches.size() > 1 && rumblePitches.size() > 8,
            "renderer frequency sweeps and limiter cadence survive preview output");
    auto quieter = c;
    quieter.effects_gain *= .5;
    require(run(quieter, PF_PREVIEW_ENGINE, 1.0).throttle_intensity < run(c, PF_PREVIEW_ENGINE, 1.0).throttle_intensity,
            "current overall strength changes actual preview commands");
    auto lowerThreshold = c;
    lowerThreshold.grip_threshold = .75;
    auto higherThreshold = c;
    higherThreshold.grip_threshold = 1.05;
    require(run(lowerThreshold, PF_PREVIEW_THRESHOLD, .7).brake_raw > run(higherThreshold, PF_PREVIEW_THRESHOLD, .7).brake_raw,
            "grip threshold setting affects the simulated grip transition");

    PfOutput sentinel{};
    sentinel.size = sizeof(sentinel);
    sentinel.version = PF_ABI_VERSION;
    sentinel.brake_intensity = 17;
    auto rejects = [&](const PfConfig* config, int effect, double t, PfOutput output) {
        const auto before = output;
        require(pf_preview(config, effect, t, &output) == PF_INVALID_ARGUMENT &&
                std::memcmp(&before, &output, sizeof(output)) == 0,
                "invalid preview request leaves caller output untouched");
    };
    rejects(nullptr, PF_PREVIEW_ENGINE, 0, sentinel);
    rejects(&c, -1, 0, sentinel);
    rejects(&c, 12, 0, sentinel);
    for (double t : {-.001, std::numeric_limits<double>::quiet_NaN(), std::numeric_limits<double>::infinity()})
        rejects(&c, PF_PREVIEW_ENGINE, t, sentinel);
    auto invalid = c;
    invalid.effects_gain = std::numeric_limits<double>::quiet_NaN();
    rejects(&invalid, PF_PREVIEW_ENGINE, 0, sentinel);
    invalid = c;
    invalid.version = 2;
    rejects(&invalid, PF_PREVIEW_ENGINE, 0, sentinel);
    invalid = c;
    invalid.size--;
    rejects(&invalid, PF_PREVIEW_ENGINE, 0, sentinel);
    sentinel.version = 2;
    rejects(&c, PF_PREVIEW_ENGINE, 0, sentinel);
    sentinel.version = PF_ABI_VERSION;
    sentinel.size--;
    rejects(&c, PF_PREVIEW_ENGINE, 0, sentinel);
    require(pf_preview(&c, PF_PREVIEW_ENGINE, 0, nullptr) == PF_INVALID_ARGUMENT,
            "null preview destination rejected");
    std::cout << "All preview tests passed (12 isolated actual-renderer scenarios, 60 Hz dynamics, settings/calibration, zero strengths, channel routing, time bounds, deterministic ABI).\n";
}
