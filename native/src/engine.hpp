#pragma once
#include "pedalfeel_api.h"
#include "pedalfeel/renderer.hpp"

namespace pedalfeel_plugin {
PfConfig defaults() noexcept;
bool validConfig(const PfConfig& config) noexcept;
// Shared final command conversion for live telemetry and isolated previews.
void physical(const pedalfeel::HapticFrame& frame, double gain, const int32_t* minimum,
              const int32_t* maximum, int32_t& hz, int32_t& intensity) noexcept;

// Deterministic bridge used by both the exported DLL and tests. Clock and frame
// availability are supplied by the caller; only the DLL adapter reads iRacing.
class Engine {
public:
    Engine() noexcept;
    bool configure(const PfConfig& config) noexcept;
    PfOutput update(bool fresh, bool connected, const pedalfeel::VehicleState& state,
                    double monotonicSeconds) noexcept;
private:
    void resetRenderers() noexcept;
    void quiet() noexcept;
    PfConfig config_{};
    PfOutput output_{};
    pedalfeel::BrakeRenderer brake_;
    pedalfeel::ThrottleRenderer throttle_;
    pedalfeel::SurfaceRenderer surface_;
    bool haveSample_{};
    bool running_{};
    double lastSampleAt_{};
    double lastSessionTime_{};
};
}
