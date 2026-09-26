# 0.5.0

- Two built-in starting profiles: Standard with the latest supplied tuning, and the existing Original GT3 Balanced setup.
- Create and delete custom profiles, keeping car assignments. Deleting a profile switches its cars to Standard and shows a notification.
- Separate brake and throttle strength; fixed common baseline at the previous ×1.
- Separate engine vibration and idle controls on the brake pedal.
- Longer shift impulses (160 ms), stronger upshift, and protection from road cues replacing the shift pulse.
- Slider edits preserve running effect state. Temporary telemetry identity changes no longer rebuild the controls.
- Output levels and navigation remain visible while scrolling, including inside SimHub's surrounding scroll container.
- Removed the brake loading and wheel lock preview buttons. Effect examples use moderate synthetic scenarios and the same output pipeline as driving. Direct 500 ms calibration tests are unchanged.
- Updated labels in all seven supported UI languages.

The upgrade keeps a dedicated copy of the previous settings catalog. Existing motor calibration and custom profiles are retained. Retired built-in variants move to Standard; the old common gain returns to ×1.

Feedback on the revised shift feel and effect examples is welcome after trying them in a live session.
