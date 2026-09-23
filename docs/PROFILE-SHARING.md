# Sharing profiles

[Open the profile form](https://github.com/Lord-of-the-Bots/PedalFeel-SimHub/issues/new?template=profile.yml)

Share settings you have actually driven with, including what remains weak, overwhelming or misleading. The purpose is to build a useful car/class library with known hardware and limitations. A report does not have to be a finished recommendation.

## Include both settings sections

1. **Feel in game:** show the selected profile and base, overall strength, and all brake, throttle and road settings. Use several screenshots if the page does not fit.
2. **Pedal setup:** show all 16, 25, 35 and 50 Hz minimum/maximum values for every pedal you use, its physical channel and whether it is enabled.

Effect percentages alone do not describe the motor output. Frequency calibration, actuator mounting and pedal stiffness can change the feel considerably. Include both sections even if the calibration is unchanged from the defaults.

## Describe the test

- Exact car/model and class. List other cars separately if you also tried them.
- Pedals, actuator model, controller, mounting and relevant modifications.
- SimHub and PedalFeel for SimHub versions.
- Track, corner or scenario: braking, lockup, ABS, acceleration, limiter, shifts, bumps or kerbs.
- Starting profile/base, changes you made, what helps and what needs work.
- What you have not tested. State explicitly if you have only used the effect previews or direct motor tests.

If a change improves the same setup, add a follow-up to the issue with the changed values and scenario. This keeps the progression and remaining limitations together.

## Trying someone else's settings

Create a copy of your profile before changing its values. Remember that edits to a shared profile affect all cars assigned to it. Use your own comfortable motor calibration; the same percentages can feel different on another mounting or pedal.

There is no one-click single-profile import/export interface in this release. Screenshots and explicit values are the current exchange format. Do not replace your complete device JSON with someone else's: it also includes physical channels, the profile library and car assignments. Share only screenshots and files you intend to make public.
