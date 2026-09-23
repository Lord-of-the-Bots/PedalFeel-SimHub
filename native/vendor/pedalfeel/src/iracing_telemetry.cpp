#include "pedalfeel/iracing_telemetry.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <cstdlib>
#include <string_view>

namespace pedalfeel {
namespace {
#pragma pack(push, 4)
struct VarBuffer {
    int tickCount;
    int bufferOffset;
    int padding[2];
};
struct Header {
    int version, status, tickRate;
    int sessionInfoUpdate, sessionInfoLength, sessionInfoOffset;
    int variableCount, variableHeaderOffset, bufferCount, bufferLength;
    int padding[2];
    VarBuffer buffers[4];
};
struct VariableHeader {
    int type, offset, count;
    std::uint8_t countAsTime;
    char padding[3];
    char name[32];
    char description[64];
    char unit[32];
};
#pragma pack(pop)
static_assert(sizeof(Header) == 112);
static_assert(sizeof(VariableHeader) == 144);

double clamp(double value, double low = 0.0, double high = 1.0) {
    return std::clamp(value, low, high);
}
double smoothstep(double low, double high, double value) {
    const double x = clamp((value - low) / (high - low));
    return x * x * (3.0 - 2.0 * x);
}
double lowPass(double current, double target, double cutoffHz, double dt) {
    const double alpha = 1.0 - std::exp(-6.283185307179586 * cutoffHz * std::max(0.0, dt));
    return current + (target - current) * alpha;
}
} // namespace

IRacingTelemetry::~IRacingTelemetry() {
    close();
}

bool IRacingTelemetry::connected() const noexcept {
#ifdef _WIN32
    return memory_ != nullptr;
#else
    return false;
#endif
}

#ifdef _WIN32
bool IRacingTelemetry::mappedRange(int offset, std::size_t length) const noexcept {
    return memory_ && offset >= 0 && static_cast<std::size_t>(offset) <= mappingSize_ &&
           length <= mappingSize_ - static_cast<std::size_t>(offset);
}
#endif

bool IRacingTelemetry::tryConnect() {
#ifdef _WIN32
    const auto now = std::chrono::steady_clock::now();
    if (now - lastConnectAttempt_ < std::chrono::seconds(1))
        return false;
    lastConnectAttempt_ = now;
    mapping_ = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\IRSDKMemMapFileName");
    if (!mapping_) {
        statusText_ = L"Waiting for iRacing";
        return false;
    }
    memory_ = static_cast<const std::byte*>(MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0, 0));
    if (!memory_) {
        CloseHandle(mapping_);
        mapping_ = nullptr;
        statusText_ = L"Unable to map iRacing telemetry";
        return false;
    }
    MEMORY_BASIC_INFORMATION mappingInfo{};
    if (!VirtualQuery(memory_, &mappingInfo, sizeof(mappingInfo)) ||
        mappingInfo.RegionSize < sizeof(Header)) {
        close();
        statusText_ = L"Invalid iRacing telemetry mapping";
        return false;
    }
    const auto* regionBase = static_cast<const std::byte*>(mappingInfo.BaseAddress);
    const auto offsetInRegion = static_cast<std::size_t>(memory_ - regionBase);
    if (offsetInRegion >= mappingInfo.RegionSize) {
        close();
        statusText_ = L"Invalid iRacing telemetry mapping";
        return false;
    }
    mappingSize_ = mappingInfo.RegionSize - offsetInRegion;
    const auto* header = reinterpret_cast<const Header*>(memory_);
    const bool validVariables =
        header->variableCount > 0 && header->variableCount <= 4096 &&
        mappedRange(header->variableHeaderOffset,
                    static_cast<std::size_t>(header->variableCount) * sizeof(VariableHeader));
    if (header->version < 1 || !validVariables || header->bufferCount <= 0 ||
        header->bufferCount > 4 || header->bufferLength <= 0 ||
        header->bufferLength > 16 * 1024 * 1024) {
        close();
        statusText_ = L"Unsupported iRacing telemetry header";
        return false;
    }
    variables_.clear();
    const auto* headers =
        reinterpret_cast<const VariableHeader*>(memory_ + header->variableHeaderOffset);
    for (int i = 0; i < header->variableCount; ++i)
        variables_.emplace(std::string(headers[i].name, strnlen_s(headers[i].name, 32)),
                           Variable{headers[i].type, headers[i].offset, headers[i].count});
    frame_.resize(static_cast<std::size_t>(header->bufferLength));
    statusText_ = L"iRacing live · GT3 model";
    lastTick_ = -1;
    readSessionMetadata();
    return true;
#else
    statusText_ = L"iRacing live telemetry requires Windows";
    return false;
#endif
}

