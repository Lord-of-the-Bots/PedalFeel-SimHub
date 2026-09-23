#pragma once

#include "pedalfeel/telemetry.hpp"
#include <array>
#include <chrono>
#include <cstddef>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

namespace pedalfeel {

// Thin, dependency-free reader for iRacing's documented shared-memory SDK.
class IRacingTelemetry final : public TelemetrySource {
  public:
    IRacingTelemetry() = default;
    ~IRacingTelemetry() override;
    IRacingTelemetry(const IRacingTelemetry&) = delete;
    IRacingTelemetry& operator=(const IRacingTelemetry&) = delete;

    [[nodiscard]] VehicleState sample(double dtSeconds) override;
    // Returns true only when a newly published iRacing frame was decoded. This
    // lets latency-sensitive callers poll promptly without advancing filters or
    // re-rendering the same 60 Hz telemetry frame multiple times.
    bool sampleLatest(double dtSeconds, VehicleState& state);
    void seek(double) override {}
    [[nodiscard]] bool connected() const noexcept;
    [[nodiscard]] const std::wstring& statusText() const noexcept {
        return statusText_;
    }
    // Read-only access used by the opt-in traction capture. These values never
    // participate in haptic rendering until a signal has been validated.
    [[nodiscard]] bool hasDiagnosticVariable(std::string_view name) const;
    [[nodiscard]] int diagnosticCount(std::string_view name) const;
    [[nodiscard]] double diagnosticValue(std::string_view name, int index = 0,
                                         double fallback = 0.0) const;

  private:
    struct Variable {
        int type{};
        int offset{};
        int count{};
    };
    bool tryConnect();
    void close();
    bool copyLatestFrame();
    void readSessionMetadata();
    [[nodiscard]] double scalar(const char* name, double fallback = 0.0) const noexcept;
    [[nodiscard]] bool boolean(const char* name) const noexcept;
    void estimate(VehicleState& state, double dt);
    void estimateSurface(VehicleState& state, double dt);

#ifdef _WIN32
    HANDLE mapping_{nullptr};
    const std::byte* memory_{nullptr};
    std::size_t mappingSize_{};
    [[nodiscard]] bool mappedRange(int offset, std::size_t length) const noexcept;
#endif
    std::unordered_map<std::string, Variable> variables_;
    std::vector<std::byte> frame_;
    std::array<double, 4> slowSlip_{};
    std::array<double, 4> previousSlip_{};
    std::array<double, 4> wheelScale_{1.0, 1.0, 1.0, 1.0};
    std::array<double, 9> learnedRpmPerMps_{};
    double absEnvelope_{};
    double learnedAbsBrake_{0.82};
    double tractionCutSuppress_{};
    double gearStableTime_{};
    std::array<double, 4> shockBaseline_{};
    double verticalAccelBaseline_{};
    bool surfaceInitialized_{};
    int lastForwardGear_{};
    int lastTick_{-1};
    bool frameAdvanced_{};
#ifdef _WIN32
    int lastSessionInfoUpdate_{-1};
#endif
    double shiftRpm_{8500.0};
    double idleRpm_{900.0};
    double redlineRpm_{9000.0};
    double shiftLightFirstRpm_{6500.0};
    double shiftLightLastRpm_{8200.0};
    VehicleState lastState_{};
    std::chrono::steady_clock::time_point lastConnectAttempt_{};
    std::wstring statusText_{L"Waiting for iRacing"};
};

} // namespace pedalfeel
