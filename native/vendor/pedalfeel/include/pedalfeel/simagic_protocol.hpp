#pragma once

#include <algorithm>
#include <array>
#include <cstdint>

namespace pedalfeel::simagic {

inline constexpr std::uint16_t VendorId = 0x3670;
inline constexpr std::uint16_t ProductId = 0x0902;
inline constexpr std::uint8_t ClutchChannel = 0;
inline constexpr std::uint8_t BrakeChannel = 1;
inline constexpr std::uint8_t ThrottleChannel = 2;
inline constexpr std::size_t ReportLength = 49;
inline constexpr std::size_t CalibrationPointCount = 4;
inline constexpr std::array<int, CalibrationPointCount> CalibrationFrequenciesHz{16, 25, 35, 50};
using IntensityCurve = std::array<int, CalibrationPointCount>;

using FeatureReport = std::array<std::uint8_t, ReportLength>;

[[nodiscard]] constexpr int calibrateIntensity(int requestedPercent, int minimumPercent,
                                               int maximumPercent) noexcept {
    requestedPercent = std::clamp(requestedPercent, 0, 100);
    minimumPercent = std::clamp(minimumPercent, 0, 100);
    maximumPercent = std::clamp(maximumPercent, minimumPercent, 100);
    if (requestedPercent == 0)
        return 0;
    // Preserve a real off state, then map the first active command exactly to
    // the user's tactile threshold and full output exactly to their ceiling.
    return minimumPercent + ((requestedPercent - 1) * (maximumPercent - minimumPercent) + 49) / 99;
}

[[nodiscard]] constexpr int interpolateIntensityCurve(const IntensityCurve& curve,
                                                       int frequencyHz) noexcept {
    if (frequencyHz <= CalibrationFrequenciesHz.front())
        return curve.front();
    if (frequencyHz >= CalibrationFrequenciesHz.back())
        return curve.back();
    for (std::size_t i = 0; i + 1 < CalibrationPointCount; ++i) {
        const int lowHz = CalibrationFrequenciesHz[i];
        const int highHz = CalibrationFrequenciesHz[i + 1];
        if (frequencyHz > highHz)
            continue;
        const int span = highHz - lowHz;
        const int numerator = (curve[i + 1] - curve[i]) * (frequencyHz - lowHz);
        const int rounded =
            numerator >= 0 ? (numerator + span / 2) / span : (numerator - span / 2) / span;
        return curve[i] + rounded;
    }
    return curve.back();
}

[[nodiscard]] constexpr int calibrateIntensity(int requestedPercent, int frequencyHz,
                                               const IntensityCurve& minimumCurve,
                                               const IntensityCurve& maximumCurve) noexcept {
    const int minimum = interpolateIntensityCurve(minimumCurve, frequencyHz);
    const int maximum = interpolateIntensityCurve(maximumCurve, frequencyHz);
    return calibrateIntensity(requestedPercent, minimum, std::max(minimum, maximum));
}

[[nodiscard]] constexpr FeatureReport makeReport(std::uint8_t channel, bool enabled,
                                                 int frequencyHz, int intensityPercent) noexcept {
    FeatureReport report{};
    const bool active = enabled && frequencyHz > 0 && intensityPercent > 0;
    report[0] = 0xF1;
    report[1] = 0xEC;
    report[2] = std::min<std::uint8_t>(channel, ThrottleChannel);
    report[3] = active ? 0x01 : 0x00;
    report[4] = active ? static_cast<std::uint8_t>(std::clamp(frequencyHz, 10, 50)) : 0x00;
    report[5] = active ? static_cast<std::uint8_t>(std::clamp(intensityPercent, 0, 100)) : 0x00;
    return report;
}

} // namespace pedalfeel::simagic
