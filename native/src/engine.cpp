#include "engine.hpp"
#include "pedalfeel/simagic_protocol.hpp"
#include <algorithm>
#include <cmath>
#include <iterator>

namespace pedalfeel_plugin {
namespace {
bool range(double value, double minimum, double maximum) noexcept {
    return std::isfinite(value) && value >= minimum && value <= maximum;
}
bool curve(const int32_t* minimum, const int32_t* maximum) noexcept {
    for (int i = 0; i < 4; ++i)
        if (minimum[i] < 0 || maximum[i] > 100 || minimum[i] > maximum[i])
            return false;
    return true;
}
}
void physical(const pedalfeel::HapticFrame& frame, double gain, const int32_t* minimum,
              const int32_t* maximum, int32_t& hz, int32_t& intensity) noexcept {
    hz = intensity = 0;
    if (!std::isfinite(frame.output) || !std::isfinite(frame.frequencyHz) ||
        frame.output <= 0 || frame.frequencyHz < 10)
        return;
    const int requested = static_cast<int>(std::lround(std::clamp(frame.output * gain, 0.0, 1.0) * 100));
    const int frequency = static_cast<int>(std::lround(std::clamp(frame.frequencyHz, 10.0, 50.0)));
    const pedalfeel::simagic::IntensityCurve low{minimum[0], minimum[1], minimum[2], minimum[3]};
    const pedalfeel::simagic::IntensityCurve high{maximum[0], maximum[1], maximum[2], maximum[3]};
    intensity = pedalfeel::simagic::calibrateIntensity(requested, frequency, low, high);
    hz = intensity > 0 ? frequency : 0;
}

PfConfig defaults() noexcept {
    PfConfig c{};
    c.size = sizeof(c);
    c.version = PF_ABI_VERSION;
    c.brake_enabled = c.throttle_enabled = 1;
    c.grip_threshold = .91;
    c.brake_strength = .70;
    c.brake_texture = .62;
    c.abs_punch = .72;
    c.traction_strength = .65;
    c.engine_texture = .48;
    c.shift_kick = .20;
    c.idle_texture = .28;
    c.surface_strength = .28;
    c.effects_gain = 2.1;
    c.limiter_strength = .20;
    c.downshift_kick = .20;
    std::fill(std::begin(c.brake_maximum), std::end(c.brake_maximum), 35);
    std::fill(std::begin(c.throttle_maximum), std::end(c.throttle_maximum), 35);
    return c;
}

bool validConfig(const PfConfig& c) noexcept {
    return c.size == sizeof(c) && c.version == PF_ABI_VERSION &&
           (c.brake_enabled == 0 || c.brake_enabled == 1) &&
           (c.throttle_enabled == 0 || c.throttle_enabled == 1) &&
           range(c.grip_threshold, .75, 1.05) && range(c.brake_strength, 0, 1) &&
           range(c.brake_texture, 0, 1) && range(c.abs_punch, 0, 1) &&
           range(c.traction_strength, 0, 1) && range(c.engine_texture, 0, 1) &&
           range(c.shift_kick, 0, 1) && range(c.idle_texture, 0, 1) &&
           range(c.surface_strength, 0, 1) && range(c.effects_gain, 0, 4.2) &&
           range(c.limiter_strength, 0, 1) &&
           range(c.downshift_kick, 0, 1) &&
           curve(c.brake_minimum, c.brake_maximum) &&
           curve(c.throttle_minimum, c.throttle_maximum);
}

Engine::Engine() noexcept : config_(defaults()) {
    quiet();
}

void Engine::resetRenderers() noexcept {
    brake_ = {};
    throttle_ = {};
    surface_ = {};
    running_ = false;
}

void Engine::quiet() noexcept {
    output_ = {};
    output_.size = sizeof(output_);
    output_.version = PF_ABI_VERSION;
    output_.stale = 1;
    output_.sample_age_ms = -1;
}

bool Engine::configure(const PfConfig& config) noexcept {
    if (!validConfig(config))
        return false;
    config_ = config;
    resetRenderers();
    haveSample_ = false;
    quiet();
    return true;
}

PfOutput Engine::update(bool fresh, bool connected, const pedalfeel::VehicleState& state,
                        double now) noexcept {
    output_.new_sample = 0;
    output_.connected = connected ? 1 : 0;
    if (!std::isfinite(now) || !connected) {
        resetRenderers();
        haveSample_ = false;
        quiet();
        output_.connected = connected ? 1 : 0;
        return output_;
    }

    if (fresh) {
        if (!haveSample_ || !running_ || state.time < lastSessionTime_ ||
            now < lastSampleAt_ || now - lastSampleAt_ >= .250)
            resetRenderers();
        haveSample_ = true;
        lastSampleAt_ = now;
        lastSessionTime_ = state.time;
        const bool active = state.drivingActive && std::isfinite(state.time);
        if (!active)
            resetRenderers();
        pedalfeel::HapticFrame brake{}, throttle{};
        if (active) {
            const pedalfeel::RenderSettings settings{config_.grip_threshold, config_.brake_strength,
                                                    config_.brake_texture, config_.abs_punch,
                                                    config_.downshift_kick};
            brake = brake_.render(state, settings);
            throttle = throttle_.render(state, config_.traction_strength, config_.engine_texture,
                                        config_.shift_kick, config_.idle_texture, config_.limiter_strength);
            brake = pedalfeel::mixBrakeChassisCue(brake, throttle);
            const auto surface = surface_.render(state, config_.surface_strength);
            brake = pedalfeel::mixSurfaceCue(brake, surface.brake);
            throttle = pedalfeel::mixSurfaceCue(throttle, surface.throttle);
            running_ = true;
        }
        if (!config_.brake_enabled) brake = {};
        if (!config_.throttle_enabled) throttle = {};
        if (!std::isfinite(brake.output) || !std::isfinite(brake.frequencyHz)) brake = {};
        if (!std::isfinite(throttle.output) || !std::isfinite(throttle.frequencyHz)) throttle = {};
        quiet();
        output_.new_sample = 1;
        output_.connected = 1;
        output_.driving_active = active ? 1 : 0;
        output_.stale = 0;
        output_.sample_age_ms = 0;
        output_.session_time = std::isfinite(state.time) ? state.time : 0;
        output_.brake_raw = brake.output;
        output_.throttle_raw = throttle.output;
        output_.brake_mode = static_cast<int32_t>(brake.mode);
        output_.throttle_mode = static_cast<int32_t>(throttle.mode);
        physical(brake, config_.effects_gain, config_.brake_minimum, config_.brake_maximum,
                 output_.brake_hz, output_.brake_intensity);
        physical(throttle, config_.effects_gain, config_.throttle_minimum, config_.throttle_maximum,
                 output_.throttle_hz, output_.throttle_intensity);
        return output_;
    }

    const double age = haveSample_ ? std::max(0.0, now - lastSampleAt_) : -1;
    if (!haveSample_ || now < lastSampleAt_ || age >= .250) {
        if (running_) resetRenderers();
        quiet();
        output_.connected = 1;
        output_.session_time = std::isfinite(lastSessionTime_) ? lastSessionTime_ : 0;
    }
    output_.sample_age_ms = age < 0 ? -1 : age * 1000;
    return output_;
}
}