void IRacingTelemetry::close() {
#ifdef _WIN32
    if (memory_)
        UnmapViewOfFile(memory_);
    if (mapping_)
        CloseHandle(mapping_);
    memory_ = nullptr;
    mapping_ = nullptr;
    mappingSize_ = 0;
#endif
    variables_.clear();
    frame_.clear();
    lastTick_ = -1;
    slowSlip_ = {};
    previousSlip_ = {};
    wheelScale_ = {1.0, 1.0, 1.0, 1.0};
    learnedRpmPerMps_ = {};
    absEnvelope_ = 0.0;
    learnedAbsBrake_ = 0.82;
    tractionCutSuppress_ = 0.0;
    gearStableTime_ = 0.0;
    shockBaseline_ = {};
    verticalAccelBaseline_ = 0.0;
    surfaceInitialized_ = false;
    lastForwardGear_ = 0;
    shiftRpm_ = 8500.0;
    idleRpm_ = 900.0;
    redlineRpm_ = 9000.0;
    shiftLightFirstRpm_ = 6500.0;
    shiftLightLastRpm_ = 8200.0;
#ifdef _WIN32
    lastSessionInfoUpdate_ = -1;
#endif
}

bool IRacingTelemetry::copyLatestFrame() {
#ifdef _WIN32
    frameAdvanced_ = false;
    if (!connected())
        return false;
    const auto* header = reinterpret_cast<const Header*>(memory_);
    if (header->bufferCount <= 0 || header->bufferCount > 4 ||
        header->bufferLength != static_cast<int>(frame_.size())) {
        close();
        statusText_ = L"iRacing telemetry layout changed";
        return false;
    }
    if (header->sessionInfoUpdate != lastSessionInfoUpdate_)
        readSessionMetadata();
    if ((header->status & 1) == 0) {
        close();
        statusText_ = L"Waiting for driver to enter session";
        return false;
    }
    int newest = 0;
    for (int i = 1; i < header->bufferCount; ++i)
        if (header->buffers[i].tickCount > header->buffers[newest].tickCount)
            newest = i;
    const auto tick = header->buffers[newest].tickCount;
    if (tick == lastTick_)
        return true;
    if (!mappedRange(header->buffers[newest].bufferOffset, frame_.size())) {
        close();
        statusText_ = L"Invalid iRacing telemetry buffer";
        return false;
    }
    std::memcpy(frame_.data(), memory_ + header->buffers[newest].bufferOffset, frame_.size());
    if (header->buffers[newest].tickCount != tick)
        return false; // writer wrapped; retry next UI tick
    lastTick_ = tick;
    frameAdvanced_ = true;
    return true;
#else
    return false;
#endif
}

