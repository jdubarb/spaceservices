# Space Services Modding Guide

This file documents the compatibility hooks Space Services exposes for other RimWorld mods.

The loadable mod folder is `SpaceServices/`. XML examples below can live in your own mod's `Defs/` folder. Use normal RimWorld load order or dependency rules to make sure Space Services is loaded before XML that references its C# def classes.

## Common Fields

Several Space Services defs share these fields:

- `enabled`: optional, defaults to `true`.
- `requiredPackageIds`: optional list of package IDs that must be active for the rule to apply.
- `serviceKinds`: optional list of service kinds. Omit it to apply to every service. Current values are `hospital` and `hospitality`.

Package ID checks are case-insensitive and accept active mods from RimWorld's running mod list.

## Event Hazards

Use `SpaceServices.SpaceServiceHazardRuleDef` when an active `GameConditionDef` should block service arrivals or delay service departures.

```xml
<SpaceServices.SpaceServiceHazardRuleDef>
  <defName>MyMod_SolarStorm_ServiceHazard</defName>
  <label>my mod solar storm service hazard</label>
  <requiredPackageIds>
    <li>author.mymod</li>
  </requiredPackageIds>
  <gameConditionDefNames>
    <li>MyMod_SolarStorm</li>
  </gameConditionDefNames>
  <serviceKinds>
    <li>hospital</li>
    <li>hospitality</li>
  </serviceKinds>
  <blockArrivals>true</blockArrivals>
  <delayDepartures>true</delayDepartures>
  <reason>solar storm unsafe for shuttle traffic</reason>
</SpaceServices.SpaceServiceHazardRuleDef>
```

Fields:

- `gameConditionDefNames`: active game conditions matched by defName.
- `incidentDefNames`: reserved in the data class. Do not rely on this as public behavior yet.
- `blockArrivals`: stops new service shuttles while the hazard is active.
- `delayDepartures`: keeps existing patients or guests on map until extraction is safe.
- `reason`: player-facing short reason used in status and alert text.

You can also attach `SpaceServices.SpaceServiceHazardExtension` directly to a `GameConditionDef`:

```xml
<GameConditionDef ParentName="SomeParent">
  <defName>MyMod_IonStorm</defName>
  <label>ion storm</label>
  <modExtensions>
    <li Class="SpaceServices.SpaceServiceHazardExtension">
      <serviceKinds>
        <li>hospital</li>
        <li>hospitality</li>
      </serviceKinds>
      <blockArrivals>true</blockArrivals>
      <delayDepartures>true</delayDepartures>
      <reason>ion storm unsafe for shuttle traffic</reason>
    </li>
  </modExtensions>
</GameConditionDef>
```

Native RimWorld flags are also respected where possible:

- `preventShuttleLaunch` blocks Space Services shuttle launch and landing flow.
- `preventNeutralVisitors` blocks Hospitality-style service arrivals.
- `causesTraderCaravanExit` is reserved for future trade service behavior.

Space Services normally only gates its own service traffic. The default-on Vanilla Gravship Expanded - Chapter 1 asteroid shower setting is a specific exception for an incident that can destroy stationary service bases before a service hazard condition exists.

## Hospital Treatment Hediffs

Use `SpaceServices.SpaceServiceHospitalTreatmentHediffDef` for diseases or long-running hediffs that should keep Hospital patients under treatment instead of letting Hospital discharge them immediately after the first tend.

```xml
<SpaceServices.SpaceServiceHospitalTreatmentHediffDef>
  <defName>MyMod_HospitalTreatment_LongDisease</defName>
  <label>my mod Hospital treatment conditions</label>
  <requiredPackageIds>
    <li>author.mymod</li>
  </requiredPackageIds>
  <minSeverity>0.01</minSeverity>
  <hediffDefNames>
    <li>MyMod_LongDisease</li>
    <li>MyMod_SevereInfection</li>
  </hediffDefNames>
</SpaceServices.SpaceServiceHospitalTreatmentHediffDef>
```

Fields:

- `hediffDefNames`: hediff defNames to keep under Hospital treatment.
- `minSeverity`: optional minimum severity before the rule applies.
- `requiredPackageIds`: optional active package gate.

Use this for conditions that should realistically require bed rest or continuing care. Avoid permanent harmless traits, dependencies, cosmetic hediffs, or anything that would trap a visitor forever.

Space Services ships core rules for vanilla/Odyssey diseases, blood loss, mechanites, and a small set of modded medical conditions. `SpaceServices/Examples/HospitalTreatmentHediffCandidates.xml` contains non-loaded candidate examples.

## Shuttle Visuals

Service shuttles are visual placeholders used by the Space Services arrival and departure flow. You can add weighted visuals without replacing the core service shuttle logic.

Use `SpaceServices.SpaceServiceShuttleVisualDef`:

