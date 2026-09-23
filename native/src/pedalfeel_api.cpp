#include "pedalfeel_api.h"
#include "engine.hpp"
#include "preview.hpp"
#include "pedalfeel/iracing_telemetry.hpp"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <memory>

namespace {
using Clock = std::chrono::steady_clock;
struct Runtime {
    pedalfeel::IRacingTelemetry telemetry;
    pedalfeel_plugin::Engine engine;
    Clock::time_point lastSample = Clock::now();
};
static_assert(offsetof(PfConfig, grip_threshold) == 16);
static_assert(offsetof(PfConfig, brake_minimum) == 88);
static_assert(offsetof(PfConfig, effects_gain) == 152);
static_assert(offsetof(PfConfig, limiter_strength) == 160);
static_assert(offsetof(PfConfig, downshift_kick) == 168);
static_assert(offsetof(PfOutput, brake_raw) == 48);
void clearOutput(PfOutput& output) noexcept {
    output = {};
    output.size = sizeof(output);
    output.version = PF_ABI_VERSION;
    output.stale = 1;
    output.sample_age_ms = -1;
}
}

extern "C" PF_API void* PF_CALL pf_create(void) {
    try { return new Runtime; } catch (...) { return nullptr; }
}
extern "C" PF_API void PF_CALL pf_destroy(void* handle) {
    delete static_cast<Runtime*>(handle);
}
extern "C" PF_API int32_t PF_CALL pf_configure(void* handle, const PfConfig* config) {
    if (!handle || !config) return PF_INVALID_ARGUMENT;
    try {
        return static_cast<Runtime*>(handle)->engine.configure(*config) ? PF_OK : PF_INVALID_ARGUMENT;
    } catch (...) { return PF_INTERNAL_ERROR; }
}
extern "C" PF_API int32_t PF_CALL pf_tick(void* handle, PfOutput* output) {
    if (!output || output->size != sizeof(*output) || output->version != PF_ABI_VERSION)
        return PF_INVALID_ARGUMENT;
    if (!handle) { clearOutput(*output); return PF_INVALID_ARGUMENT; }
    try {
        auto& runtime = *static_cast<Runtime*>(handle);
        const auto now = Clock::now();
        const double dt = std::clamp(std::chrono::duration<double>(now - runtime.lastSample).count(),
                                     .001, .05);
        pedalfeel::VehicleState state{};
        const bool fresh = runtime.telemetry.sampleLatest(dt, state);
        if (fresh) runtime.lastSample = now;
        *output = runtime.engine.update(fresh, runtime.telemetry.connected(), state,
                                       std::chrono::duration<double>(now.time_since_epoch()).count());
        return PF_OK;
    } catch (...) {
        clearOutput(*output);
        return PF_INTERNAL_ERROR;
    }
}
extern "C" PF_API int32_t PF_CALL pf_default_config(PfConfig* config) {
    if (!config || config->size != sizeof(*config) || config->version != PF_ABI_VERSION)
        return PF_INVALID_ARGUMENT;
    *config = pedalfeel_plugin::defaults();
    return PF_OK;
}
extern "C" PF_API int32_t PF_CALL pf_preview(const PfConfig* config, int32_t effect,
                                             double elapsed_seconds, PfOutput* output) {
    if (!config || !output || output->size != sizeof(*output) || output->version != PF_ABI_VERSION ||
        !pedalfeel_plugin::validConfig(*config) || !pedalfeel_plugin::validPreviewEffect(effect) ||
        !std::isfinite(elapsed_seconds) || elapsed_seconds < 0)
        return PF_INVALID_ARGUMENT;
    // No runtime handle, shared memory or device access: repeated calls at the
    // same time/settings are identical and cannot change live engine filters.
    *output = pedalfeel_plugin::preview(*config, effect, elapsed_seconds);
    return PF_OK;
}