void IRacingTelemetry::readSessionMetadata() {
#ifdef _WIN32
    if (!connected())
        return;
    const auto* header = reinterpret_cast<const Header*>(memory_);
    if (header->sessionInfoLength <= 0 || header->sessionInfoLength > 16 * 1024 * 1024 ||
        !mappedRange(header->sessionInfoOffset,
                     static_cast<std::size_t>(header->sessionInfoLength)))
        return;
    const std::string_view session(
        reinterpret_cast<const char*>(memory_ + header->sessionInfoOffset),
        static_cast<std::size_t>(header->sessionInfoLength));
    const auto readRpm = [&](std::string_view key) -> double {
        const auto at = session.find(key);
        if (at == std::string_view::npos)
            return 0.0;
        const auto remaining = session.substr(
            at + key.size(), std::min<std::size_t>(32, session.size() - at - key.size()));
        const std::string value(remaining);
        return std::strtod(value.c_str(), nullptr);
    };
    const auto validRpm = [](double rpm, double minimum = 300.0) {
        return rpm >= minimum && rpm <= 20000.0;
    };
    const double idle = readRpm("DriverCarIdleRPM:");
    const double redline = readRpm("DriverCarRedLine:");
    const double first = readRpm("DriverCarSLFirstRPM:");
    const double last = readRpm("DriverCarSLLastRPM:");
    const double shift = readRpm("DriverCarSLShiftRPM:");
    const double blink = readRpm("DriverCarSLBlinkRPM:");
    if (validRpm(idle))
        idleRpm_ = idle;
    if (validRpm(shift, 2000.0))
        shiftRpm_ = shift;
    else if (validRpm(blink, 2000.0))
        shiftRpm_ = blink;
    if (validRpm(redline, shiftRpm_))
        redlineRpm_ = redline;
    else if (validRpm(blink, shiftRpm_))
        redlineRpm_ = blink;
    else
        redlineRpm_ = shiftRpm_;
    if (validRpm(first, idleRpm_))
        shiftLightFirstRpm_ = first;
    else
        shiftLightFirstRpm_ = idleRpm_ + (shiftRpm_ - idleRpm_) * .70;
    if (validRpm(last, shiftLightFirstRpm_))
        shiftLightLastRpm_ = last;
    else
        shiftLightLastRpm_ = std::max(shiftLightFirstRpm_, shiftRpm_);
    lastSessionInfoUpdate_ = header->sessionInfoUpdate;
#endif
}

double IRacingTelemetry::scalar(const char* name, double fallback) const noexcept {
    const auto found = variables_.find(name);
    if (found == variables_.end() || found->second.offset < 0)
        return fallback;
    constexpr std::array<std::size_t, 6> widths{1, 1, 4, 4, 4, 8};
    if (found->second.type < 0 || found->second.type >= static_cast<int>(widths.size()) ||
        static_cast<std::size_t>(found->second.offset) + widths[found->second.type] > frame_.size())
        return fallback;
    const auto* p = frame_.data() + found->second.offset;
    switch (found->second.type) {
    case 0: {
        char v{};
        std::memcpy(&v, p, 1);
        return v;
    }
    case 1: {
        std::uint8_t v{};
        std::memcpy(&v, p, 1);
        return v != 0;
    }
    case 2:
    case 3: {
        std::int32_t v{};
        std::memcpy(&v, p, 4);
        return v;
    }
    case 4: {
        float v{};
        std::memcpy(&v, p, 4);
        return v;
    }
    case 5: {
        double v{};
        std::memcpy(&v, p, 8);
        return v;
    }
    default:
        return fallback;
    }
}
bool IRacingTelemetry::boolean(const char* name) const noexcept {
    return scalar(name, 0.0) != 0.0;
}

bool IRacingTelemetry::hasDiagnosticVariable(std::string_view name) const {
    return variables_.contains(std::string(name));
}

int IRacingTelemetry::diagnosticCount(std::string_view name) const {
    const auto found = variables_.find(std::string(name));
    return found == variables_.end() ? 0 : std::max(0, found->second.count);
}

double IRacingTelemetry::diagnosticValue(std::string_view name, int index, double fallback) const {
    const auto found = variables_.find(std::string(name));
    if (found == variables_.end() || index < 0 || index >= found->second.count ||
        found->second.offset < 0)
        return fallback;
    constexpr std::array<std::size_t, 6> widths{1, 1, 4, 4, 4, 8};
    const auto& variable = found->second;
    if (variable.type < 0 || variable.type >= static_cast<int>(widths.size()))
        return fallback;
    const auto width = widths[variable.type];
    const auto offset =
        static_cast<std::size_t>(variable.offset) + static_cast<std::size_t>(index) * width;
    if (offset + width > frame_.size())
        return fallback;
    const auto* p = frame_.data() + offset;
    switch (variable.type) {
    case 0: {
        char v{};
        std::memcpy(&v, p, 1);
        return v;
    }
    case 1: {
        std::uint8_t v{};
        std::memcpy(&v, p, 1);
        return v != 0;
    }
    case 2:
    case 3: {
        std::int32_t v{};
        std::memcpy(&v, p, 4);
        return v;
    }
    case 4: {
        float v{};
        std::memcpy(&v, p, 4);
        return v;
    }
    case 5: {
        double v{};
        std::memcpy(&v, p, 8);
        return v;
    }
    default:
        return fallback;
    }
}

