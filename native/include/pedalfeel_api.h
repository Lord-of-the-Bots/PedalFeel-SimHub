#pragma once
#include <stdint.h>

#if defined(_WIN32)
#define PF_CALL __cdecl
#if defined(PF_BUILD_DLL)
#define PF_API __declspec(dllexport)
#elif defined(PF_USE_DLL)
#define PF_API __declspec(dllimport)
#else
#define PF_API
#endif
#else
#define PF_CALL
#define PF_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ABI v4. All callers MUST use pack=8, 32-bit integers and IEEE-754 doubles.
 * Set size=sizeof(struct), version=PF_ABI_VERSION before every call.
 * All operations on the same handle, including destroy, must be serialized.
 * This library reads iRacing shared memory only. It never opens/writes HID devices.
 */
#define PF_ABI_VERSION 4u
#define PF_OK 0
#define PF_INVALID_ARGUMENT (-1)
#define PF_INTERNAL_ERROR (-2)

#pragma pack(push, 8)
typedef struct PfConfig {
    uint32_t size;
    uint32_t version;
    int32_t brake_enabled;
    int32_t throttle_enabled;
    double grip_threshold;
    double brake_strength;
    double brake_texture;
    double abs_punch;
    double traction_strength;
    double engine_texture;
    double shift_kick;
    double idle_texture;
    double surface_strength;
    /* Four calibration anchors at 16, 25, 35, 50 Hz, per logical pedal.
     * Each minimum/maximum is 0..100, minimum <= maximum.
     * Defaults are minimum=0 and maximum=35 until explicitly calibrated.
     */
    int32_t brake_minimum[4];
    int32_t brake_maximum[4];
    int32_t throttle_minimum[4];
    int32_t throttle_maximum[4];
    /* Applied after effect mixing and before rounding/calibration. Range 0..4.2,
     * default 2.1. Zero silences output; calibrated ceilings still apply. */
    double effects_gain;
    /* Independent rev-limiter cue strength, 0..1, default .20.
     * Zero removes its pulse, engine ducking and mode/frequency takeover. */
    double limiter_strength;
    /* Independent brake downshift pulse, 0..1, default .20. */
    double downshift_kick;
    double throttle_strength, brake_engine_texture, brake_idle_texture;
} PfConfig;

typedef struct PfOutput {
    uint32_t size;
    uint32_t version;
    int32_t new_sample;
    int32_t connected;
    int32_t driving_active;
    int32_t stale;
    int32_t brake_hz;
    int32_t brake_intensity;
    int32_t throttle_hz;
    int32_t throttle_intensity;
    /* 0 quiet, 1 idle, 2 loading, 3 threshold, 4 locking, 5 ABS,
     * 6 traction, 7 surface, 8 rumble strip, 9 shift, 10 limiter. */
    int32_t brake_mode;
    int32_t throttle_mode;
    /* Mixed renderer signal before effects_gain and calibration. */
    double brake_raw;
    double throttle_raw;
    /* -1 until the first sample. Stale at age >=250ms. */
    double sample_age_ms;
    double session_time;
} PfOutput;
#pragma pack(pop)

/* Returns null on allocation failure. */
PF_API void* PF_CALL pf_create(void);
PF_API void PF_CALL pf_destroy(void* handle);
/* Rejects invalid/NaN settings atomically; preserves filter/gear history on success; waits for a new sample.
 * grip_threshold range is .75..1.05; effects_gain is 0..4.2;
 * individual effect strengths are 0..1. */
PF_API int32_t PF_CALL pf_configure(void* handle, const PfConfig* config);
/* Poll frequently (e.g. 4ms). Repeated frames are not re-rendered. All output
 * is zeroed immediately on disconnect/inactive driving, or after 250ms without
 * a new sample. Inspect output even when new_sample==0 to observe stale stops. */
PF_API int32_t PF_CALL pf_tick(void* handle, PfOutput* output);
/* Optional helper: caller initializes size/version first. */
PF_API int32_t PF_CALL pf_default_config(PfConfig* config);

/* Isolated synthetic effect previews. IDs are stable; this additive export keeps
 * ABI v4 adds independent pedal/engine settings. The DLL still never writes hardware. */
typedef enum PfPreviewEffect {
    PF_PREVIEW_BRAKE_LOADING = 0,
    PF_PREVIEW_THRESHOLD = 1,
    PF_PREVIEW_LOCKING = 2,
    PF_PREVIEW_ABS = 3,
    PF_PREVIEW_DOWNSHIFT = 4,
    PF_PREVIEW_TRACTION = 5,
    PF_PREVIEW_ENGINE = 6,
    PF_PREVIEW_LIMITER = 7,
    PF_PREVIEW_IDLE = 8,
    PF_PREVIEW_UPSHIFT = 9,
    PF_PREVIEW_SURFACE = 10,
    PF_PREVIEW_RUMBLE = 11,
    PF_PREVIEW_BRAKE_ENGINE = 12, PF_PREVIEW_BRAKE_IDLE = 13
} PfPreviewEffect;
/* Replays deterministic synthetic telemetry through isolated original renderers
 * at 60 Hz, then applies the current gain, calibration and channel enables.
 * Effects 0..4 use brake only, 5..9 throttle only, 10..11 both enabled pedals.
 * elapsed_seconds >=2 returns silence. Invalid config/ID/time/header rejects
 * atomically without changing output. Negative/nonfinite times are invalid.
 * A valid active preview sets new_sample/driving_active, never connected: this
 * API does not inspect a live game or device. No runtime handle/state is used. */
PF_API int32_t PF_CALL pf_preview(const PfConfig* config, int32_t effect,
                                double elapsed_seconds, PfOutput* output);

#ifdef __cplusplus
}
static_assert(sizeof(PfConfig) == 200, "PfConfig ABI size");
static_assert(sizeof(PfOutput) == 80, "PfOutput ABI size");
#endif
