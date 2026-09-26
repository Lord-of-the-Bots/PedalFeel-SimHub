#include "engine.hpp"
#include "pedalfeel/simagic_protocol.hpp"
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <limits>

using namespace pedalfeel;
using namespace pedalfeel_plugin;
namespace {
void require(bool condition, const char* text) {
    if (!condition) { std::cerr << "FAILED: " << text << '\n'; std::exit(1); }
}
void equal(double actual, double expected, const char* text) {
    require(std::isfinite(actual) && std::abs(actual - expected) < 1e-12, text);
}
VehicleState fixture(int i) {
    VehicleState s{};
    s.time = 1 + i / 60.0;
    s.speedKph = 100;
    s.engineRpm = 1700 + (i % 120) * 54;
    s.engineWarningsAvailable = true;
    s.revLimiterActive = i % 120 > 115;
    s.brake = s.brakeRaw = (i % 90) / 90.0;
    s.throttle = s.throttleRaw = (i % 70) / 70.0;
    s.absSeverity = i % 37 > 30 ? .8 : 0;
    s.rearSlipSeverity = i % 63 > 55 ? .7 : 0;
    s.gear = 1 + (i / 75) % 5;
    s.surfaceTelemetryAvailable = true;
    s.surfaceImpactSeverity = i % 83 == 0 ? .9 : 0;
    s.rumbleStripTelemetryAvailable = true;
    s.rumbleStripSeverity = i % 51 > 40 ? .6 : 0;
    s.rumbleStripFrequencyHz = 82;
    for (auto& tire : s.tires) {
        tire.combinedUtilization = .5 + (i % 31) / 35.0;
        tire.slipRatio = i % 101 > 93 ? .5 : .02;
        tire.locking = tire.slipRatio > .3;
    }
    return s;
}

void sameFrame(const HapticFrame& actual, const HapticFrame& expected, const char* text) {
    equal(actual.output, expected.output, text);
    equal(actual.frequencyHz, expected.frequencyHz, text);
    require(actual.mode == expected.mode, text);
}

void independentEffects() {
    VehicleState s{};
    s.time = 1.01;
    s.gear = 3;
    s.engineRpm = s.redlineRpm = s.shiftRpm = 9000;
    s.throttle = s.throttleRaw = 1;
    s.engineWarningsAvailable = s.revLimiterActive = true;
    ThrottleRenderer fullLimiter, softLimiter, zeroLimiter;
    for (int i = 0; i < 120; ++i) {
        s.time += 1.0 / 60;
        const auto full = fullLimiter.render(s, 0, 0, 0, 0, 1);
        const auto soft = softLimiter.render(s, 0, 0, 0, 0, .20);
        const auto zero = zeroLimiter.render(s, 0, 0, 0, 0, 0);
        require(full.output > 0 && full.mode == HapticMode::Limiter, "limiter full scale creates its own pulse");
        equal(soft.output, full.output * .20, "limiter twenty percent independently scales the pulse");
        require(soft.mode == HapticMode::Limiter && soft.frequencyHz == full.frequencyHz,
                "lower limiter retains recognizable frequency");
        require(zero.output == 0 && zero.frequencyHz == 0 && zero.mode == HapticMode::Quiet,
                "disabled limiter alone is completely quiet");
    }
    // A live change must release ownership immediately, including an existing envelope.
    const auto disabledLive = fullLimiter.render(s, 0, 0, 0, 0, 0);
    require(disabledLive.output == 0 && disabledLive.mode == HapticMode::Quiet,
            "disabling limiter clears an existing pulse envelope immediately");

    ThrottleRenderer disabledEngine, cleanEngine, halfEngine, fullEngine;
    ThrottleRenderer noWarning, noFlag;
    for (int i = 0; i < 120; ++i) {
        s.time += 1.0 / 60;
        auto clean = s;
        clean.revLimiterActive = false;
        const auto unaffected = disabledEngine.render(s, 0, .48, 0, 0, 0);
        const auto expected = cleanEngine.render(clean, 0, .48, 0, 0, 1);
        sameFrame(unaffected, expected, "limiter zero leaves engine amplitude, mode and frequency untouched");
        const auto half = halfEngine.render(s, 0, .48, 0, 0, .20);
        const auto full = fullEngine.render(s, 0, .48, 0, 0, 1);
        equal(half.loading, unaffected.loading + (full.loading - unaffected.loading) * .20,
                "limiter control also scales its own engine ducking");
        clean.engineWarningsAvailable = false;
        clean.revLimiterActive = true;
        const auto unavailable = noWarning.render(clean, 0, .48, 0, 0, 1);
        clean.engineWarningsAvailable = true;
        clean.revLimiterActive = false;
        sameFrame(unavailable, noFlag.render(clean, 0, .48, 0, 0, 1),
                  "high RPM without a usable limiter flag never invents limiter feedback");
        HapticFrame road{};
        road.output = .3;
        road.frequencyHz = 18;
        road.mode = HapticMode::Surface;
        require(mixSurfaceCue(unaffected, road).mode == HapticMode::Surface,
                "disabled limiter cannot block road effects");
    }

    // Isolate upshift, traction and limiter. Disabled cues cannot alter another cue.
    ThrottleRenderer shiftDisabled, noShift, tractionDisabled, noTraction;
    ThrottleRenderer shiftUnderLimiter, shiftWithoutLimiter, limiterWithoutShift;
    for (int i = 0; i < 90; ++i) {
        s.time += 1.0 / 60;
        s.gear = i < 45 ? 3 : 4;
        auto clean = s;
        clean.gear = 3;
        sameFrame(shiftDisabled.render(s, 0, .48, 0, 0, 0),
                  noShift.render(clean, 0, .48, 0, 0, 0),
                  "upshift zero leaves engine output, mode and pitch unchanged during a real shift");
        auto slipping = s;
        slipping.rearSlipSeverity = .9;
        slipping.tires[2].spinRatio = slipping.tires[3].spinRatio = .2;
        sameFrame(tractionDisabled.render(slipping, 0, .48, .2, 0, .2),
                  noTraction.render(s, 0, .48, .2, 0, .2),
                  "traction zero cannot suppress engine, shift or limiter or own their pitch");
        clean = s;
        clean.revLimiterActive = false;
        const auto shifted = shiftUnderLimiter.render(s, 0, 0, .2, 0, 0);
        sameFrame(shifted, shiftWithoutLimiter.render(clean, 0, 0, .2, 0, 0),
                  "limiter zero does not affect an independent upshift pulse");
        if (i == 45)
            require(shifted.mode == HapticMode::Shift && shifted.output > 0,
                    "upshift remains available while limiter is disabled");
        const auto limited = limiterWithoutShift.render(s, 0, 0, 0, 0, .2);
        require(limited.mode == HapticMode::Limiter && limited.output > 0,
                "limiter remains available with engine and upshift disabled");
    }

    BrakeRenderer downFull, downSoft, downZero, noDown;
    BrakeRenderer absDisabled, noAbs, textureDisabled, noThreshold;
    VehicleState brake{};
    brake.time = 1;
    brake.gear = 4;
    brake.longitudinalG = -.3;
    RenderSettings onlyDown{.91, 1, 0, 0, 1};
    for (int i = 0; i < 90; ++i) {
        brake.time += 1.0 / 60;
        brake.gear = i < 45 ? 4 : 3;
        auto settings = onlyDown;
        const auto full = downFull.render(brake, settings);
        settings.downshiftKick = .2;
        const auto soft = downSoft.render(brake, settings);
        settings.downshiftKick = 0;
        const auto zero = downZero.render(brake, settings);
        auto unchanged = brake;
        unchanged.gear = 4;
        sameFrame(zero, noDown.render(unchanged, settings),
                  "downshift zero leaves no pulse, mode or frequency ownership");
        if (i == 45) {
            require(full.mode == HapticMode::Shift && soft.mode == HapticMode::Shift &&
                    full.output > soft.output && soft.output > 0,
                    "independent brake downshift has full and reduced strength");
            const double transient = 1 - (1.0 / 60) / .16;
            equal(soft.output, std::tanh(transient * .34 * .2 * 1.35) / std::tanh(1.35),
                  "downshift gain is applied before original brake shaping");
        }
        auto absState = brake;
        absState.brake = absState.brakeRaw = .4;
        absState.absSeverity = 1;
        for (auto& tire : absState.tires) tire.combinedUtilization = 1.1;
        auto clean = absState;
        clean.absSeverity = 0;
        settings = {.91, .7, .62, 0, .2};
        sameFrame(absDisabled.render(absState, settings), noAbs.render(clean, settings),
                  "ABS zero cannot duck brake texture or downshift or claim their mode");
        settings = {.91, .7, 0, 0, 0};
        auto lowGrip = clean;
        for (auto& tire : lowGrip.tires) tire.combinedUtilization = 0;
        sameFrame(textureDisabled.render(clean, settings), noThreshold.render(lowGrip, settings),
                  "threshold texture zero leaves loading amplitude, mode and pitch untouched");
    }

    auto nativeConfig = defaults();
    nativeConfig.brake_strength = nativeConfig.traction_strength = nativeConfig.engine_texture = 0;
    nativeConfig.shift_kick = nativeConfig.idle_texture = nativeConfig.surface_strength = 0;
    nativeConfig.effects_gain = 1;
    nativeConfig.limiter_strength = .2;
    Engine nativeLimiter;
    require(nativeLimiter.configure(nativeConfig), "independent limiter accepted by native configuration");
    ThrottleRenderer direct;
    const auto expected = direct.render(s, 0, 0, 0, 0, .2);
    const auto actual = nativeLimiter.update(true, true, s, s.time);
    equal(actual.throttle_raw, expected.output, "native wrapper forwards limiter strength");
    nativeConfig.limiter_strength = 0;
    require(nativeLimiter.configure(nativeConfig), "native limiter can be disabled");
    require(nativeLimiter.update(true, true, s, s.time).throttle_raw == 0,
            "native wrapper honors disabled limiter");
    nativeConfig.brake_strength = .7;
    nativeConfig.brake_texture = nativeConfig.abs_punch = 0;
    nativeConfig.downshift_kick = .2;
    require(nativeLimiter.configure(nativeConfig), "native downshift strength accepted");
    brake.gear = 4;
    (void)nativeLimiter.update(true, true, brake, brake.time);
    brake.gear = 3;
    brake.time += 1.0 / 60;
    require(nativeLimiter.update(true, true, brake, brake.time).brake_raw > 0,
            "native wrapper enables independent downshift");
    nativeConfig.downshift_kick = 0;
    require(nativeLimiter.configure(nativeConfig), "native downshift can be disabled");
    brake.gear = 4;
    (void)nativeLimiter.update(true, true, brake, brake.time);
    brake.gear = 3;
    brake.time += 1.0 / 60;
    require(nativeLimiter.update(true, true, brake, brake.time).brake_raw == 0,
            "native wrapper forwards disabled downshift");
}
}