void IRacingTelemetry::estimate(VehicleState& state, double dt) {
    constexpr std::array<const char*, 4> speeds{"LFspeed", "RFspeed", "LRspeed", "RRspeed"};
    const double ground = std::max(1.0, state.speedKph / 3.6);
    const double speedConfidence = clamp((ground - 5.0) / 12.0);
    const double lateralDemand = clamp(std::abs(state.lateralG) / 1.75);
    const bool absActive = boolean("BrakeABSactive");
    if (absActive && state.brake > .25)
        learnedAbsBrake_ = lowPass(learnedAbsBrake_, state.brake, 0.35, dt);
    const double trailAllowance = 1.0 - lateralDemand * .24;
    const double learnedBrakeDemand =
        clamp(state.brake / std::max(.45, learnedAbsBrake_ * trailAllowance), 0.0, 1.2);
    for (std::size_t i = 0; i < 4; ++i) {
        auto& tire = state.tires[i];
        const double wheel = scalar(speeds[i], ground);
        tire.wheelSpeed = wheel;
        const bool hasWheel = variables_.contains(speeds[i]);
        if (hasWheel && speedConfidence > .55 && state.brake < .03 && state.throttleRaw < .15) {
            const double targetScale = clamp(ground / std::max(1.0, std::abs(wheel)), .90, 1.10);
            wheelScale_[i] = lowPass(wheelScale_[i], targetScale, .35, dt);
        }
        const double correctedWheel = wheel * wheelScale_[i];
        tire.wheelSpeed = correctedWheel;
        const double rawSlip = clamp((ground - correctedWheel) / ground, -0.35, 0.55);
        slowSlip_[i] = lowPass(slowSlip_[i], rawSlip, 8.0, dt);
        const double instability = std::abs(rawSlip - previousSlip_[i]);
        previousSlip_[i] = rawSlip;
        tire.slipRatio = std::max(0.0, slowSlip_[i]);
        tire.spinRatio = std::max(0.0, -slowSlip_[i]);
        tire.confidence = (hasWheel ? .92 : .38) * speedConfidence;
        tire.longitudinalUtilization = clamp(tire.slipRatio / .115);
        tire.lateralUtilization = lateralDemand * (i < 2 ? 1.0 : .72);
        const double measured =
            std::hypot(tire.longitudinalUtilization, tire.lateralUtilization * .72) +
            instability * 1.8;
        const double brakeEstimate =
            i < 2 ? learnedBrakeDemand *
                        (i % 2 ? (1.0 + state.lateralG * .025) : (1.0 - state.lateralG * .025))
                  : 0.0;
        tire.combinedUtilization = clamp(std::max(measured, brakeEstimate), 0.0, 1.25);
        tire.locking = state.brake > .08 && tire.slipRatio > .16 && speedConfidence > .25;
        tire.spinning = state.throttleRaw > .1 && tire.spinRatio > .01 && i >= 2;
        tire.verticalLoad = clamp(.5 + (i % 2 ? state.lateralG : -state.lateralG) * .10 +
                                  (i < 2 ? -state.longitudinalG : .35 * state.longitudinalG) * .12);
    }
    const double absTarget = absActive ? 1.0 : 0.0;
    absEnvelope_ = lowPass(absEnvelope_, absTarget, absTarget > absEnvelope_ ? 18.0 : 7.0, dt);
    const double frontSlip = std::max(state.tires[0].slipRatio, state.tires[1].slipRatio);
    state.absSeverity = clamp(absEnvelope_ * (.58 + frontSlip * 2.2));
    if (state.gear <= 0) {
        tractionCutSuppress_ = std::max(tractionCutSuppress_, .35);
        gearStableTime_ = 0.0;
    } else if (lastForwardGear_ > 0 && state.gear != lastForwardGear_) {
        tractionCutSuppress_ = .35;
        gearStableTime_ = 0.0;
    } else
        gearStableTime_ += dt;
    if (state.gear > 0)
        lastForwardGear_ = state.gear;
    tractionCutSuppress_ = std::max(0.0, tractionCutSuppress_ - dt);
    const double drivenSpin = std::max(state.tires[2].spinRatio, state.tires[3].spinRatio);
    // A sharp suspension impact can briefly unload and accelerate a driven wheel. Do not present
    // that wheel-speed spike as traction loss when the independent shock/vertical-accel detector
    // identifies the same frame as a bump.
    const double roadDisturbance =
        std::max(state.surfaceImpactSeverity, state.rumbleStripSeverity * .65);
    const double bumpRejection = 1.0 - smoothstep(.08, .35, roadDisturbance);
    const double wheelspinSeverity = clamp((drivenSpin - .004) / .032) *
                                     clamp(state.throttleRaw * 1.25) * speedConfidence *
                                     bumpRejection;
    const double throttleCut = tractionCutSuppress_ > 0.0 || state.revLimiterActive
                                   ? 0.0
                                   : std::max(0.0, state.throttleRaw - state.throttle);
    const double cutSeverity = clamp((throttleCut - .025) / .22) * clamp(state.throttleRaw * 1.2);
    double drivelineOverspeed = 0.0, drivelineSeverity = 0.0;
    const bool validGear =
        state.gear > 0 && state.gear < static_cast<int>(learnedRpmPerMps_.size());
    const bool clutchCoupled = !state.clutchAvailable || state.clutch > .92;
    const bool useDrivelineFallback = !state.rearWheelTelemetryAvailable;
    if (useDrivelineFallback && clutchCoupled && validGear && ground > 10.0 &&
        state.engineRpm > 1800.0 && gearStableTime_ > .35 && state.throttleRaw > .35 &&
        state.brake < .03) {
        const double observed = state.engineRpm / ground;
        auto& learned = learnedRpmPerMps_[static_cast<std::size_t>(state.gear)];
        if (learned <= 0.0)
            learned = observed;
        else if (observed > learned * .94 && observed < learned * 1.06)
            learned = lowPass(learned, observed, .18, dt);
    }
    if (useDrivelineFallback && validGear) {
        const double learned = learnedRpmPerMps_[static_cast<std::size_t>(state.gear)];
        state.drivelineSlipAvailable = learned > 0.0;
        if (learned > 0.0)
            drivelineOverspeed = std::max(0.0, state.engineRpm / (ground * learned) - 1.0);
        const bool coupled = clutchCoupled && gearStableTime_ > .35 &&
                             tractionCutSuppress_ <= 0.0 && state.brake < .05 &&
                             state.throttleRaw > .22;
        // Below ~43 km/h, require meaningful cornering load so launch/clutch
        // slip is not presented as tire slip. Never fire at walking speed.
        const bool lowSpeedContext = ground > 3.5 && (ground >= 12.0 || lateralDemand > .14);
        if (coupled && lowSpeedContext) {
            drivelineSeverity = clamp((drivelineOverspeed - .025) / .14) *
                                 clamp((state.throttleRaw - .18) / .55) * bumpRejection;
        }
    }
    state.drivelineOverspeedRatio = drivelineOverspeed;
    state.rearWheelspinRatio =
        state.rearWheelTelemetryAvailable ? drivenSpin : drivelineOverspeed;
    state.throttleCutSeverity = cutSeverity;
    state.tractionConfidence =
        state.rearWheelTelemetryAvailable
            ? .92
            : (state.drivelineSlipAvailable ? .72 : (state.throttleRawAvailable ? .30 : 0.0));
    if (state.rearWheelTelemetryAvailable) {
        // A processed-throttle cut is not uniquely TC: shifts and engine strategy can also cause
        // it. With real rear wheel speeds present, use the cut only to reinforce measured spin.
        state.rearSlipSeverity = clamp(wheelspinSeverity * (1.0 + cutSeverity * .25));
    } else {
        // Without rear wheel speeds, driveline overspeed is the useful fallback. A throttle cut
        // remains a deliberately lower-confidence cue when it cannot be corroborated.
        state.rearSlipSeverity = std::max(drivelineSeverity, cutSeverity * .55);
    }
}

