#include "pedalfeel/renderer.hpp"
#include <algorithm>
#include <cmath>

namespace pedalfeel {
namespace {
double clamp(double value, double low = 0.0, double high = 1.0) noexcept {
    return std::clamp(value, low, high);
}

double smoothstep(double low, double high, double value) noexcept {
    const auto x = clamp((value - low) / (high - low));
    return x * x * (3.0 - 2.0 * x);
}
double follow(double current, double target, double attackHz, double releaseHz,
              double dt) noexcept {
    const double hz = target > current ? attackHz : releaseHz;
    return current + (target - current) * (1.0 - std::exp(-6.283185307179586 * hz * dt));
}
double frameTime(double now, double& previous) noexcept {
    double dt = (previous > 0.0 && now > previous) ? now - previous : 1.0 / 60.0;
    previous = now;
    return std::clamp(dt, 1.0 / 360.0, .05);
}
} // namespace

SurfaceHaptics SurfaceRenderer::render(const VehicleState& state, double strength) noexcept {
    const double dt = frameTime(state.time, previousTime_);
    if (!state.drivingActive) {
        previousSeverity_ = 0.0;
        cooldown_ = 0.0;
        eventAge_ = 1.0;
        rumbleEnvelope_ = 0.0;
        return {};
    }
    cooldown_ = std::max(0.0, cooldown_ - dt);
    eventAge_ += dt;
    const double severity = clamp(state.surfaceImpactSeverity);
    const bool onset =
        severity > .22 && (previousSeverity_ <= .22 || severity > previousSeverity_ + .12);
    if (state.surfaceTelemetryAvailable && onset && cooldown_ <= 0.0) {
        eventSeverity_ = severity;
        eventAge_ = 0.0;
        eventDuration_ = .065 + severity * .055;
        cooldown_ = .055;
    }
    previousSeverity_ = severity;

    const double rumbleTarget = state.rumbleStripTelemetryAvailable
                                    ? clamp(state.rumbleStripSeverity)
                                    : 0.0;
    rumbleEnvelope_ = follow(rumbleEnvelope_, rumbleTarget, 22.0, 14.0, dt);

    SurfaceHaptics result;
    if (strength <= .001)
        return result;
    double amplitude = 0.0;
    double frequency = 0.0;
    HapticMode mode = HapticMode::Quiet;
    if (eventAge_ < eventDuration_) {
        const double envelope = 1.0 - eventAge_ / eventDuration_;
        amplitude = (.12 + eventSeverity_ * .20) * clamp(strength) * envelope;
        frequency = 14.0 + eventSeverity_ * 6.0;
        mode = HapticMode::Surface;
    }
    if (rumbleEnvelope_ > .01) {
        const double rumbleAmplitude = (.10 + rumbleEnvelope_ * .18) * clamp(strength);
        if (rumbleAmplitude >= amplitude) {
            amplitude = rumbleAmplitude;
            // Preserve road-speed cadence without sending pitches beyond the useful pedal band.
            frequency = 18.0 + clamp((state.rumbleStripFrequencyHz - 12.0) / 95.0) * 24.0;
            mode = HapticMode::RumbleStrip;
        }
    }
    if (amplitude <= .001)
        return result;
    // Pedals are not left/right transducers, so every chassis impact reaches both at full strength.
    // The stiffer brake and the driver's loaded foot need a stronger command for comparable feel.
    constexpr double BrakeTransmissionCompensation = 1.60;
    result.brake.output = clamp(amplitude * BrakeTransmissionCompensation);
    result.throttle.output = clamp(amplitude);
    result.brake.frequencyHz = result.throttle.frequencyHz = frequency;
    result.brake.mode = result.throttle.mode = mode;
    return result;
}

HapticFrame mixSurfaceCue(const HapticFrame& base, const HapticFrame& surface) noexcept {
    if (surface.output <= .001)
        return base;
    switch (base.mode) {
    case HapticMode::Abs:
    case HapticMode::Locking:
    case HapticMode::Traction:
    case HapticMode::Limiter:
        return base;
    default:
        break;
    }
    HapticFrame mixed = base;
    if (base.mode == HapticMode::Threshold) {
        // Preserve the grip cue's identity and pitch, but allow a small impact pulse through it.
        mixed.output = clamp(base.output + surface.output * .70);
        return mixed;
    }
    mixed.output = clamp(std::max(base.output, base.output * .45 + surface.output));
    mixed.frequencyHz = surface.frequencyHz;
    mixed.mode = surface.mode;
    return mixed;
}

HapticFrame mixBrakeChassisCue(const HapticFrame& brake, const HapticFrame& throttle) noexcept {
    switch (brake.mode) {
    case HapticMode::Abs:
    case HapticMode::Locking:
    case HapticMode::Threshold:
    case HapticMode::Shift:
        return brake;
    default:
        break;
    }

    double transfer = 0.0;
    switch (throttle.mode) {
    case HapticMode::Idle:
    case HapticMode::Loading:
        transfer = .12;
        break;
    case HapticMode::Shift:
        transfer = .16;
        break;
    case HapticMode::Limiter:
        transfer = .18;
        break;
    default:
        return brake;
    }
    const double chassisOutput = throttle.output * transfer;
    if (chassisOutput <= .001)
        return brake;

    HapticFrame mixed = brake;
    mixed.output = clamp(brake.output + chassisOutput);
    mixed.frequencyHz = throttle.frequencyHz;
    if (brake.mode == HapticMode::Quiet)
        mixed.mode = throttle.mode;
    return mixed;
}

HapticFrame BrakeRenderer::render(const VehicleState& state,
                                  const RenderSettings& settings, bool includeLoading) noexcept {
    if (!state.drivingActive) {
        thresholdEnvelope_ = lockEnvelope_ = absEnvelope_ = lockTransient_ = 0.0;
        downshiftTransient_ = brakeContextRemaining_ = 0.0;
        lastForwardGear_ = 0;
        previousLockMask_ = 0;
        return {};
    }
    const auto& lf = state.tires[static_cast<std::size_t>(Wheel::LeftFront)];
    const auto& rf = state.tires[static_cast<std::size_t>(Wheel::RightFront)];
    const double confidence = clamp((lf.confidence + rf.confidence) * 0.5);
    const double frontGrip = (lf.combinedUtilization + rf.combinedUtilization) * 0.5;
    const double lockSeverity = std::max(lf.slipRatio, rf.slipRatio);
    const double brakeGate = smoothstep(.04, .14, state.brake);

    const double dt = frameTime(state.time, previousTime_);
    if (std::max(state.brake, state.brakeRaw) > .02)
        brakeContextRemaining_ = .40;
    else
        brakeContextRemaining_ = std::max(0.0, brakeContextRemaining_ - dt);
    const bool brakingContext = brakeContextRemaining_ > 0.0 || state.longitudinalG < -.12;
    if (state.gear > 0 && lastForwardGear_ > 0 && state.gear < lastForwardGear_ &&
        brakingContext)
        downshiftTransient_ = 1.0;
    if (state.gear > 0)
        lastForwardGear_ = state.gear;
    downshiftTransient_ = std::max(0.0, downshiftTransient_ - dt / .10);
    HapticFrame result;
    // A restrained pressure foundation gives the driver a continuous path toward the threshold
    // cue without allowing cornering alone to vibrate the pedal.
    // Isolated effect previews can omit the pressure foundation. Live rendering
    // keeps the default true and therefore retains its original mixed signal.
    result.loading = includeLoading ? clamp(state.brake) * .14 : 0.0;
    const double thresholdTarget =
        smoothstep(settings.gripThreshold * 0.72, settings.gripThreshold * 1.05, frontGrip) * 0.66 *
        confidence * brakeGate;
    const double lockTarget = smoothstep(0.13, 0.28, lockSeverity) * 0.82 * confidence * brakeGate;
    thresholdEnvelope_ = follow(thresholdEnvelope_, thresholdTarget, 14.0, 8.0, dt);
    lockEnvelope_ = follow(lockEnvelope_, lockTarget, 25.0, 10.0, dt);
    absEnvelope_ = follow(absEnvelope_, clamp(state.absSeverity), 28.0, 9.0, dt);
    const std::uint8_t lockMask =
        static_cast<std::uint8_t>((lf.locking ? 1 : 0) | (rf.locking ? 2 : 0));
    if (lockMask != 0 && (lockMask & ~previousLockMask_) != 0)
        lockTransient_ = 1.0;
    lockTransient_ = std::max(0.0, lockTransient_ - dt / 0.045);
    previousLockMask_ = lockMask;
    result.threshold = thresholdEnvelope_;
    result.lock = clamp(lockEnvelope_ + lockTransient_ * .38);
    result.abs = absEnvelope_ * clamp(settings.absPunch);

    // A downshift is a single low thump. Tire lock and ABS feedback take priority.
    const double absPriority = settings.absPunch > 0.0 ? state.absSeverity : 0.0;
    const double tirePriority = clamp(std::max({absPriority, lockEnvelope_, lockTransient_}));
    const double downshift = downshiftTransient_ * .34 * clamp(settings.downshiftKick) * (1.0 - tirePriority);

    // ABS owns the pedal while active; loading and threshold texture are ducked.
    const double duck = 1.0 - clamp(absPriority) * 0.62;
    const double mixed =
        (result.loading * duck + result.threshold * clamp(settings.texture) * duck + result.lock +
         result.abs + downshift) *
        clamp(settings.strength);
    result.output = clamp(std::tanh(mixed * 1.35) / std::tanh(1.35));
    result.frequencyHz = 14.0 + (settings.texture > 0.0 ? result.threshold : 0.0) * 20.0;
    // A single centered pedal cannot communicate left-versus-right spatially. Use one stable,
    // severity-linked lock signature instead of asking the driver to memorize wheel-specific tones.
    if (lockMask != 0)
        result.frequencyHz = 25.0 + result.lock * 5.0;
    if (result.abs > .12)
        result.frequencyHz = 19.0 + result.abs * 8.0 + std::sin(state.time * 41.0) * 1.2;
    else if (settings.downshiftKick > 0.0 && downshiftTransient_ > .05 && tirePriority < .25)
        result.frequencyHz = 15.0 + downshiftTransient_ * 5.0;

    if (settings.absPunch > 0.0 && state.absSeverity > 0.3)
        result.mode = HapticMode::Abs;
    else if (result.lock > 0.25)
        result.mode = HapticMode::Locking;
    else if (settings.downshiftKick > 0.0 && downshiftTransient_ > .05 && tirePriority < .25)
        result.mode = HapticMode::Shift;
    else if (settings.texture > 0.0 && result.threshold > 0.35)
        result.mode = HapticMode::Threshold;
    else if (result.loading > .02)
        result.mode = HapticMode::Loading;
    if (result.output <= 0.0) {
        result.frequencyHz = 0.0;
        result.mode = HapticMode::Quiet;
    }
    return result;
}

HapticFrame ThrottleRenderer::render(const VehicleState& state, double tractionStrength,
                                     double engineTexture, double shiftKick,
                                     double idleTexture, double limiterStrength) noexcept {
    if (!state.drivingActive) {
        slipEnvelope_ = tractionEnvelope_ = characterEnvelope_ = limiterEnvelope_ = 0.0;
        shiftTransient_ = 0.0;
        lastForwardGear_ = 0;
        previousThrottleRaw_ = previousPedalDemand_ = previousEngineRpm_ = 0.0;
        return {};
    }
    const double dt = frameTime(state.time, previousTime_);
    const auto& lr = state.tires[static_cast<std::size_t>(Wheel::LeftRear)];
    const auto& rr = state.tires[static_cast<std::size_t>(Wheel::RightRear)];
    const double rearSpin = std::max({lr.spinRatio, rr.spinRatio, state.rearSlipSeverity * .06});
    const double slipTarget = smoothstep(.035, .18, rearSpin) * clamp(state.throttle);
    slipEnvelope_ = follow(slipEnvelope_, slipTarget, 16.0, 7.0, dt);
    tractionEnvelope_ = follow(tractionEnvelope_, state.rearSlipSeverity, 25.0, 10.0, dt);
    const double driverDemand = std::max({state.throttleRaw, state.throttle, previousThrottleRaw_});
    if (state.gear > 0 && lastForwardGear_ > 0 && state.gear > lastForwardGear_ &&
        driverDemand > .35)
        shiftTransient_ = 1.0;
    if (state.gear > 0)
        lastForwardGear_ = state.gear;
    previousThrottleRaw_ = state.throttleRaw;
    shiftTransient_ = std::max(0.0, shiftTransient_ - dt / .075);
    const double idleRpm = std::max(300.0, state.idleRpm);
    const double rangeTop = std::max({idleRpm + 2500.0, state.redlineRpm, state.shiftRpm});
    const double rpmNormalized = clamp((state.engineRpm - idleRpm) / (rangeTop - idleRpm));
    const double pedalDemand = clamp(std::max(state.throttleRaw, state.throttle));
    const double limiterTarget = state.engineWarningsAvailable && state.revLimiterActive &&
                                         state.gear > 0 && pedalDemand >= .70
                                     ? 1.0
                                     : 0.0;
    // SimHub extension: the independent limiter control also releases engine ducking
    // and frequency ownership. Disabling it must not leave a fading hidden limiter.
    limiterEnvelope_ = limiterStrength > 0.0
                           ? follow(limiterEnvelope_, limiterTarget, 30.0, 18.0, dt)
                           : 0.0;
    const double limiterCue = limiterEnvelope_ * clamp(limiterStrength);
    // A linear low-pedal blend starts engine character near 1–2% travel instead of holding it
    // below perception through the first several percent, while still taking 25% travel to reach
    // the full engine layer.
    const double textureBlend = clamp(pedalDemand / .25);
    // Keep low-rev character restrained, then add a broad high-rev lift. The former curve only
    // changed by five output points across the rev range, which encouraged using the actuator's
    // calibration minimum as an engine-strength control. That makes every small signal, including
    // idle, jump to the same physical floor. Strength belongs here instead: it should grow with RPM
    // while calibration remains a barely-perceptible hardware threshold.
    const double firstShiftLight =
        clamp((state.shiftLightFirstRpm - idleRpm) / (rangeTop - idleRpm), .25, .85);
    const double highRpmCharacter = smoothstep(firstShiftLight * .65, .92, rpmNormalized);
    const double engineCharacter =
        .15 + .09 * std::sqrt(pedalDemand) + .16 * highRpmCharacter;
    const double engineLayer =
        engineTexture > .001
            ? clamp(engineCharacter * clamp(engineTexture), 0.0, .38) * textureBlend
            : 0.0;
    // GT3 engines commonly idle well above road-car RPM. Keep the layer at full strength
    // through that range, then fade it out so lift-off at driving RPM stays quiet.
    const double idleProximity =
        1.0 - smoothstep(idleRpm + 500.0, idleRpm + 1800.0, state.engineRpm);
    const double idleLayer =
        state.engineRpm > 400.0 && idleTexture > .001
            ? IdleTextureMaximumOutput * idleProximity * clamp(idleTexture) * (1.0 - textureBlend)
            : 0.0;
    const double pedalRate =
        std::max(0.0, (pedalDemand - previousPedalDemand_) / std::max(dt, .001));
    const double rpmRate =
        previousEngineRpm_ > 0.0
            ? std::max(0.0, (state.engineRpm - previousEngineRpm_) / std::max(dt, .001))
            : 0.0;
    previousPedalDemand_ = pedalDemand;
    previousEngineRpm_ = state.engineRpm;
    const double fastTipIn = smoothstep(1.5, 6.0, pedalRate);
    const double characterAttackHz = 3.0 - fastTipIn * 1.6;
    const double rpmActivity = smoothstep(200.0, 1800.0, rpmRate);
    // A steady engine still gains authority as it approaches the shift point; RPM motion adds the
    // remaining texture without making a high-gear pull disappear into a subdued background.
    const double steadyRpmGain = .62 + .20 * highRpmCharacter;
    const double engineMotionGain = steadyRpmGain + rpmActivity * (1.0 - steadyRpmGain);
    // Slow pedal application tracks directly. A fast stab is eased in, while a loaded engine
    // whose RPM is barely moving retains only a subdued background instead of a fixed loud buzz.
    const double characterTarget = idleLayer + engineLayer * engineMotionGain;
    characterEnvelope_ = follow(characterEnvelope_, characterTarget, characterAttackHz, 3.0, dt);
    HapticFrame result;
    // Disabled effects must not choose another effect's frequency or attenuate it.
    const double tractionCue = tractionStrength > 0.0
                                   ? std::max(tractionEnvelope_, slipEnvelope_ * .85)
                                   : 0.0;
    result.loading =
        characterEnvelope_ * (1.0 - tractionCue * .88) * (1.0 - limiterCue * .65);
    result.threshold = slipEnvelope_ * .48;
    result.abs = tractionEnvelope_ * .64;
    const double shiftLayer =
        shiftTransient_ * .20 * clamp(shiftKick) * (1.0 - clamp(tractionCue * 1.5));
    result.lock = shiftLayer;
    const double tractionPulse = .78 + .22 * (.5 + .5 * std::sin(state.time * 75.0));
    const double tractionLayer =
        std::max(result.threshold, result.abs) * clamp(tractionStrength) * tractionPulse;
    // The direct iRacing limiter flag drives a deliberately rough intermittent pulse. It is
    // distinct from the smooth high-RPM texture and remains subordinate to traction information.
    const double limiterPulse = std::sin(state.time * 100.53096491487338) >= 0.0 ? 1.0 : .32;
    const double limiterLayer = limiterCue * .24 * limiterPulse * (1.0 - tractionCue);
    result.output = clamp(result.loading + tractionLayer + result.lock + limiterLayer);
    // Keep idle as a distinct low throb, while active engine character lives in a smoother
    // 24–50 Hz tactile band. This is a perceptual RPM encoding rather than literal firing rate.
    const double engineFrequency = 24.0 + rpmNormalized * 26.0;
    result.frequencyHz = engineFrequency;
    if (idleLayer > 0.0) {
        const double idleFrequency =
            IdleTextureFrequencyHz + 2.0 + std::sin(state.time * 11.0) * 2.0;
        result.frequencyHz = idleFrequency * (1.0 - textureBlend) + engineFrequency * textureBlend;
    }
    if (shiftKick > 0.0 && shiftTransient_ > .05 && tractionCue <= .08)
        result.frequencyHz = 16.0 + shiftTransient_ * 7.0;
    if (tractionCue > .08) {
        result.frequencyHz = 24.0 + tractionCue * 8.0;
        result.mode = HapticMode::Traction;
    } else if (limiterEnvelope_ > .12) {
        result.frequencyHz = 38.0 + limiterPulse * 6.0;
        result.mode = HapticMode::Limiter;
    } else if (shiftKick > 0.0 && shiftTransient_ > .05)
        result.mode = HapticMode::Shift;
    else if (state.throttle > .15 || (result.loading > .001 && idleLayer <= 0.0))
        result.mode = HapticMode::Loading;
    else if (idleLayer > 0.0)
        result.mode = HapticMode::Idle;
    if (result.output <= 0.0) {
        result.frequencyHz = 0.0;
        result.mode = HapticMode::Quiet;
    }
    return result;
}

const wchar_t* modeName(HapticMode mode) noexcept {
    switch (mode) {
    case HapticMode::Idle:
        return L"IDLE";
    case HapticMode::Loading:
        return L"LOADING";
    case HapticMode::Threshold:
        return L"AT THRESHOLD";
    case HapticMode::Locking:
        return L"LOCKING";
    case HapticMode::Abs:
        return L"ABS INTERVENTION";
    case HapticMode::Traction:
        return L"REAR TRACTION LOSS";
    case HapticMode::Shift:
        return L"GEAR SHIFT";
    case HapticMode::Surface:
        return L"BUMP";
    case HapticMode::RumbleStrip:
        return L"RUMBLE STRIP";
    case HapticMode::Limiter:
        return L"REV LIMITER";
    default:
        return L"QUIET";
    }
}

HapticMode dominantMode(const HapticFrame& brake, const HapticFrame& throttle) noexcept {
    const auto priority = [](HapticMode mode) noexcept {
        switch (mode) {
        case HapticMode::Abs:
            return 8;
        case HapticMode::Locking:
            return 7;
        case HapticMode::Traction:
            return 6;
        case HapticMode::Limiter:
            return 5;
        case HapticMode::Threshold:
            return 4;
        case HapticMode::Surface:
        case HapticMode::RumbleStrip:
            return 3;
        case HapticMode::Shift:
            return 2;
        case HapticMode::Loading:
            return 1;
        case HapticMode::Idle:
            return 0;
        default:
            return 0;
        }
    };
    return priority(throttle.mode) > priority(brake.mode) ? throttle.mode : brake.mode;
}
} // namespace pedalfeel
