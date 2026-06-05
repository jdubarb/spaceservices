# Space Services

Space Services is a RimWorld 1.6/Odyssey compatibility mod for running Hospital and Hospitality-style services on stationary space maps: asteroid bases, orbit maps, moons, and station-like facilities.

Actual travelling gravships are intentionally blocked.

## What It Does

- Adds a 7x7 service landing pad for Hospital patients and Hospitality guests.
- Lets Hospital patient arrivals, departures, and mass-casualty events work on eligible stationary space maps.
- Lets Hospitality guests arrive, stay, and depart by service shuttle on eligible stationary space maps.
- Adds pad modes for hospital-only, hospitality-only, shared, priority, and departures-only behavior.
- Handles service shuttle visuals, visitor cleanup, stale record cleanup, and vacuum-safe arrivals/departures.
- Provides XML hooks for modders to add hazards, shuttle visuals, vacuum apparel sets, and Hospital treatment hediffs.

## Requirements

Required:

- Harmony
- RimWorld Odyssey

Soft integrations:

- Hospital
- Hospitality
- Simply More Roofs
- Vanilla Events Expanded
- VE Events Space maps Compatibility Patch
- Architect Icons / Better Architect Menu
- MedPod, experimental and disabled by default

Not supported directly:

- Rimsential Spaceports
- Trader Ships

Fuel sales and trade traffic are planned as a future Space Services addon instead of direct Spaceports or Trader Ships integration.

## Install

The playable mod folder is:

```text
SpaceServices/
```

Install that folder as the RimWorld mod.

`SpaceServicesTradeFuel/` is staged as the future optional Trade and Fuel addon. It is not gameplay-complete yet.

## Build

```bash
cd SpaceServices/Source
./build.sh
```

The build writes:

```text
SpaceServices/1.6/Assemblies/SpaceServices.dll
```

The script can discover common Steam library paths. Override paths with `RIMWORLD_MANAGED_DIR`, `RIMWORLD_DIR`, `HARMONY_DLL`, or `STEAM_LIBRARY_DIRS` if needed.

## Gameplay Notes

- Keep Hospitality guest areas inside sealed, safe rooms. Include the service-adjacent paths guests are allowed to use.
- Exposed pads should be close to pressurized rooms. Odyssey can still apply vacuum burns below `100%` vacuum resistance.
- Sealed pads can allow most arrivals to skip vacsuits. Exposed pads require practical `100%` vacuum resistance.
- Traffic rates are deterministic by default. At `0.25x`, every fourth eligible service attempt is allowed, not a random 25% chance.
- Vanilla Gravship Expanded - Chapter 1 asteroid showers can be blocked on eligible stationary service maps through a default-on compatibility setting.
- MedPod support works, but MedPod itself can heavily reduce TPS while occupied. The bridge is experimental and off by default.

## Mod Integration

See [MODDING.md](MODDING.md) for XML hooks and compatibility notes.

Current extension points include:

- `SpaceServiceHazardRuleDef` and `SpaceServiceHazardExtension` for events and game conditions that should block arrivals or delay departures.
- `SpaceServiceHospitalTreatmentHediffDef` for long-term Hospital conditions that should keep patients in bed instead of immediately discharging after a tend.
- `SpaceServiceShuttleVisualDef` and `SpaceServiceShuttleVisualExtension` for weighted service shuttle visuals.
- `SpaceServiceVacuumApparelSetDef` for explicit fallback vacuum apparel sets.
- Map eligibility signals for stationary space maps.

## License

Space Services is licensed under GNU GPLv3. See [LICENSE](LICENSE) and [SpaceServices/LICENSE.txt](SpaceServices/LICENSE.txt).

Forks, continuations, translations, compatibility patches, and ports are welcome under GPLv3 terms. Please keep attribution and link back to the original project where practical.

## Repository Note

Forgejo is the primary development repository. This GitHub repository is a publishable/staging view rooted at the `mods/` directory.