void IRacingTelemetry::estimateSurface(VehicleState& state, double dt) {
    constexpr std::array<const char*, 4> rumbleNames{
        "TireLF_RumblePitch", "TireRF_RumblePitch", "TireLR_RumblePitch", "TireRR_RumblePitch"};
    state.rumbleStripTelemetryAvailable =
        std::all_of(rumbleNames.begin(), rumbleNames.end(),
                    [&](const char* name) { return variables_.contains(name); });
    if (state.rumbleStripTelemetryAvailable) {
        std::array<double, 4> pitch{};
        int activeTires = 0;
        double pitchTotal = 0.0;
        for (std::size_t i = 0; i < pitch.size(); ++i) {
            pitch[i] = std::max(0.0, scalar(rumbleNames[i]));
            if (pitch[i] > 0.0) {
                ++activeTires;
                pitchTotal += pitch[i];
            }
        }
        if (activeTires > 0) {
            state.rumbleStripFrequencyHz = pitchTotal / activeTires;
            const double frequencyAuthority =
                smoothstep(12.0, 95.0, state.rumbleStripFrequencyHz);
            state.rumbleStripSeverity =
                clamp(.38 + frequencyAuthority * .34 + (activeTires - 1) * .09);
            const double left = std::max(pitch[0], pitch[2]);
            const double right = std::max(pitch[1], pitch[3]);
            if (std::max(left, right) > 0.0)
                state.rumbleStripDirection = right > left ? 1.0 : (left > right ? -1.0 : 0.0);
        }
    }

    constexpr std::array<const char*, 4> shockNames{"LFshockVel", "RFshockVel", "LRshockVel",
                                                    "RRshockVel"};
    constexpr std::array<const char*, 4> shockTimeNames{
        "LFshockVel_ST", "RFshockVel_ST", "LRshockVel_ST", "RRshockVel_ST"};
    state.surfaceTelemetryAvailable =
        std::all_of(shockNames.begin(), shockNames.end(),
                    [&](const char* name) { return variables_.contains(name); });
    if (!state.surfaceTelemetryAvailable)
        return;

    const bool highRateShock =
        std::all_of(shockTimeNames.begin(), shockTimeNames.end(), [&](const char* name) {
            const auto found = variables_.find(name);
            return found != variables_.end() && found->second.count >= 6;
        });
    const auto verticalTime = variables_.find("VertAccel_ST");
    const bool highRateVertical =
        verticalTime != variables_.end() && verticalTime->second.count >= 6;

    std::array<double, 4> shock{};
    for (std::size_t i = 0; i < shock.size(); ++i)
        shock[i] = highRateShock ? diagnosticValue(shockTimeNames[i], 5) : scalar(shockNames[i]);
    const double verticalG =
        (highRateVertical ? diagnosticValue("VertAccel_ST", 5) : scalar("VertAccel")) / 9.80665;
    if (!surfaceInitialized_) {
        shockBaseline_ = shock;
        verticalAccelBaseline_ = verticalG;
        surfaceInitialized_ = true;
        return;
    }

    std::array<double, 4> movement{};
    for (std::size_t i = 0; i < shock.size(); ++i) {
        if (highRateShock) {
            for (int sample = 0; sample < 6; ++sample) {
                movement[i] = std::max(
                    movement[i],
                    std::abs(diagnosticValue(shockTimeNames[i], sample) - shockBaseline_[i]));
            }
        } else {
            movement[i] = std::abs(shock[i] - shockBaseline_[i]);
        }
        shockBaseline_[i] = lowPass(shockBaseline_[i], shock[i], 1.5, dt);
    }
    double verticalImpulse = std::abs(verticalG - verticalAccelBaseline_);
    if (highRateVertical) {
        for (int sample = 0; sample < 6; ++sample) {
            const double sampleG = diagnosticValue("VertAccel_ST", sample) / 9.80665;
            verticalImpulse =
                std::max(verticalImpulse, std::abs(sampleG - verticalAccelBaseline_));
        }
    }
    verticalAccelBaseline_ = lowPass(verticalAccelBaseline_, verticalG, 1.5, dt);
    const double leftMovement = std::max(movement[0], movement[2]);
    const double rightMovement = std::max(movement[1], movement[3]);
    const double strongest = std::max(leftMovement, rightMovement);
    const double shockSeverity = clamp((strongest - .045) / .235);
    const double verticalSeverity = clamp((verticalImpulse - .06) / .39);
    const double speedGate = clamp((state.speedKph - 8.0) / 18.0);

    // Suspension motion is the primary signal. Chassis acceleration confirms that
    // it was an impact rather than smooth pitch/roll, while retaining some response
    // for a bump touched by only one wheel.
    state.surfaceImpactSeverity = shockSeverity * (.35 + .65 * verticalSeverity) * speedGate;
    const double imbalance =
        strongest > .001 ? std::abs(rightMovement - leftMovement) / strongest : 0.0;
    state.surfaceImpactConfidence = clamp((imbalance - .18) / .52);
    if (state.surfaceImpactConfidence > .0)
        state.surfaceImpactDirection = rightMovement > leftMovement ? 1.0 : -1.0;
}

