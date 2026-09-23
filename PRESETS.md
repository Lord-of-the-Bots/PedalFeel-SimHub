# Starting presets and named profiles — 0.4.0

**Original GT3 Balanced** is selected by default when creating a profile. Editing a profile does not change its starting preset; you can always create another independent profile from the same preset.

There are three presets based on the author's settings: **GT3 Balanced, Subtle and Aggressive**. Their effect coefficients come from `src/win32_main.cpp::applyProfile` in PedalFeel 0.19.0, commit `06649f3cd7c59abaa5f928d82752ae9d8a3956ef`. At the user's request, the upshift starting value is 20% in every new base; the other original effect coefficients are retained. Upshift remains adjustable from 0–100%. **The original has no separate GT4 preset.** Earlier GT4 and other car-family presets were provisional choices made for this integration; previously saved settings are imported and remain available as named profiles.

| Base | Threshold | Brake | Grip warning | ABS | Rear traction | Engine | Idle | Upshift |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Original GT3 Balanced | 0.91 | 70% | 62% | 72% | 65% | 48% | 28% | 20% |
| Original GT3 Subtle | 0.93 | 52% | 45% | 58% | 48% | 30% | 16% | 20% |
| Original GT3 Aggressive | 0.87 | 88% | 84% | 90% | 82% | 68% | 40% | 20% |
| Formula cars · 0.3.4 settings | 0.99 | 52% | 28% | 65% | 30% | 28% | 12% | 20% |

In the Original GT3 bases, road feedback is 28%, and limiter and downshift are 100% of their original fixed amplitudes. This is **not 100% motor power**: the profile's overall multiplier and frequency calibration apply afterward. Every new base scales the original mixed signal by 2.1, shown as ×1 on the slider. This is an integration setting, separate from the author's effect coefficients. Copies and imported profiles keep their saved values.

In the **Formula cars** base, road feedback is 18%, and limiter and downshift are both 20%. Overall strength defaults to the same value as the other new bases: a gain of 2.1, shown as ×1. The slider ranges from ×0 to ×2 and is saved in the profile. This is a provisional setup based on user feedback, not a setup validated across all formula cars.

## Creating and assigning profiles

- A new profile gets its own ID, name and copy of the selected base. Duplicate and blank names are rejected with an explanation.
- **Copy of the selected profile** copies its effects and overall strength. Editing the copy does not change the original profile.
- **Assign to current car** saves the link between that car and the profile. The profile is selected automatically the next time the car loads.
- One profile can be assigned to several cars. Changes to that profile apply to every car assigned to it.
- Selecting a profile alone does not assign it to the car. A car without an assignment uses Original GT3 Balanced.
- Frequency minimums/maximums, physical channels and automatic activation are shared by the device. A profile changes the feedback while retaining these motor settings.

## Updating older settings

The 0.4.0 format adds a profile library and car assignments. Previously saved car settings are imported into separate profiles, retaining their effective values, including the former device-wide overall gain. Migration runs once. When a car's provisional name becomes associated with a permanent ID, its assignment is retained.

The car catalog still helps identify names and paths, but its 201 entries do not represent individually tuned setups. New profiles use an explicitly selected base; individual effect previews and short drives help refine the actual feel.

Coefficient source: [PedalFeel applyProfile](https://github.com/UdaraJay/PedalFeel/blob/06649f3cd7c59abaa5f928d82752ae9d8a3956ef/src/win32_main.cpp#L449-L468).
