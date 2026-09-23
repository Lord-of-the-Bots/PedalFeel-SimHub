#include "pedalfeel/renderer.hpp"
#include "pedalfeel/simagic_protocol.hpp"
#include <cmath>
#include <cstdlib>
#include <iostream>

using namespace pedalfeel;

namespace {
void require(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAILED: " << message << '\n';
        std::exit(1);
    }
}
} // namespace

int main() {
    BrakeRenderer renderer;
    RenderSettings settings;
    VehicleState state;
    auto quiet = renderer.render(state, settings);
    require(quiet.output < 0.001, "unloaded brake is quiet");
    require(quiet.mode == HapticMode::Quiet, "unloaded mode is quiet");
    state.brake = .7;
    state.tires[0].combinedUtilization = .9;
    state.tires[1].combinedUtilization = .9;
    auto threshold = renderer.render(state, settings);
    require(threshold.output > quiet.output, "threshold increases output");
    require(threshold.threshold > .3, "threshold layer is legible");
    state.absSeverity = .9;
    auto abs = renderer.render(state, settings);
    require(abs.mode == HapticMode::Abs, "ABS owns mode");
    require(abs.abs > .55, "ABS envelope attacks quickly");
    settings.strength = 10;
    state.brake = 3;
    state.absSeverity = 3;
    state.tires[0].slipRatio = 4;
    auto limited = renderer.render(state, settings);
    require(limited.output >= 0 && limited.output <= 1, "output is safety limited");

    BrakeRenderer corneringRenderer;
    VehicleState cornering{};
    cornering.time = 1;
    cornering.tires[0].combinedUtilization = 1.15;
    cornering.tires[1].combinedUtilization = 1.15;
    cornering.tires[0].slipRatio = .22;
    cornering.tires[1].slipRatio = .22;
    auto corneringOnly = corneringRenderer.render(cornering, RenderSettings{});
    require(corneringOnly.output == 0 && corneringOnly.mode == HapticMode::Quiet,
            "cornering without brake input never vibrates the brake pedal");

    BrakeRenderer pressureRenderer;
    VehicleState pressureOnly{};
    pressureOnly.time = 1;
    pressureOnly.brake = .85;
    auto pressureResult = pressureRenderer.render(pressureOnly, RenderSettings{});
    require(pressureResult.output > 0 && pressureResult.output < .16,
            "brake pressure creates only a restrained foundation cue");

    BrakeRenderer transientRenderer;
    VehicleState lockState{};
    lockState.time = 1;
    lockState.brake = .8;
    lockState.tires[0].slipRatio = .22;
    lockState.tires[0].locking = true;
    auto onset = transientRenderer.render(lockState, RenderSettings{});
    lockState.time = 1.1;
    lockState.tires[0].slipRatio = 0;
    lockState.tires[0].locking = false;
    auto recovered = transientRenderer.render(lockState, RenderSettings{});
    require(onset.lock > recovered.lock, "lock onset transient decays during recovery");

    BrakeRenderer downshiftRenderer;
    VehicleState downshift{};
    downshift.time = 1;
    downshift.gear = 5;
    downshift.brake = .6;
    auto beforeDownshift = downshiftRenderer.render(downshift, RenderSettings{});
    downshift.time = 1.01;
    downshift.gear = 4;
    auto afterDownshift = downshiftRenderer.render(downshift, RenderSettings{});
    require(afterDownshift.mode == HapticMode::Shift,
            "braking downshift produces a distinct brake event");
    require(afterDownshift.output > beforeDownshift.output,
            "downshift thump rises above the quiet brake background");
    require(afterDownshift.output < .45, "brake downshift remains a subordinate cue");

    BrakeRenderer thresholdDownshiftRenderer;
    VehicleState thresholdDownshift{};
    thresholdDownshift.time = 1;
    thresholdDownshift.gear = 5;
    thresholdDownshift.brake = .7;
    thresholdDownshift.tires[0].combinedUtilization = .95;
    thresholdDownshift.tires[1].combinedUtilization = .95;
    (void)thresholdDownshiftRenderer.render(thresholdDownshift, RenderSettings{});
    thresholdDownshift.time += 1.0 / 60.0;
    thresholdDownshift.gear = 4;
    const auto shiftThroughThreshold =
        thresholdDownshiftRenderer.render(thresholdDownshift, RenderSettings{});
    require(shiftThroughThreshold.mode == HapticMode::Shift,
            "downshift remains distinct beneath ordinary threshold feedback");

    BrakeRenderer coastingShiftRenderer;
    VehicleState coastingShift{};
    coastingShift.time = 1;
    coastingShift.gear = 5;
    auto beforeCoastingShift = coastingShiftRenderer.render(coastingShift, RenderSettings{});
    coastingShift.time = 1.01;
    coastingShift.gear = 4;
    auto coastingDownshift = coastingShiftRenderer.render(coastingShift, RenderSettings{});
    require(coastingDownshift.mode == HapticMode::Quiet,
            "coasting downshift does not pulse the brake");
    (void)beforeCoastingShift;

    BrakeRenderer deceleratingShiftRenderer;
    VehicleState deceleratingShift{};
    deceleratingShift.time = 1;
    deceleratingShift.gear = 5;
    deceleratingShift.longitudinalG = -.45;
    (void)deceleratingShiftRenderer.render(deceleratingShift, RenderSettings{});
    deceleratingShift.time += 1.0 / 60.0;
    deceleratingShift.gear = 4;
    const auto restingFootDownshift =
        deceleratingShiftRenderer.render(deceleratingShift, RenderSettings{});
    require(restingFootDownshift.mode == HapticMode::Shift && restingFootDownshift.output > 0,
            "deceleration carries a downshift thump to a resting brake foot");

    BrakeRenderer recentBrakeShiftRenderer;
    VehicleState recentBrakeShift{};
    recentBrakeShift.time = 1;
    recentBrakeShift.gear = 5;
    recentBrakeShift.brake = .20;
    (void)recentBrakeShiftRenderer.render(recentBrakeShift, RenderSettings{});
    recentBrakeShift.time += .10;
    recentBrakeShift.brake = 0;
    recentBrakeShift.gear = 4;
    const auto releasedAtShift = recentBrakeShiftRenderer.render(recentBrakeShift, RenderSettings{});
    require(releasedAtShift.mode == HapticMode::Shift,
            "recent brake use preserves downshift context after telemetry returns to zero");

    BrakeRenderer absDownshiftRenderer;
    VehicleState absDownshift{};
    absDownshift.time = 1;
    absDownshift.gear = 5;
    absDownshift.brake = .8;
    auto beforeAbsDownshift = absDownshiftRenderer.render(absDownshift, RenderSettings{});
    absDownshift.time = 1.01;
    absDownshift.gear = 4;
    absDownshift.absSeverity = .9;
    auto absDownshiftResult = absDownshiftRenderer.render(absDownshift, RenderSettings{});
    require(absDownshiftResult.mode == HapticMode::Abs, "ABS takes priority over a downshift");
    (void)beforeAbsDownshift;

    ThrottleRenderer throttleRenderer;
    VehicleState traction{};
    traction.time = 1;
    traction.throttle = .8;
    traction.rearSlipSeverity = .75;
    auto tractionResult = throttleRenderer.render(traction);
    require(tractionResult.output > .2, "inferred rear slip creates independent throttle output");
    require(tractionResult.mode == HapticMode::Traction,
            "inferred traction loss is not labelled as reported TC");
    ThrottleRenderer shiftRenderer;
    VehicleState shift{};
    shift.time = 1;
    shift.throttle = .9;
    shift.throttleRaw = .9;
    shift.engineRpm = 7600;
    shift.gear = 3;
    auto beforeShift = shiftRenderer.render(shift);
    shift.time = 1.01;
    shift.gear = 0;
    shift.throttle = 0;
    auto neutralShift = shiftRenderer.render(shift);
    shift.time = 1.02;
    shift.gear = 4;
    auto afterShift = shiftRenderer.render(shift);
    (void)neutralShift;
    require(afterShift.mode == HapticMode::Shift, "upshift produces a distinct throttle event");
    require(afterShift.output > beforeShift.output, "upshift kick rises above engine texture");
    require(afterShift.lock <= .16, "upshift kick remains below tire-intervention strength");
    ThrottleRenderer lowRenderer, partialRenderer, fullRenderer;
    VehicleState power{};
    power.time = 1;
    power.gear = 3;
    power.shiftRpm = 8000;
    power.engineRpm = 2500;
    power.throttle = .03;
    power.throttleRaw = .03;
    auto low = lowRenderer.render(power);
    power.engineRpm = 4500;
    power.throttle = .5;
    power.throttleRaw = .5;
    auto partial = partialRenderer.render(power);
    for (int i = 0; i < 8; ++i) {
        power.time += 1.0 / 60.0;
        partial = partialRenderer.render(power);
    }
    power.engineRpm = 7500;
    power.throttle = 1;
    power.throttleRaw = 1;
    auto full = fullRenderer.render(power);
    for (int i = 0; i < 8; ++i) {
        power.time += 1.0 / 60.0;
        full = fullRenderer.render(power);
    }
    require(low.output > 0, "idle texture crossfades into low-throttle engine texture");
    require(partial.loading >= .05, "midrange RPM builds into a clear engine texture");
    require(full.loading > partial.loading, "engine texture grows through the rev range");

    ThrottleRenderer idleRenderer;
    VehicleState idle{};
    idle.time = 1;
    idle.engineRpm = 900;
    auto idleResult = idleRenderer.render(idle, .65, .65, .75, .50);
    require(idleResult.output > 0, "idle texture creates throttle output with the pedal released");
    require(idleResult.mode == HapticMode::Idle, "idle texture has a distinct mode");
    auto disabledIdle = idleRenderer.render(idle, .65, .65, .75, 0);
    for (int i = 0; i < 12; ++i) {
        idle.time += 1.0 / 60.0;
        disabledIdle = idleRenderer.render(idle, .65, .65, .75, 0);
    }
    require(disabledIdle.output < .005, "zero idle texture fades the idle effect fully off");
    idle.engineRpm = 2200;
    idle.idleRpm = 2200;
    auto gt3Idle = idleRenderer.render(idle, .65, .65, .75, 1);
    for (int i = 0; i < 12; ++i) {
        idle.time += 1.0 / 60.0;
        gt3Idle = idleRenderer.render(idle, .65, .65, .75, 1);
    }
    require(gt3Idle.output >= IdleTextureMaximumOutput - .01,
            "full idle texture is tactile at GT3 idle speed");
    require(gt3Idle.mode == HapticMode::Idle, "GT3 idle speed retains the idle effect");
    idle.engineRpm = 4000;
    auto aboveIdle = idleRenderer.render(idle, .65, .65, .75, 1);
    for (int i = 0; i < 12; ++i) {
        idle.time += 1.0 / 60.0;
        aboveIdle = idleRenderer.render(idle, .65, .65, .75, 1);
    }
    require(aboveIdle.output < .005, "idle texture fades out above the idle range");
    idle.engineRpm = 1800;
    idle.throttle = .25;
    auto pedalPressed = idleRenderer.render(idle, .65, 0, .75, 1);
    for (int i = 0; i < 12; ++i) {
        idle.time += 1.0 / 60.0;
        pedalPressed = idleRenderer.render(idle, .65, 0, .75, 1);
    }
    require(pedalPressed.output < .005, "processed throttle demand suppresses idle texture");

    ThrottleRenderer crossfadeRenderer;
    VehicleState crossfade{};
    crossfade.time = 1;
    crossfade.engineRpm = 1800;
    crossfade.idleRpm = 1800;
    auto released = crossfadeRenderer.render(crossfade, 0, 1, 0, 1);
    for (int i = 0; i < 12; ++i) {
        crossfade.time += 1.0 / 60.0;
        released = crossfadeRenderer.render(crossfade, 0, 1, 0, 1);
    }
    crossfade.time += .01;
    crossfade.throttle = .02;
    crossfade.throttleRaw = .02;
    crossfade.engineRpm = 1900;
    auto tipIn = crossfadeRenderer.render(crossfade, 0, 1, 0, 1);
    crossfade.time += .01;
    crossfade.throttle = .08;
    crossfade.throttleRaw = .08;
    crossfade.engineRpm = 2400;
    auto transitioning = crossfadeRenderer.render(crossfade, 0, 1, 0, 1);
    crossfade.time += .01;
    crossfade.throttle = .25;
    crossfade.throttleRaw = .25;
    crossfade.engineRpm = 4000;
    auto onThrottle = crossfadeRenderer.render(crossfade, 0, 1, 0, 1);
    crossfade.time += .01;
    crossfade.throttle = 1;
    crossfade.throttleRaw = 1;
    crossfade.engineRpm = 7500;
    auto fullThrottle = crossfadeRenderer.render(crossfade, 0, 1, 0, 1);
    require(released.output > 0 && tipIn.output > 0 && transitioning.output > 0 &&
                onThrottle.output > 0,
            "idle and engine textures have no quiet gap during tip-in");
    require(tipIn.output <= released.output + .01,
            "initial throttle application does not bump above idle texture");
    require(transitioning.output + .001 >= tipIn.output &&
                onThrottle.output + .001 >= transitioning.output &&
                fullThrottle.output > onThrottle.output,
            "engine texture builds progressively through full pedal travel");

    ThrottleRenderer pitchRenderer;
    VehicleState pitch{};
    pitch.time = 3.14159265358979323846 / 11.0;
    pitch.engineRpm = 1800;
    pitch.idleRpm = 1800;
    auto idlePitch = pitchRenderer.render(pitch, 0, 1, 0, 1);
    pitch.throttle = .10;
    pitch.throttleRaw = .10;
    auto tipInPitch = pitchRenderer.render(pitch, 0, 1, 0, 1);
    pitch.throttle = .20;
    pitch.throttleRaw = .20;
    auto enginePitch = pitchRenderer.render(pitch, 0, 1, 0, 1);
    require(tipInPitch.frequencyHz >= idlePitch.frequencyHz &&
                enginePitch.frequencyHz >= tipInPitch.frequencyHz,
            "throttle texture pitch never falls during tip-in");

    ThrottleRenderer idleBandRenderer;
    VehicleState idleBand{};
    idleBand.engineRpm = 900;
    idleBand.time = 3.14159265358979323846 / 22.0;
    const auto idleBandHigh = idleBandRenderer.render(idleBand, 0, 0, 0, 1);
    idleBand.time = 3.0 * 3.14159265358979323846 / 22.0;
    const auto idleBandLow = idleBandRenderer.render(idleBand, 0, 0, 0, 1);
    require(std::abs(idleBandHigh.frequencyHz - 20.0) < .001 &&
                std::abs(idleBandLow.frequencyHz - 16.0) < .001,
            "idle texture stays within its distinct 16-to-20 Hz band");

    ThrottleRenderer engineBandRenderer;
    VehicleState engineBand{};
    engineBand.time = 1;
    engineBand.engineRpm = 900;
    engineBand.shiftRpm = 8000;
    engineBand.redlineRpm = 8000;
    engineBand.throttle = .25;
    engineBand.throttleRaw = .25;
    const auto lowEngineBand = engineBandRenderer.render(engineBand, 0, 1, 0, 0);
    engineBand.time += 1.0 / 60.0;
    engineBand.engineRpm = 8000;
    const auto highEngineBand = engineBandRenderer.render(engineBand, 0, 1, 0, 0);
    require(lowEngineBand.frequencyHz >= 24.0,
            "active engine texture starts above the separate idle throb");
    require(highEngineBand.frequencyHz <= 50.0 && highEngineBand.frequencyHz > 49.0,
            "active engine texture reaches but never exceeds the actuator ceiling");

    ThrottleRenderer rollingTipInRenderer;
    VehicleState rolling{};
    rolling.time = 1;
    rolling.engineRpm = 4000;
    rolling.shiftRpm = 8000;
    auto rollingCoast = rollingTipInRenderer.render(rolling, 0, .65, 0, .35);
    rolling.time += 1.0 / 60.0;
    rolling.throttle = .15;
    rolling.throttleRaw = .15;
    auto rollingOnset = rollingTipInRenderer.render(rolling, 0, .65, 0, .35);
    HapticFrame rollingBuilt = rollingOnset;
    for (int i = 0; i < 8; ++i) {
        rolling.time += 1.0 / 60.0;
        rollingBuilt = rollingTipInRenderer.render(rolling, 0, .65, 0, .35);
    }
    require(rollingCoast.output == 0, "rolling off-throttle remains quiet");
    require(rollingOnset.output > 0 && rollingOnset.output < rollingBuilt.output,
            "rolling throttle texture fades in instead of switching on at a fixed floor");
    require(rollingOnset.mode == HapticMode::Loading,
            "active low-throttle engine texture is never labelled quiet");
    require(rollingOnset.output < .04,
            "first rolling throttle frame remains a subtle tactile onset");

    const auto sustainedTexture = [](double rpmStep) {
        ThrottleRenderer renderer;
        VehicleState sample{};
        sample.time = 1;
        sample.engineRpm = 4000;
        sample.shiftRpm = 8000;
        (void)renderer.render(sample, 0, .65, 0, 0);
        sample.throttle = .8;
        sample.throttleRaw = .8;
        HapticFrame result{};
        for (int i = 0; i < 30; ++i) {
            sample.time += 1.0 / 60.0;
            sample.engineRpm += rpmStep;
            result = renderer.render(sample, 0, .65, 0, 0);
        }
        return result;
    };
    const auto loadedHighGear = sustainedTexture(2.0);
    const auto sweepingLowGear = sustainedTexture(25.0);
    require(sweepingLowGear.loading > loadedHighGear.loading * 1.25,
            "rapid RPM sweep feels richer than a loaded high-gear steady buzz");

    const auto steadyTextureAt = [](double rpm) {
        ThrottleRenderer renderer;
        VehicleState sample{};
        sample.time = 1;
        sample.engineRpm = rpm;
        sample.shiftRpm = 9000;
        sample.throttle = .7;
        sample.throttleRaw = .7;
        HapticFrame result{};
        for (int i = 0; i < 30; ++i) {
            sample.time += 1.0 / 60.0;
            result = renderer.render(sample, 0, .65, 0, 0);
        }
        return result;
    };
    const auto steadyLowRpm = steadyTextureAt(2500);
    const auto steadyHighRpm = steadyTextureAt(7500);
    require(steadyHighRpm.loading > steadyLowRpm.loading * 1.65,
            "steady high-RPM texture is substantially stronger than low-RPM texture");
    require(steadyHighRpm.loading >= .15,
            "high-RPM texture is tactile without raising the actuator minimum");

    ThrottleRenderer limiterRenderer;
    VehicleState limiter{};
    limiter.time = 1.01;
    limiter.gear = 3;
    limiter.engineRpm = 8000;
    limiter.shiftRpm = 8000;
    limiter.throttle = 1;
    limiter.throttleRaw = 1;
    limiter.engineWarningsAvailable = true;
    limiter.revLimiterActive = true;
    const auto limiterResult = limiterRenderer.render(limiter, 1, .65, 1, 0);
    require(limiterResult.mode == HapticMode::Limiter,
            "reported iRacing limiter state creates a distinct throttle cue");
    require(limiterResult.output > .10 && limiterResult.frequencyHz >= 38,
            "rev limiter creates a clear rough pulse above engine texture");

    ThrottleRenderer unavailableLimiterRenderer;
    limiter.engineWarningsAvailable = false;
    const auto unavailableLimiter = unavailableLimiterRenderer.render(limiter, 1, .65, 1, 0);
    require(unavailableLimiter.mode != HapticMode::Limiter,
            "cars without EngineWarnings never receive an inferred limiter cue");

    ThrottleRenderer limiterTractionRenderer;
    limiter.engineWarningsAvailable = true;
    limiter.rearSlipSeverity = 1;
    const auto tractionOverLimiter = limiterTractionRenderer.render(limiter, 1, .65, 1, 0);
    require(tractionOverLimiter.mode == HapticMode::Traction,
            "genuine traction loss retains priority over the rev limiter");

    HapticFrame quietBrake{}, engineChassis{};
    engineChassis.output = .20;
    engineChassis.frequencyHz = 42;
    engineChassis.mode = HapticMode::Loading;
    const auto engineAtBrake = mixBrakeChassisCue(quietBrake, engineChassis);
    require(engineAtBrake.mode == HapticMode::Loading && engineAtBrake.output >= .023 &&
                engineAtBrake.output <= .025 && engineAtBrake.frequencyHz == 42,
            "a restrained engine layer reaches an unpressed brake pedal");
    HapticFrame thresholdBrake{};
    thresholdBrake.output = .4;
    thresholdBrake.frequencyHz = 28;
    thresholdBrake.mode = HapticMode::Threshold;
    const auto thresholdOwnsBrake = mixBrakeChassisCue(thresholdBrake, engineChassis);
    require(thresholdOwnsBrake.output == thresholdBrake.output &&
                thresholdOwnsBrake.frequencyHz == thresholdBrake.frequencyHz,
            "brake threshold information excludes shared chassis character");
    engineChassis.mode = HapticMode::Traction;
    require(mixBrakeChassisCue(quietBrake, engineChassis).output == 0,
            "throttle traction information is never copied onto the brake");

    ThrottleRenderer clearEngineRenderer, tractionPriorityRenderer;
    VehicleState powered{};
    powered.time = 1;
    powered.engineRpm = 6000;
    powered.shiftRpm = 8000;
    powered.throttle = .8;
    powered.throttleRaw = .8;
    auto clearEngine = clearEngineRenderer.render(powered, 1, 1, 1, 0);
    powered.rearSlipSeverity = 1;
    auto underTractionLoss = tractionPriorityRenderer.render(powered, 1, 1, 1, 0);
    require(underTractionLoss.mode == HapticMode::Traction, "traction loss owns the throttle cue");
    require(underTractionLoss.loading < clearEngine.loading * .35,
            "traction loss strongly ducks background engine texture");

    SurfaceRenderer leftSurfaceRenderer;
    VehicleState leftImpact{};
    leftImpact.time = 1;
    leftImpact.speedKph = 100;
    leftImpact.surfaceTelemetryAvailable = true;
    leftImpact.surfaceImpactSeverity = .8;
    leftImpact.surfaceImpactDirection = -1;
    leftImpact.surfaceImpactConfidence = .9;
    const auto leftSurface = leftSurfaceRenderer.render(leftImpact, 1);
    require(leftSurface.brake.mode == HapticMode::Surface && leftSurface.brake.output > 0,
            "a validated suspension impact creates a sparse surface event");
    SurfaceHaptics sustainedSurface = leftSurface;
    for (int i = 0; i < 20; ++i) {
        leftImpact.time += 1.0 / 60.0;
        sustainedSurface = leftSurfaceRenderer.render(leftImpact, 1);
    }
    require(sustainedSurface.brake.output == 0 && sustainedSurface.throttle.output == 0,
            "a sustained suspension value cannot become continuous road buzz");

    SurfaceRenderer rightSurfaceRenderer;
    leftImpact.surfaceImpactDirection = 1;
    const auto rightSurface = rightSurfaceRenderer.render(leftImpact, 1);
    require(std::abs(leftSurface.brake.output - rightSurface.brake.output) < .001 &&
                std::abs(leftSurface.throttle.output - rightSurface.throttle.output) < .001,
            "bump direction does not create artificial left/right pedal routing");
    require(rightSurface.brake.output >= rightSurface.throttle.output * 1.59,
            "brake bump output compensates for the stiffer pedal");

    SurfaceRenderer rumbleRenderer;
    VehicleState rumble{};
    rumble.time = 1;
    rumble.speedKph = 120;
    rumble.rumbleStripTelemetryAvailable = true;
    rumble.rumbleStripSeverity = .75;
    rumble.rumbleStripFrequencyHz = 82;
    auto rumbleResult = rumbleRenderer.render(rumble, 1);
    for (int i = 0; i < 4; ++i) {
        rumble.time += 1.0 / 60.0;
        rumbleResult = rumbleRenderer.render(rumble, 1);
    }
    require(rumbleResult.brake.mode == HapticMode::RumbleStrip &&
                rumbleResult.throttle.mode == HapticMode::RumbleStrip,
            "direct tire rumble telemetry creates a distinct rumble-strip effect");
    require(rumbleResult.brake.output >= rumbleResult.throttle.output * 1.59,
            "rumble strips retain brake transmission compensation");
    require(rumbleResult.throttle.frequencyHz > 18 && rumbleResult.throttle.frequencyHz <= 42,
            "rumble pitch is compressed into the useful pedal frequency band");
    for (int i = 0; i < 12; ++i) {
        rumble.time += 1.0 / 60.0;
        rumble.rumbleStripSeverity = 0;
        rumble.rumbleStripFrequencyHz = 0;
        rumbleResult = rumbleRenderer.render(rumble, 1);
    }
    require(rumbleResult.brake.output < .01 && rumbleResult.throttle.output < .01,
            "rumble strip output releases promptly after tire contact ends");

    SurfaceRenderer centralSurfaceRenderer;
    leftImpact.surfaceImpactConfidence = .1;
    const auto centralSurface = centralSurfaceRenderer.render(leftImpact, 1);
    require(std::abs(centralSurface.brake.output - leftSurface.brake.output) < .001 &&
                std::abs(centralSurface.throttle.output - leftSurface.throttle.output) < .001,
            "uncertain bumps use the same full-strength two-pedal routing");

    HapticFrame engineBackground{};
    engineBackground.output = .08;
    engineBackground.frequencyHz = 38;
    engineBackground.mode = HapticMode::Loading;
    const auto surfaceOverBackground = mixSurfaceCue(engineBackground, leftSurface.brake);
    require(surfaceOverBackground.mode == HapticMode::Surface &&
                surfaceOverBackground.frequencyHz == leftSurface.brake.frequencyHz,
            "surface events replace background character briefly");
    HapticFrame thresholdBackground{};
    thresholdBackground.output = .35;
    thresholdBackground.frequencyHz = 29;
    thresholdBackground.mode = HapticMode::Threshold;
    const auto bumpUnderThreshold = mixSurfaceCue(thresholdBackground, leftSurface.brake);
    require(bumpUnderThreshold.mode == HapticMode::Threshold &&
                bumpUnderThreshold.output > thresholdBackground.output &&
                bumpUnderThreshold.frequencyHz == thresholdBackground.frequencyHz,
            "most of a bump pulse remains perceptible without replacing threshold information");
    HapticFrame absPriority{};
    absPriority.output = .7;
    absPriority.frequencyHz = 24;
    absPriority.mode = HapticMode::Abs;
    const auto surfaceUnderAbs = mixSurfaceCue(absPriority, leftSurface.brake);
    require(surfaceUnderAbs.mode == HapticMode::Abs && surfaceUnderAbs.output == absPriority.output,
            "ABS is never displaced by a surface event");

    HapticFrame brakePriority{}, throttlePriority{};
    brakePriority.mode = HapticMode::Threshold;
    throttlePriority.mode = HapticMode::Traction;
    require(dominantMode(brakePriority, throttlePriority) == HapticMode::Traction,
            "traction outranks a brake threshold warning");
    brakePriority.mode = HapticMode::Abs;
    require(dominantMode(brakePriority, throttlePriority) == HapticMode::Abs,
            "ABS has highest cross-pedal priority");

    VehicleState replayState{};
    replayState.drivingActive = false;
    replayState.brake = replayState.throttle = 1;
    replayState.absSeverity = replayState.rearSlipSeverity = 1;
    replayState.surfaceTelemetryAvailable = replayState.rumbleStripTelemetryAvailable = true;
    replayState.surfaceImpactSeverity = replayState.rumbleStripSeverity = 1;
    require(BrakeRenderer{}.render(replayState, RenderSettings{}).output == 0 &&
                ThrottleRenderer{}.render(replayState).output == 0 &&
                SurfaceRenderer{}.render(replayState, 1).brake.output == 0,
            "replay, garage and out-of-car telemetry are gated before rendering");

    auto report = simagic::makeReport(simagic::BrakeChannel, true, 25, 40);
    require(report.size() == 49, "feature report is 49 bytes");
    require(report[0] == 0xF1 && report[1] == 0xEC, "feature report header");
    require(report[2] == 1 && report[3] == 1, "brake channel enabled");
    require(report[4] == 25 && report[5] == 40, "frequency and intensity bytes");
    for (std::size_t i = 6; i < report.size(); ++i)
        require(report[i] == 0, "feature report tail is zero-filled");
    auto clamped = simagic::makeReport(simagic::ThrottleChannel, true, 500, 200);
    require(clamped[2] == 2 && clamped[4] == 50 && clamped[5] == 100,
            "hardware ranges are clamped");
    auto stopped = simagic::makeReport(simagic::ClutchChannel, false, 0, 0);
    require(stopped[3] == 0 && stopped[4] == 0 && stopped[5] == 0, "stop report is safe");
    auto zeroFrequency = simagic::makeReport(simagic::BrakeChannel, true, 0, 40);
    require(zeroFrequency[3] == 0 && zeroFrequency[4] == 0 && zeroFrequency[5] == 0,
            "zero Hz means off");
    auto activeFloor = simagic::makeReport(simagic::BrakeChannel, true, 5, 40);
    require(activeFloor[3] == 1 && activeFloor[4] == 10 && activeFloor[5] == 40,
            "active frequency is clamped to 10 Hz");
    require(simagic::calibrateIntensity(0, 12, 68) == 0, "calibration preserves a true off state");
    require(simagic::calibrateIntensity(1, 12, 68) == 12,
            "first active command reaches tactile threshold");
    require(simagic::calibrateIntensity(100, 12, 68) == 68,
            "full command respects comfortable ceiling");
    const auto midpoint = simagic::calibrateIntensity(50, 12, 68);
    require(midpoint >= 39 && midpoint <= 41, "calibration expands the useful intensity range");
    constexpr simagic::IntensityCurve frequencyMinimum{8, 10, 14, 22};
    constexpr simagic::IntensityCurve frequencyMaximum{60, 64, 70, 82};
    require(simagic::calibrateIntensity(0, 35, frequencyMinimum, frequencyMaximum) == 0,
            "frequency calibration preserves the true off state");
    require(simagic::calibrateIntensity(1, 16, frequencyMinimum, frequencyMaximum) == 8 &&
                simagic::calibrateIntensity(1, 30, frequencyMinimum, frequencyMaximum) == 12,
            "frequency calibration interpolates the tactile threshold between anchors");
    require(simagic::calibrateIntensity(100, 10, frequencyMinimum, frequencyMaximum) == 60 &&
                simagic::calibrateIntensity(100, 50, frequencyMinimum, frequencyMaximum) == 82,
            "frequencies outside the curve use safe endpoint ceilings");
    constexpr simagic::IntensityCurve descending{24, 20, 16, 12};
    require(simagic::interpolateIntensityCurve(descending, 30) == 18,
            "curve interpolation handles actuators that become stronger at high frequency");
    std::cout << "All PedalFeel engine tests passed.\n";
}