int main() {
    independentEffects();
    auto c = defaults();
    require(c.effects_gain == 2.1 && c.shift_kick == .20 && c.limiter_strength == .20 &&
            c.downshift_kick == .20, "new baseline and restrained transient defaults");
    c.limiter_strength = c.downshift_kick = 1;
    c.effects_gain = 1;
    Engine bridge;
    require(bridge.configure(c), "unity gain parity configuration");
    auto amplifiedConfig = c;
    amplifiedConfig.effects_gain = 3;
    const simagic::IntensityCurve amplifiedLow{12, 20, 28, 36};
    const simagic::IntensityCurve amplifiedHigh{40, 60, 80, 100};
    for (int i = 0; i < 4; ++i) {
        amplifiedConfig.brake_minimum[i] = amplifiedConfig.throttle_minimum[i] = amplifiedLow[i];
        amplifiedConfig.brake_maximum[i] = amplifiedConfig.throttle_maximum[i] = amplifiedHigh[i];
    }
    Engine amplified;
    require(amplified.configure(amplifiedConfig), "triple gain with frequency-dependent calibration");
    auto mutedConfig = amplifiedConfig;
    mutedConfig.effects_gain = 0;
    Engine muted;
    require(muted.configure(mutedConfig), "zero gain accepted with positive calibration minimum");
    int clippedSamples = 0;
    BrakeRenderer directBrake;
    ThrottleRenderer directThrottle;
    SurfaceRenderer directSurface;
    RenderSettings settings{c.grip_threshold, c.brake_strength, c.brake_texture, c.abs_punch};
    const simagic::IntensityCurve low{0, 0, 0, 0}, high{35, 35, 35, 35};
    for (int i = 0; i < 1500; ++i) {
        const auto state = fixture(i);
        auto b = directBrake.render(state, settings);
        auto t = directThrottle.render(state, c.traction_strength, c.engine_texture, c.shift_kick,
                                      c.idle_texture);
        auto s = directSurface.render(state, c.surface_strength);
        s.brake.output *= c.brake_strength;
        b = mixSurfaceCue(b, s.brake);
        t = mixSurfaceCue(t, s.throttle);
        const auto actual = bridge.update(true, true, state, state.time);
        equal(actual.brake_raw, b.output, "bridge brake exactly matches upstream render+mix");
        equal(actual.throttle_raw, t.output, "bridge throttle exactly matches upstream render+mix");
        require(actual.brake_mode == static_cast<int>(b.mode), "brake mode parity");
        require(actual.throttle_mode == static_cast<int>(t.mode), "throttle mode parity");
        require(actual.brake_intensity == simagic::calibrateIntensity(
                    static_cast<int>(std::lround(b.output * 100)),
                    static_cast<int>(std::lround(b.frequencyHz)), low, high), "brake calibration parity");
        require(actual.throttle_intensity == simagic::calibrateIntensity(
                    static_cast<int>(std::lround(t.output * 100)),
                    static_cast<int>(std::lround(t.frequencyHz)), low, high), "throttle calibration parity");
        require(actual.brake_intensity <= 35 && actual.throttle_intensity <= 35, "conservative ceiling");
        require(actual.brake_hz == (actual.brake_intensity > 0 ?
                    static_cast<int>(std::lround(std::clamp(b.frequencyHz, 10.0, 50.0))) : 0),
                "unity gain brake frequency parity");
        require(actual.throttle_hz == (actual.throttle_intensity > 0 ?
                    static_cast<int>(std::lround(std::clamp(t.frequencyHz, 10.0, 50.0))) : 0),
                "unity gain throttle frequency parity");
        const auto boosted = amplified.update(true, true, state, state.time);
        equal(boosted.brake_raw, actual.brake_raw, "gain does not change brake raw diagnostic");
        equal(boosted.throttle_raw, actual.throttle_raw, "gain does not change throttle raw diagnostic");
        require(boosted.brake_mode == actual.brake_mode && boosted.throttle_mode == actual.throttle_mode,
                "gain preserves renderer modes");
        auto verifyBoost = [&](const HapticFrame& frame, int intensity, int hz) {
            const int frequency = static_cast<int>(std::lround(std::clamp(frame.frequencyHz, 10.0, 50.0)));
            const int requested = static_cast<int>(std::lround(std::clamp(frame.output * 3, 0.0, 1.0) * 100));
            require(intensity == simagic::calibrateIntensity(requested, frequency, amplifiedLow, amplifiedHigh),
                    "gain applied to mixed signal before frequency calibration");
            require(intensity <= simagic::interpolateIntensityCurve(amplifiedHigh, frequency) && intensity <= 100,
                    "amplified signal never exceeds calibrated or protocol ceiling");
            require(hz == (intensity > 0 ? frequency : 0), "amplified command frequency or complete silence");
            if (frame.output * 3 >= 1) {
                ++clippedSamples;
                require(intensity == simagic::interpolateIntensityCurve(amplifiedHigh, frequency),
                        "clipped signal reaches exact calibrated maximum");
            }
        };
        verifyBoost(b, boosted.brake_intensity, boosted.brake_hz);
        verifyBoost(t, boosted.throttle_intensity, boosted.throttle_hz);
        const auto silent = muted.update(true, true, state, state.time);
        equal(silent.brake_raw, actual.brake_raw, "zero gain retains unamplified diagnostic");
        equal(silent.throttle_raw, actual.throttle_raw, "zero gain retains throttle diagnostic");
        require(!silent.brake_intensity && !silent.brake_hz && !silent.throttle_intensity && !silent.throttle_hz,
                "zero gain is silent even with nonzero calibration minima");
        const auto repeated = bridge.update(false, true, {}, state.time + .004);
        require(!repeated.new_sample && !repeated.stale, "duplicate frame status");
        equal(repeated.brake_raw, actual.brake_raw, "duplicate frame does not advance filters");
    }
    require(clippedSamples > 0, "fixture exercises triple gain saturation");

    // A sub-percent source must be multiplied before rounding; its valid
    // frequency must survive even when the unity-gain command would be zero.
    auto weakConfig = c;
    weakConfig.brake_strength = .01;
    weakConfig.brake_texture = weakConfig.abs_punch = weakConfig.traction_strength = 0;
    weakConfig.engine_texture = weakConfig.shift_kick = weakConfig.idle_texture = weakConfig.surface_strength = 0;
    for (int i = 0; i < 4; ++i) {
        weakConfig.brake_minimum[i] = weakConfig.throttle_minimum[i] = 20;
        weakConfig.brake_maximum[i] = weakConfig.throttle_maximum[i] = 100;
    }
    VehicleState weakState{};
    weakState.time = 1;
    weakState.brake = weakState.brakeRaw = 1;
    Engine weak;
    require(weak.configure(weakConfig), "weak signal unity config");
    const auto weakUnity = weak.update(true, true, weakState, 1);
    require(weakUnity.brake_raw > 0 && weakUnity.brake_raw < .005 &&
            weakUnity.brake_intensity == 0 && weakUnity.brake_hz == 0,
            "weak signal initially rounds below first active request");
    weakConfig.effects_gain = 3;
    require(weak.configure(weakConfig), "weak signal triple config");
    const auto weakTriple = weak.update(true, true, weakState, 1);
    equal(weakTriple.brake_raw, weakUnity.brake_raw, "weak signal raw remains unchanged");
    require(weakTriple.brake_intensity == 20 && weakTriple.brake_hz == 14,
            "triple gain rescues sub-percent signal before rounding and preserves its frequency");
    weakConfig.effects_gain = 4.2;
    require(weak.configure(weakConfig), "maximum supported gain accepted");
    VehicleState zeroState{};
    zeroState.time = 1;
    const auto zeroSource = weak.update(true, true, zeroState, 1);
    require(zeroSource.brake_raw == 0 && zeroSource.throttle_raw == 0 &&
            !zeroSource.brake_intensity && !zeroSource.throttle_intensity &&
            !zeroSource.brake_hz && !zeroSource.throttle_hz,
            "maximum gain and positive minima never revive a zero source");

    Engine stale;
    auto state = fixture(35);
    state.time = 4;
    const auto initial = stale.update(true, true, state, 10);
    require(initial.brake_intensity > 0, "stale fixture has active output");
    const auto before = stale.update(false, true, {}, 10.249);
    require(before.brake_intensity == initial.brake_intensity && !before.stale, "output retained under 250ms");
    const auto expired = stale.update(false, true, {}, 10.250);
    require(expired.stale && !expired.driving_active && !expired.new_sample && expired.connected &&
            expired.brake_intensity == 0 && expired.throttle_intensity == 0 &&
            expired.brake_hz == 0 && expired.throttle_hz == 0 && expired.brake_raw == 0,
            "stalled connected mapping goes quiet exactly at 250ms");
    Engine fresh;
    state.time += 1;
    const auto recovered = stale.update(true, true, state, 11);
    const auto clean = fresh.update(true, true, state, 11);
    equal(recovered.brake_raw, clean.brake_raw, "stale recovery resets brake state");
    equal(recovered.throttle_raw, clean.throttle_raw, "stale recovery resets throttle state");
    const auto disconnected = stale.update(false, false, {}, 11.001);
    require(disconnected.stale && !disconnected.connected && !disconnected.brake_intensity,
            "disconnect stops immediately");

    Engine inactive;
    (void)inactive.update(true, true, state, 11);
    state.drivingActive = false;
    const auto parked = inactive.update(true, true, state, 11.017);
    require(!parked.driving_active && !parked.stale && parked.new_sample &&
            !parked.brake_intensity && !parked.throttle_intensity, "garage or replay stops immediately");
    state.drivingActive = true;
    const auto resumed = inactive.update(true, true, state, 11.033);
    Engine afterGarage;
    const auto garageClean = afterGarage.update(true, true, state, 11.033);
    equal(resumed.brake_raw, garageClean.brake_raw, "garage recovery resets filters");

    auto invalid = c;
    invalid.brake_strength = std::numeric_limits<double>::quiet_NaN();
    require(!validConfig(invalid) && !bridge.configure(invalid), "NaN config rejected");
    for (double gain : {-0.01, 4.2001, std::numeric_limits<double>::quiet_NaN(),
                        std::numeric_limits<double>::infinity()}) {
        invalid = c;
        invalid.effects_gain = gain;
        require(!validConfig(invalid) && !bridge.configure(invalid), "nonfinite or out-of-range gain rejected");
    }
    for (double strength : {-0.01, 1.01, std::numeric_limits<double>::quiet_NaN(),
                            std::numeric_limits<double>::infinity()}) {
        invalid = c;
        invalid.limiter_strength = strength;
        require(!validConfig(invalid) && !bridge.configure(invalid), "invalid limiter strength rejected");
        invalid = c;
        invalid.downshift_kick = strength;
        require(!validConfig(invalid) && !bridge.configure(invalid), "invalid downshift strength rejected");
    }
    invalid = c;
    invalid.throttle_minimum[2] = 90;
    require(!bridge.configure(invalid), "inverted calibration rejected");
    invalid = c;
    invalid.version = 999;
    require(!bridge.configure(invalid), "unknown ABI rejected");
    invalid.version = 1;
    require(!bridge.configure(invalid), "old ABI rejected after independent transient controls");
    invalid.version = 2;
    require(!bridge.configure(invalid), "ABI v2 rejected after limiter/downshift field addition");
    invalid = c;
    invalid.size--;
    require(!bridge.configure(invalid), "wrong struct size rejected");
    auto disabled = c;
    disabled.brake_enabled = 0;
    require(bridge.configure(disabled), "valid settings accepted");
    auto disabledOutput = bridge.update(true, true, state, 12);
    require(!disabledOutput.brake_intensity && !disabledOutput.brake_hz &&
            disabledOutput.brake_raw == 0, "individual brake channel disable");
    require(bridge.configure(c), "restore defaults");
    disabledOutput = bridge.update(false, true, {}, 12.01);
    require(disabledOutput.stale && !disabledOutput.brake_intensity,
            "configuration changes wait for next genuine sample");

    PfConfig apiConfig{};
    apiConfig.size = sizeof(apiConfig);
    apiConfig.version = PF_ABI_VERSION;
    require(pf_default_config(&apiConfig) == PF_OK && apiConfig.brake_maximum[3] == 35 &&
            apiConfig.effects_gain == 2.1 && apiConfig.shift_kick == .20 &&
            apiConfig.limiter_strength == .20 && apiConfig.downshift_kick == .20 && apiConfig.version == 4,
            "C ABI default settings");
    require(pf_configure(nullptr, &apiConfig) == PF_INVALID_ARGUMENT, "null handle rejected");
    auto* runtime = pf_create();
    require(runtime != nullptr, "C ABI runtime allocation");
    require(pf_configure(runtime, &apiConfig) == PF_OK, "C ABI settings marshal");
    PfOutput apiOutput{};
    apiOutput.size = sizeof(apiOutput);
    apiOutput.version = PF_ABI_VERSION;
    require(pf_tick(runtime, &apiOutput) == PF_OK, "real telemetry adapter can poll");
    if (!apiOutput.connected)
        require(apiOutput.stale && !apiOutput.brake_intensity && !apiOutput.throttle_intensity,
                "no iRacing mapping stays silent");
    pf_destroy(runtime);
    pf_destroy(nullptr);
    std::cout << "All bridge tests passed (1500 unity/gain3/gain0 samples, gain boundaries, stale timeout, reset, limiter/downshift controls, disabled-effect isolation, ABI v3).\n";
}
