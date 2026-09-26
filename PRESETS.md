# Profiles — 0.5.1

Two built-in starting profiles are available: **Standard** and **Original GT3**. Both can be tuned. Use **Create your own profile** to make an independent copy; user-created profiles can be deleted. Bind a profile to the current car with **Assign to current car**. Deleting a bound profile assigns Standard to affected cars and shows a notification.

| Setting | Standard | Original GT3 |
| --- | ---: | ---: |
| Brake strength | 60% | 70% |
| Throttle strength | 100% | 100% |
| Warning threshold | 1.00 | 0.91 |
| Grip warning | 35% | 62% |
| ABS | 70% | 72% |
| Downshift | 100% | 100% |
| Rear grip loss | 30% | 65% |
| Throttle engine | 25% | 48% |
| Throttle idle | 25% | 28% |
| Limiter | 25% | 100% |
| Upshift | 65% | 20% |
| Road | 70% | 28% |
| Brake engine / idle | 0% / 0% | 0% / 0% |

The common baseline stays at 2.1, equivalent to the former ×1. Percentages are effect coefficients, not constant motor power. Calibration limits still apply. Original GT3 keeps the integration's previous Balanced coefficients derived from UdaraJay/PedalFeel 0.19.0, including its previously adjusted upshift value; it is not an untouched standalone PedalFeel configuration.

Older user-created profiles and car assignments are retained. Retired built-in variants are replaced by Standard and old common gains reset to ×1. A dedicated `.before-0.5.0` settings snapshot preserves the prior catalog. Device calibration and channels remain shared and are not replaced by profile selection.

0.5.1 updates unchanged old Standard coefficients once. Custom values, other profiles and motor calibration are preserved. Use **Restore base settings** on Standard to apply the entire latest starting setup.
