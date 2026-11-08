# Space Services 1.1.4 Changelog

## RimWorld 1.7 pre-release support

- Added a native 1.7 assembly and separate versioned content, retaining the 1.6 payload and load route.
- Tested on **RimWorld 1.7.5000 rev1275**, including migration of a 1.6 colony with active Hospital patients and Hospitality guests, service turnover, and save/load checkpoints.

## Compatibility fixes

- Added 1.7 Room Service compatibility guards for non-bed definitions and missing cooldown data during old-save loading, preserving existing serialized data.
- Updated the 1.7 Vanilla Events Expanded hazard rules for its remade orbital-crash events and removed the obsolete Psychic Rain traffic hold.
- Excluded Vanilla Gravship Expanded Chapter 2 war-platform combat maps from service-colony eligibility on 1.7.

## Experimental Hospitality ownership workaround — 1.7 only

- Added an **off-by-default** option under Space Services → Experimental for the inspected Hospitality build's stale guest-bed ownership issue.
- When enabled and authorized, corrects ownership cleanup during unclaiming and repairs narrowly identified stale references on departed, mothballed guests before load initialization.
- Exact-build checks cover the loaded Hospitality DLL, the tested game assembly, package identity, and required method/field layouts. A changed or unrecognized build leaves the workaround inactive while retaining the saved preference; settings display its effective status.
- Enable the option **before loading** a save that needs repair. Restart the game after game or mod updates. This is a targeted workaround, not a general guest-save repair tool.
- Verified repair of the affected fixture, ordinary Smart Speed progression through a guest departure, and fresh-process reload without ownership reassignment. The 1.6 build does not include this workaround.

## Testing notes

- Not every optional-mod combination or bed reassignment path is verified.
- Hospitality's separate same-session guest-cache issue can produce faction-relation errors after reloading; a full game restart cleared those errors in the tested colony. This update does not fix that upstream cache issue.