```xml
<SpaceServices.SpaceServiceShuttleVisualDef>
  <defName>MyMod_ServiceShuttleVisual</defName>
  <label>my mod service shuttle</label>
  <weight>1</weight>
  <requiredPackageIds>
    <li>author.mymod</li>
  </requiredPackageIds>
  <serviceKinds>
    <li>hospitality</li>
  </serviceKinds>
  <shipThingDefName>JDB_ServiceShuttlePayload</shipThingDefName>
  <incomingSkyfallerDefName>JDB_ServiceShuttleIncoming</incomingSkyfallerDefName>
  <leavingSkyfallerDefName>JDB_ServiceShuttleLeaving</leavingSkyfallerDefName>
  <graphicData>
    <graphicClass>Graphic_Multi</graphicClass>
    <texPath>Things/Building/MyShuttle/MyShuttle</texPath>
    <shaderType>CutoutComplex</shaderType>
    <drawSize>(3,5)</drawSize>
  </graphicData>
  <angleOffset>0</angleOffset>
</SpaceServices.SpaceServiceShuttleVisualDef>
```

Fields:

- `weight`: weighted random selection value.
- `shipThingDefName`: visual payload thing. Use `JDB_ServiceShuttlePayload` unless you have a specific reason not to.
- `incomingSkyfallerDefName`: incoming skyfaller. Usually `JDB_ServiceShuttleIncoming`.
- `leavingSkyfallerDefName`: leaving skyfaller. Usually `JDB_ServiceShuttleLeaving`.
- `graphicData`: normal RimWorld `GraphicData`.
- `rotation`: optional `Rot4`, defaults to east.
- `angleOffset`: optional draw angle correction. Use this for textures authored 90 degrees off.

You can also attach a `SpaceServices.SpaceServiceShuttleVisualExtension` to an existing shuttle-like `ThingDef`. The fields are the same:

```xml
<ThingDef ParentName="SomeExistingShuttleParent">
  <defName>MyMod_Shuttle</defName>
  <label>my shuttle</label>
  <modExtensions>
    <li Class="SpaceServices.SpaceServiceShuttleVisualExtension">
      <weight>1</weight>
      <serviceKinds>
        <li>hospital</li>
        <li>hospitality</li>
      </serviceKinds>
      <graphicData>
        <graphicClass>Graphic_Multi</graphicClass>
        <texPath>Things/Building/MyShuttle/MyShuttle</texPath>
        <shaderType>CutoutComplex</shaderType>
        <drawSize>(3,5)</drawSize>
      </graphicData>
    </li>
  </modExtensions>
</ThingDef>
```

The in-game `Allow modded shuttle visuals` setting controls optional non-Ludeon visuals. If disabled, Space Services keeps the pool to built-in/vanilla-style visuals.

## Vacuum Apparel Sets

Space Services automatically scans generated apparel with the Odyssey `VacuumResistance` stat and caches practical options. Explicit sets are still useful when your mod has a suit-plus-helmet pairing or when auto discovery cannot satisfy the target safely.

Use `SpaceServices.SpaceServiceVacuumApparelSetDef`:

```xml
<SpaceServices.SpaceServiceVacuumApparelSetDef>
  <defName>MyMod_ServiceVacSuitSet</defName>
  <label>my mod vacsuit set</label>
  <weight>1</weight>
  <requiredPackageIds>
    <li>author.mymod</li>
  </requiredPackageIds>
  <adultApparelDefNames>
    <li>MyMod_Apparel_VacSuit</li>
    <li>MyMod_Apparel_VacHelmet</li>
  </adultApparelDefNames>
  <childApparelDefNames>
    <li>MyMod_Apparel_ChildVacSuit</li>
    <li>MyMod_Apparel_VacHelmet</li>
  </childApparelDefNames>
</SpaceServices.SpaceServiceVacuumApparelSetDef>
```

Notes:

- Space Services targets `100%` practical vacuum resistance for exposed service movement.
- Lower values can still receive vacuum burns in Odyssey.
- Warcasket-style apparel is intentionally blacklisted.
- Space Services only removes or replaces apparel that conflicts with a safety piece's layer/body coverage.
- Sealed no-suit arrivals only remove gear tagged as Space Services-injected.

## Map Eligibility

Map eligibility is currently code-based, not an XML def hook.

Space Services allows stationary space-like maps when at least one stable space signal is present and no hard block is present. Current allow signals include:

- `map.Tile.LayerDef.isSpace`
- a biome with `inVacuum`
- asteroid orbital debris
- generator def names containing `Asteroid`, `Orbit`, `Moon`, `Station`, or `Space`
- parent def `SpaceSettlement`
- parent type/base names containing stationary space terms such as `SpaceMapParent`, `AsteroidMapParent`, `Station`, or `OrbitalBase`

Current hard blocks include:

- actual Odyssey gravship parents
- Quest Editor temporary space submaps
- non-space atmospheric or sky layers such as sky islands, troposphere, stratosphere, or mesosphere

If your map mod is a stationary space map and Space Services does not detect it, prefer exposing one of the stable signals above. If that is not practical, open an issue or compatibility patch request for a dedicated map eligibility hook.

## Architect Icon

Space Services includes an Architect Icons / Better Architect Menu icon at:

```text
SpaceServices/1.6/Textures/UI/ArchitectIcons/JDB_SpaceServices.png
```

Other mods do not need to patch this unless they intentionally move Space Services buildings into a different architect category.

## Non-Goals

- Space Services does not directly integrate Trader Ships.
- Space Services does not directly integrate Rimsential Spaceports.
- Space Services does not make real Odyssey gravships valid service maps.
- Space Services does not suppress event mods globally. It only blocks or delays its own service shuttle traffic.

Future fuel and trade behavior should be handled by a Space Services addon rather than by patching Trader Ships or Spaceports directly.
