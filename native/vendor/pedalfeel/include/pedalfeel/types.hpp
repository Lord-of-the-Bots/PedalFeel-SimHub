#pragma once

#include <array>
#include <cstdint>

namespace pedalfeel {

enum class Wheel : std::uint8_t { LeftFront, RightFront, LeftRear, RightRear };

struct TireState {
    double wheelSpeed{};
    double slipRatio{};
    double spinRatio{};
    double verticalLoad{};
    double longitudinalUtilization{};
    double lateralUtilization{};
    double combinedUtilization{};
    double confidence{1.0};
    bool locking{};
    bool spinning{};
};

struct VehicleState {
    double time{};
    double speedKph{};
    double brake{};
    double brakeRaw{};
    double throttle{};
    double throttleRaw{};
    // iRacing reports 1 when the clutch is fully coupled and 0 when disengaged.
    double clutch{1.0};
    double steering{};
    double longitudinalG{};
    double lateralG{};
    double engineRpm{};
    double idleRpm{900.0};
    double redlineRpm{9000.0};
    double shiftLightFirstRpm{6500.0};
    double shiftLightLastRpm{8200.0};
    double shiftRpm{8500.0};
    double shiftPower{};
    bool revLimiterActive{};
    bool engineWarningsAvailable{};
    double absSeverity{};
    // Estimated driven-wheel traction loss; not a direct iRacing TC intervention flag.
    double rearSlipSeverity{};
    double rearWheelspinRatio{};
    double drivelineOverspeedRatio{};
    double throttleCutSeverity{};
    double tractionConfidence{};
    // Sparse suspension-impact cue. Direction is -1 for left, +1 for right and
    // zero when the impact is chassis-wide or cannot be localized confidently.
    double surfaceImpactSeverity{};
    double surfaceImpactDirection{};
    double surfaceImpactConfidence{};
    // Direct per-tire iRacing rumble-strip signal, separate from inferred impacts.
    double rumbleStripSeverity{};
    double rumbleStripFrequencyHz{};
    double rumbleStripDirection{};
    bool surfaceTelemetryAvailable{};
    bool rumbleStripTelemetryAvailable{};
    bool rearWheelTelemetryAvailable{};
    bool drivelineSlipAvailable{};
    bool throttleRawAvailable{};
    bool brakeRawAvailable{};
    bool clutchAvailable{};
    bool shiftPowerAvailable{};
    bool drivingActive{true};
    int trackSurface{};
    int trackSurfaceMaterial{};
    int gear{};
    std::array<TireState, 4> tires{};
};

struct RenderSettings {
    double gripThreshold{0.91};
    double strength{0.70};
    double texture{0.62};
    double absPunch{0.72};
    // SimHub extension. One preserves the original unadjustable downshift pulse.
    double downshiftKick{1.0};
};

enum class HapticMode {
    Quiet,
    Idle,
    Loading,
    Threshold,
    Locking,
    Abs,
    Traction,
    Surface,
    RumbleStrip,
    Shift,
    Limiter
};

struct HapticFrame {
    double loading{};
    double threshold{};
    double lock{};
    double abs{};
    double output{};
    double frequencyHz{};
    HapticMode mode{HapticMode::Quiet};
};

} // namespace pedalfeel
