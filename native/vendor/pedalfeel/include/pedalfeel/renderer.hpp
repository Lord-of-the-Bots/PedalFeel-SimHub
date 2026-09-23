#pragma once

#include "pedalfeel/types.hpp"

namespace pedalfeel {

inline constexpr double IdleTextureFrequencyHz{16.0};
inline constexpr double IdleTextureMaximumOutput{0.18};

struct SurfaceHaptics {
    HapticFrame brake{};
    HapticFrame throttle{};
};

class SurfaceRenderer {
  public:
    [[nodiscard]] SurfaceHaptics render(const VehicleState& state, double strength = 0.28) noexcept;

  private:
    double previousTime_{};
    double previousSeverity_{};
    double cooldown_{};
    double eventAge_{1.0};
    double eventDuration_{.09};
    double eventSeverity_{};
    double rumbleEnvelope_{};
};

// Surface events sit above background/shift character but never replace tire
// intervention information such as ABS, lock, traction loss or the threshold cue.
[[nodiscard]] HapticFrame mixSurfaceCue(const HapticFrame& base,
                                        const HapticFrame& surface) noexcept;

// A real rigid pedal box carries a small amount of engine and driveline motion into both feet.
// This adds only character cues to an otherwise quiet brake; tire and brake information owns it.
[[nodiscard]] HapticFrame mixBrakeChassisCue(const HapticFrame& brake,
                                             const HapticFrame& throttle) noexcept;

class BrakeRenderer {
  public:
    [[nodiscard]] HapticFrame render(const VehicleState& state,
                                     const RenderSettings& settings,
                                     bool includeLoading = true) noexcept;

  private:
    double previousTime_{};
    double thresholdEnvelope_{};
    double lockEnvelope_{};
    double absEnvelope_{};
    double lockTransient_{};
    double downshiftTransient_{};
    double brakeContextRemaining_{};
    int lastForwardGear_{};
    std::uint8_t previousLockMask_{};
};

class ThrottleRenderer {
  public:
    [[nodiscard]] HapticFrame render(const VehicleState& state, double tractionStrength = 0.65,
                                     double engineTexture = 0.48, double shiftKick = 0.45,
                                     double idleTexture = 0.28, double limiterStrength = 1.0) noexcept;

  private:
    double previousTime_{};
    double slipEnvelope_{};
    double tractionEnvelope_{};
    double characterEnvelope_{};
    double limiterEnvelope_{};
    double shiftTransient_{};
    int lastForwardGear_{};
    double previousThrottleRaw_{};
    double previousPedalDemand_{};
    double previousEngineRpm_{};
};

[[nodiscard]] const wchar_t* modeName(HapticMode mode) noexcept;
[[nodiscard]] HapticMode dominantMode(const HapticFrame& brake,
                                      const HapticFrame& throttle) noexcept;

} // namespace pedalfeel
