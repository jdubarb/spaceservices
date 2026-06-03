using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    public static class ServiceShuttleUtility
    {
        public const int ArrivalTouchdownDelayTicks = 260;

        public static void SpawnArrival(Map map, IntVec3 cell)
        {
            SpawnArrival(map, cell, null, null);
        }

        public static void SpawnArrival(Map map, IntVec3 cell, string serviceKind, string visualDefName)
        {
            ServiceDebugUtility.LogAudit("SpawnArrivalShuttle cell=" + cell + " map=" + (map == null ? "null" : map.Index.ToString()));
            SpawnSkyfaller(map, cell, ShuttleVisual.Resolve(serviceKind, visualDefName), true);
        }

        public static void SpawnArrival(Map map, IntVec3 cell, ShuttleVisual visual)
        {
            ServiceDebugUtility.LogAudit("SpawnArrivalShuttle cell=" + cell + " map=" + (map == null ? "null" : map.Index.ToString()));
            SpawnSkyfaller(map, cell, visual, true);
        }

        public static void SpawnDeparture(Map map, IntVec3 cell)
        {
            SpawnDeparture(map, cell, null, null);
        }

        public static void SpawnDeparture(Map map, IntVec3 cell, string serviceKind, string visualDefName)
        {
            ServiceDebugUtility.LogAudit("SpawnDepartureShuttle cell=" + cell + " map=" + (map == null ? "null" : map.Index.ToString()));
            SpawnSkyfaller(map, cell, ShuttleVisual.Resolve(serviceKind, visualDefName), false);
        }

        public static void SpawnDeparture(Map map, IntVec3 cell, ShuttleVisual visual)
        {
            ServiceDebugUtility.LogAudit("SpawnDepartureShuttle cell=" + cell + " map=" + (map == null ? "null" : map.Index.ToString()));
            SpawnSkyfaller(map, cell, visual, false);
        }

        public static bool TryReplaceDropPodWithArrivalShuttle(IntVec3 cell, Map map, ActiveTransporterInfo info, Faction faction, bool showArrival, bool showDeparture)
        {
            if (map == null || info == null || info.innerContainer == null || !cell.IsValid)
            {
                return false;
            }

            List<Thing> things = info.innerContainer.ToList();
            if (things.Count == 0)
            {
                return false;
            }

            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return false;
            }
            ShuttleVisual visual = ShuttleVisual.Resolve("hospital", null);
            if (showArrival && visual == null)
            {
                return false;
            }
            foreach (Thing thing in things)
            {
                if (thing == null)
                {
                    continue;
                }
                info.innerContainer.Remove(thing);
            }
            if (showArrival)
            {
                SpawnSkyfaller(map, cell, visual, true);
            }
            comp.ScheduleShuttleArrival(cell, visual == null || visual.shipThingDef == null ? null : visual.shipThingDef.defName, visual == null ? null : visual.id, things, showDeparture);
            return true;
        }

        public static void TickPendingArrivals(Map map, List<ScheduledServiceShuttleArrival> arrivals)
        {
            if (map == null || arrivals == null || arrivals.Count == 0)
            {
                return;
            }
            for (int i = arrivals.Count - 1; i >= 0; i--)
            {
                ScheduledServiceShuttleArrival arrival = arrivals[i];
                if (arrival == null || Find.TickManager.TicksGame < arrival.touchdownTick)
                {
                    continue;
                }
                if (arrival.showDeparture)
                {
                    CleanupTouchdownShuttle(map, arrival.cell, arrival.shuttleThingDefName);
                }
                SpawnContents(map, arrival.cell, arrival.things);
                if (arrival.showDeparture)
                {
                    SpawnDeparture(map, arrival.cell, null, arrival.shuttleVisualDefName);
                }
                arrivals.RemoveAt(i);
            }
        }

        private static void SpawnContents(Map map, IntVec3 cell, ThingOwner<Thing> things)
        {
            int index = 0;
            foreach (Thing thing in (things == null ? Enumerable.Empty<Thing>() : things.ToList()))
            {
                if (thing == null || thing.Destroyed)
                {
                    continue;
                }
                IntVec3 spawnCell = FindSpawnCell(cell, map, index++);
                things.Remove(thing);
                GenSpawn.Spawn(thing, spawnCell, map);
            }
        }

        private static void SpawnSkyfaller(Map map, IntVec3 cell, ShuttleVisual visual, bool incoming)
        {
            if (map == null || !cell.IsValid || visual == null)
            {
                return;
            }

            ThingDef skyfallerDef = incoming ? visual.incomingSkyfallerDef : visual.leavingSkyfallerDef;
            ThingDef shipThingDef = visual.shipThingDef;
            if (skyfallerDef == null || shipThingDef == null)
            {
                return;
            }
            Thing innerThing = ThingMaker.MakeThing(shipThingDef);
            ServiceShuttlePayload payload = innerThing as ServiceShuttlePayload;
            if (payload != null)
            {
                payload.visualDefName = visual.id;
            }
            // Graphic_Multi payloads are made off-map, so set the service pad facing explicitly.
            innerThing.Rotation = visual.rotation;
            Skyfaller skyfaller = SkyfallerMaker.SpawnSkyfaller(skyfallerDef, innerThing, cell, map);
            ServiceShuttleSkyfaller serviceSkyfaller = skyfaller as ServiceShuttleSkyfaller;
            if (serviceSkyfaller != null)
            {
                serviceSkyfaller.visualDefName = visual.id;
            }
        }

        public static void CleanupTouchdownShuttle(Map map, IntVec3 cell, string shuttleThingDefName)
        {
            if (map == null || !cell.IsValid || string.IsNullOrEmpty(shuttleThingDefName))
            {
                return;
            }
            ThingDef shuttleDef = DefDatabase<ThingDef>.GetNamedSilentFail(shuttleThingDefName);
            if (shuttleDef == null)
            {
                return;
            }
            int destroyed = CleanupShuttleThingsNear(map, shuttleDef, cell, 8f);
            ServiceDebugUtility.LogAudit("CleanupTouchdownShuttle cell=" + cell + " map=" + map.Index + " def=" + shuttleThingDefName + " destroyed=" + destroyed);
        }

        public static int CleanupServiceShuttlePayloadsNear(Map map, IntVec3 cell, float radius)
        {
            ThingDef shuttleDef = DefDatabase<ThingDef>.GetNamedSilentFail("JDB_ServiceShuttlePayload");
            if (map == null || shuttleDef == null || !cell.IsValid)
            {
                return 0;
            }
            return CleanupShuttleThingsNear(map, shuttleDef, cell, radius);
        }

        public static int CleanupServiceShuttlesNear(Map map, IntVec3 cell, float radius)
        {
            if (map == null || !cell.IsValid)
            {
                return 0;
            }
            return CleanupMatchingServiceShuttles(map, thing => thing.Position.InHorDistOf(cell, radius));
        }

        public static int CleanupAllServiceShuttles(Map map)
        {
            if (map == null)
            {
                return 0;
            }
            return CleanupMatchingServiceShuttles(map, thing => true);
        }

        private static int CleanupMatchingServiceShuttles(Map map, Func<Thing, bool> predicate)
        {
            HashSet<ThingDef> shipDefs = ShuttleVisual.KnownShipThingDefs();
            HashSet<ThingDef> skyfallerDefs = ShuttleVisual.KnownSkyfallerThingDefs();
            List<Thing> things = map.listerThings.AllThings
                .Where(thing => thing != null &&
                    !thing.Destroyed &&
                    predicate(thing) &&
                    (shipDefs.Contains(thing.def) || skyfallerDefs.Contains(thing.def)))
                .ToList();
            foreach (Thing thing in things)
            {
                thing.Destroy(DestroyMode.Vanish);
            }
            return things.Count;
        }

        private static int CleanupShuttleThingsNear(Map map, ThingDef shuttleDef, IntVec3 cell, float radius)
        {
            if (map == null || shuttleDef == null || !cell.IsValid)
            {
                return 0;
            }
            List<Thing> things = map.listerThings.ThingsOfDef(shuttleDef)
                .Where(thing => thing != null && !thing.Destroyed && thing.Position.InHorDistOf(cell, radius))
                .ToList();
            foreach (Thing thing in things)
            {
                thing.Destroy(DestroyMode.Vanish);
            }
            return things.Count;
        }

        private static IntVec3 FindSpawnCell(IntVec3 center, Map map, int index)
        {
            List<IntVec3> cells = GenRadial.RadialCellsAround(center, 3f, true)
                .Where(cell => cell.InBounds(map) && cell.Standable(map) && cell.GetFirstPawn(map) == null)
                .OrderBy(cell => cell.DistanceToSquared(center))
                .ToList();
            if (cells.Count == 0)
            {
                return center;
            }
            return cells[index % cells.Count];
        }
    }

    public sealed class ShuttleVisual
    {
        public string id;
        public float weight = 1f;
        public Rot4 rotation = Rot4.East;
        public ThingDef shipThingDef;
        public ThingDef incomingSkyfallerDef;
        public ThingDef leavingSkyfallerDef;
        public GraphicData graphicData;
        private static readonly Dictionary<string, List<ShuttleVisual>> VisualsByKind = new Dictionary<string, List<ShuttleVisual>>(StringComparer.OrdinalIgnoreCase);

        public static ShuttleVisual Resolve()
        {
            return Resolve(null, null);
        }

        public static ShuttleVisual Resolve(string serviceKind, string visualDefName)
        {
            List<ShuttleVisual> visuals = AvailableVisuals(serviceKind);
            if (!string.IsNullOrEmpty(visualDefName))
            {
                ShuttleVisual exact = visuals.FirstOrDefault(visual => string.Equals(visual.id, visualDefName, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
                ServiceDebugUtility.LogThrottled("missing-shuttle-visual-" + visualDefName, "Saved shuttle visual " + visualDefName + " is no longer available; using deterministic fallback.", GenDate.TicksPerDay);
                ShuttleVisual fallback = FallbackVisual();
                if (fallback != null)
                {
                    return fallback;
                }
            }
            if (visuals.Count == 0)
            {
                return FallbackVisual();
            }
            float totalWeight = visuals.Sum(visual => Math.Max(0.001f, visual.weight));
            float roll = Rand.Value * totalWeight;
            foreach (ShuttleVisual visual in visuals)
            {
                roll -= Math.Max(0.001f, visual.weight);
                if (roll <= 0f)
                {
                    return visual;
                }
            }
            return visuals[visuals.Count - 1];
        }

        public static void ClearCache()
        {
            VisualsByKind.Clear();
        }

        public static HashSet<ThingDef> KnownShipThingDefs()
        {
            HashSet<ThingDef> defs = new HashSet<ThingDef>();
            AddKnownVisualDefs(defs, null, skyfallers: false);
            AddKnownVisualDefs(defs, "hospital", skyfallers: false);
            AddKnownVisualDefs(defs, "hospitality", skyfallers: false);
            return defs;
        }

        public static HashSet<ThingDef> KnownSkyfallerThingDefs()
        {
            HashSet<ThingDef> defs = new HashSet<ThingDef>();
            AddKnownVisualDefs(defs, null, skyfallers: true);
            AddKnownVisualDefs(defs, "hospital", skyfallers: true);
            AddKnownVisualDefs(defs, "hospitality", skyfallers: true);
            return defs;
        }

        private static void AddKnownVisualDefs(HashSet<ThingDef> defs, string serviceKind, bool skyfallers)
        {
            List<ShuttleVisual> visuals = AvailableVisuals(serviceKind).ToList();
            ShuttleVisual fallback = FallbackVisual();
            if (fallback != null)
            {
                visuals.Add(fallback);
            }
            foreach (ShuttleVisual visual in visuals)
            {
                if (visual == null)
                {
                    continue;
                }
                if (skyfallers)
                {
                    if (visual.incomingSkyfallerDef != null)
                    {
                        defs.Add(visual.incomingSkyfallerDef);
                    }
                    if (visual.leavingSkyfallerDef != null)
                    {
                        defs.Add(visual.leavingSkyfallerDef);
                    }
                }
                else if (visual.shipThingDef != null)
                {
                    defs.Add(visual.shipThingDef);
                }
            }
        }

        private static List<ShuttleVisual> AvailableVisuals(string serviceKind)
        {
            string key = (serviceKind ?? "").Trim();
            List<ShuttleVisual> cached;
            if (VisualsByKind.TryGetValue(key, out cached))
            {
                return cached;
            }
            cached = BuildAvailableVisuals(serviceKind);
            VisualsByKind[key] = cached;
            return cached;
        }

        private static List<ShuttleVisual> BuildAvailableVisuals(string serviceKind)
        {
            List<ShuttleVisual> visuals = new List<ShuttleVisual>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SpaceServiceShuttleVisualDef def in DefDatabase<SpaceServiceShuttleVisualDef>.AllDefsListForReading)
            {
                if (def != null && VisualSourceAllowed(def.requiredPackageIds) && def.AppliesTo(serviceKind))
                {
                    ShuttleVisual visual = FromNames(def.defName, def.weight, def.shipThingDefName, def.incomingSkyfallerDefName, def.leavingSkyfallerDefName, def.rotation, def.graphicData, def.angleOffset);
                    if (visual != null && TryReserveVisualId(seen, visual.id, "def"))
                    {
                        visuals.Add(visual);
                    }
                }
            }

            foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                SpaceServiceShuttleVisualExtension extension = thingDef.GetModExtension<SpaceServiceShuttleVisualExtension>();
                if (extension == null || !VisualSourceAllowed(extension.requiredPackageIds) || !extension.AppliesTo(serviceKind))
                {
                    continue;
                }
                string shipDefName = !string.IsNullOrEmpty(extension.shipThingDefName) ? extension.shipThingDefName :
                    extension.graphicData == null ? thingDef.defName : "JDB_ServiceShuttlePayload";
                ShuttleVisual visual = FromNames(thingDef.defName, extension.weight, shipDefName, extension.incomingSkyfallerDefName, extension.leavingSkyfallerDefName, extension.rotation, extension.graphicData, extension.angleOffset);
                if (visual != null && TryReserveVisualId(seen, visual.id, "extension"))
                {
                    visuals.Add(visual);
                }
            }
            return visuals;
        }

        private static bool VisualSourceAllowed(List<string> requiredPackageIds)
        {
            if (SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.allowModdedShuttleVisuals || requiredPackageIds.NullOrEmpty())
            {
                return true;
            }
            return requiredPackageIds.All(IsLudeonPackage);
        }

        private static bool IsLudeonPackage(string packageId)
        {
            return !string.IsNullOrEmpty(packageId) &&
                packageId.StartsWith("Ludeon.RimWorld", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReserveVisualId(HashSet<string> seen, string id, string source)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            if (seen.Add(id))
            {
                return true;
            }
            ServiceDebugUtility.LogThrottled("duplicate-shuttle-visual-" + id, "Duplicate shuttle visual id " + id + " from " + source + " ignored; first loaded visual wins.", GenDate.TicksPerDay);
            return false;
        }

        private static ShuttleVisual FromNames(string id, float weight, string shipThingDefName, string incomingSkyfallerDefName, string leavingSkyfallerDefName, Rot4 rotation, GraphicData graphicData, float angleOffset)
        {
            if (weight <= 0f)
            {
                return null;
            }
            if (string.IsNullOrEmpty(shipThingDefName))
            {
                shipThingDefName = "JDB_ServiceShuttlePayload";
            }
            if (string.IsNullOrEmpty(incomingSkyfallerDefName))
            {
                incomingSkyfallerDefName = "JDB_ServiceShuttleIncoming";
            }
            if (string.IsNullOrEmpty(leavingSkyfallerDefName))
            {
                leavingSkyfallerDefName = "JDB_ServiceShuttleLeaving";
            }
            ThingDef payload = DefDatabase<ThingDef>.GetNamedSilentFail(shipThingDefName);
            if (payload == null)
            {
                return null;
            }

            ThingDef incoming = DefDatabase<ThingDef>.GetNamedSilentFail(incomingSkyfallerDefName);
            ThingDef leaving = DefDatabase<ThingDef>.GetNamedSilentFail(leavingSkyfallerDefName);
            if (incoming == null || leaving == null)
            {
                return null;
            }
            return new ShuttleVisual
            {
                id = id,
                weight = weight,
                rotation = rotation,
                shipThingDef = payload,
                incomingSkyfallerDef = incoming,
                leavingSkyfallerDef = leaving,
                graphicData = graphicData,
                angleOffset = angleOffset
            };
        }

        private static ShuttleVisual FallbackVisual()
        {
            return FromNames(
                "JDB_ServicePassengerShuttleVisual",
                1f,
                "JDB_ServiceShuttlePayload",
                "JDB_ServiceShuttleIncoming",
                "JDB_ServiceShuttleLeaving",
                Rot4.East,
                null,
                0f);
        }

        public float angleOffset;
    }

    public sealed class ServiceShuttlePayload : Thing
    {
        public string visualDefName;

        public override Graphic Graphic
        {
            get
            {
                Graphic graphic = ServiceShuttleGraphicUtility.GraphicFor(visualDefName);
                return graphic ?? base.Graphic;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref visualDefName, "visualDefName");
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            float angleOffset = ServiceShuttleGraphicUtility.AngleOffsetFor(visualDefName);
            if (Mathf.Abs(angleOffset) < 0.001f)
            {
                base.DrawAt(drawLoc, flip);
                return;
            }
            Graphic graphic = Graphic;
            if (graphic == null)
            {
                base.DrawAt(drawLoc, flip);
                return;
            }
            graphic.Draw(drawLoc, Rotation, this, angleOffset);
        }
    }

    public sealed class ServiceShuttleSkyfaller : Skyfaller
    {
        public string visualDefName;
        private Material cachedServiceShadowMaterial;

        public override Graphic Graphic
        {
            get
            {
                Graphic graphic = ServiceShuttleGraphicUtility.GraphicFor(visualDefName);
                if (graphic == null && innerContainer.Any && innerContainer[0] is ServiceShuttlePayload payload)
                {
                    graphic = ServiceShuttleGraphicUtility.GraphicFor(payload.visualDefName);
                }
                return graphic ?? base.Graphic;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref visualDefName, "visualDefName");
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            float angleOffset = ServiceShuttleGraphicUtility.AngleOffsetFor(visualDefName);
            if (Mathf.Abs(angleOffset) < 0.001f)
            {
                base.DrawAt(drawLoc, flip);
                return;
            }

            // The base Skyfaller draw path cannot add a per-visual texture rotation, so this mirrors
            // the vanilla curve handling and changes only the final graphic angle.
            float pos = Traverse.Create(this).Property<float>("TimeInAnimation").Value;
            float drawAngle = 0f;
            if (def.skyfaller.rotateGraphicTowardsDirection)
            {
                drawAngle = angle;
            }
            if (def.skyfaller.angleCurve != null)
            {
                angle = def.skyfaller.angleCurve.Evaluate(pos);
            }
            if (def.skyfaller.rotationCurve != null)
            {
                drawAngle += def.skyfaller.rotationCurve.Evaluate(pos);
            }
            if (def.skyfaller.xPositionCurve != null)
            {
                drawLoc.x += def.skyfaller.xPositionCurve.Evaluate(pos);
            }
            if (def.skyfaller.zPositionCurve != null)
            {
                drawLoc.z += def.skyfaller.zPositionCurve.Evaluate(pos);
            }

            Graphic graphic = Graphic;
            if (graphic != null)
            {
                graphic.Draw(drawLoc, Rotation, this, drawAngle + angleOffset);
            }
            Material shadow = ServiceShadowMaterial;
            if (shadow != null)
            {
                drawLoc.z = GenThing.TrueCenter(this).z;
                DrawDropSpotShadow(drawLoc, Rotation, shadow, def.skyfaller.shadowSize, ticksToImpact);
            }
        }

        private Material ServiceShadowMaterial
        {
            get
            {
                if (cachedServiceShadowMaterial == null && !def.skyfaller.shadow.NullOrEmpty())
                {
                    cachedServiceShadowMaterial = MaterialPool.MatFrom(def.skyfaller.shadow, ShaderDatabase.Transparent);
                }
                return cachedServiceShadowMaterial;
            }
        }
    }

    public static class ServiceShuttleGraphicUtility
    {
        public static Graphic GraphicFor(string visualDefName)
        {
            if (string.IsNullOrEmpty(visualDefName))
            {
                return null;
            }
            SpaceServiceShuttleVisualDef def = DefDatabase<SpaceServiceShuttleVisualDef>.GetNamedSilentFail(visualDefName);
            GraphicData graphicData = def == null ? null : def.graphicData;
            if (graphicData == null)
            {
                ThingDef thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(visualDefName);
                SpaceServiceShuttleVisualExtension extension = thingDef == null ? null : thingDef.GetModExtension<SpaceServiceShuttleVisualExtension>();
                graphicData = extension == null ? null : extension.graphicData;
            }
            if (graphicData == null)
            {
                return null;
            }
            try
            {
                return graphicData.Graphic;
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogThrottled("bad-shuttle-graphic-" + visualDefName, "Failed to resolve shuttle visual graphic " + visualDefName + ": " + ex.Message, GenDate.TicksPerDay);
                return null;
            }
        }

        public static float AngleOffsetFor(string visualDefName)
        {
            if (string.IsNullOrEmpty(visualDefName))
            {
                return 0f;
            }
            SpaceServiceShuttleVisualDef def = DefDatabase<SpaceServiceShuttleVisualDef>.GetNamedSilentFail(visualDefName);
            if (def != null)
            {
                return def.angleOffset;
            }
            ThingDef thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(visualDefName);
            SpaceServiceShuttleVisualExtension extension = thingDef == null ? null : thingDef.GetModExtension<SpaceServiceShuttleVisualExtension>();
            return extension == null ? 0f : extension.angleOffset;
        }
    }
}