VehicleState IRacingTelemetry::sample(double dtSeconds) {
    VehicleState state;
    (void)sampleLatest(dtSeconds, state);
    return lastState_;
}

bool IRacingTelemetry::sampleLatest(double dtSeconds, VehicleState& state) {
    if (!connected() && !tryConnect())
        return false;
    if (!copyLatestFrame())
        return false;
    if (!frameAdvanced_)
        return false;
    state.time = scalar("SessionTime", lastState_.time + dtSeconds);
    state.speedKph = std::max(0.0, scalar("Speed") * 3.6);
    state.brake = clamp(scalar("Brake"));
    state.brakeRaw = clamp(scalar("BrakeRaw", state.brake));
    state.throttle = clamp(scalar("Throttle"));
    state.throttleRaw = clamp(scalar("ThrottleRaw", state.throttle));
    state.clutch = clamp(scalar("Clutch", 1.0));
    state.steering = scalar("SteeringWheelAngle");
    state.rearWheelTelemetryAvailable =
        variables_.contains("LRspeed") && variables_.contains("RRspeed");
    state.throttleRawAvailable = variables_.contains("ThrottleRaw");
    state.brakeRawAvailable = variables_.contains("BrakeRaw");
    state.clutchAvailable = variables_.contains("Clutch");
    state.longitudinalG = scalar("LongAccel") / 9.80665;
    state.lateralG = scalar("LatAccel") / 9.80665;
    state.gear = static_cast<int>(scalar("Gear", 1));
    state.engineRpm = std::max(0.0, scalar("RPM"));
    state.idleRpm = idleRpm_;
    state.redlineRpm = redlineRpm_;
    state.shiftLightFirstRpm = shiftLightFirstRpm_;
    state.shiftLightLastRpm = shiftLightLastRpm_;
    state.shiftRpm = shiftRpm_;
    state.shiftPowerAvailable = variables_.contains("ShiftPowerPct");
    state.shiftPower = clamp(scalar("ShiftPowerPct"));
    state.trackSurface = static_cast<int>(scalar("PlayerTrackSurface"));
    state.trackSurfaceMaterial = static_cast<int>(scalar("PlayerTrackSurfaceMaterial"));
    state.engineWarningsAvailable = variables_.contains("EngineWarnings");
    constexpr std::uint32_t RevLimiterActiveMask = 0x0020;
    const auto engineWarnings = static_cast<std::uint32_t>(scalar("EngineWarnings"));
    state.revLimiterActive =
        state.engineWarningsAvailable && (engineWarnings & RevLimiterActiveMask) != 0;
    const bool onTrack = !variables_.contains("IsOnTrack") || boolean("IsOnTrack");
    const bool onTrackCar =
        !variables_.contains("IsOnTrackCar") || boolean("IsOnTrackCar");
    const bool inGarage = variables_.contains("IsInGarage") && boolean("IsInGarage");
    const bool replayPlaying =
        variables_.contains("IsReplayPlaying") && boolean("IsReplayPlaying");
    state.drivingActive = onTrack && onTrackCar && !inGarage && !replayPlaying;
    if (!state.drivingActive) {
        slowSlip_ = {};
        previousSlip_ = {};
        absEnvelope_ = 0.0;
        tractionCutSuppress_ = 0.0;
        gearStableTime_ = 0.0;
        surfaceInitialized_ = false;
        lastForwardGear_ = 0;
        lastState_ = state;
        return true;
    }
    estimateSurface(state, std::clamp(dtSeconds, .001, .05));
    estimate(state, std::clamp(dtSeconds, .001, .05));
    lastState_ = state;
    return true;
}
} // namespace pedalfeel
