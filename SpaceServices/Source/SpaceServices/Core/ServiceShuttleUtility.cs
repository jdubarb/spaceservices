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

        public static void SpawnDeparture(Map map, IntVec3 cell)
        {
            SpawnDeparture(map, cell, null, null);
        }

        public static void SpawnDeparture(Map map, IntVec3 cell, string serviceKind, string visualDefName)
        {
            ServiceDebugUtility.LogAudit("SpawnDepartureShuttle cell=" + cell + " map=" + (map == null ? "null" : map.Index.ToString()));
            SpawnSkyfaller(map, cell, ShuttleVisual.Resolve(serviceKind, visualDefName), false);
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
            // Graphic_Multi payloads are made off-map, so set the service pad facing explicitly.
            innerThing.Rotation = visual.rotation;
            SkyfallerMaker.SpawnSkyfaller(skyfallerDef, innerThing, cell, map);
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

        public static ShuttleVisual Resolve()
        {
            return Resolve(null, null);
        }

        public static ShuttleVisual Resolve(string serviceKind, string visualDefName)
        {
            List<ShuttleVisual> visuals = AvailableVisuals(serviceKind).ToList();
            if (!string.IsNullOrEmpty(visualDefName))
            {
                ShuttleVisual exact = visuals.FirstOrDefault(visual => string.Equals(visual.id, visualDefName, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
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

        private static IEnumerable<ShuttleVisual> AvailableVisuals(string serviceKind)
        {
            foreach (SpaceServiceShuttleVisualDef def in DefDatabase<SpaceServiceShuttleVisualDef>.AllDefsListForReading)
            {
                if (def != null && def.AppliesTo(serviceKind))
                {
                    ShuttleVisual visual = FromNames(def.defName, def.weight, def.shipThingDefName, def.incomingSkyfallerDefName, def.leavingSkyfallerDefName, def.rotation);
                    if (visual != null)
                    {
                        yield return visual;
                    }
                }
            }

            foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                SpaceServiceShuttleVisualExtension extension = thingDef.GetModExtension<SpaceServiceShuttleVisualExtension>();
                if (extension == null || !extension.AppliesTo(serviceKind))
                {
                    continue;
                }
                string shipDefName = string.IsNullOrEmpty(extension.shipThingDefName) ? thingDef.defName : extension.shipThingDefName;
                ShuttleVisual visual = FromNames(thingDef.defName, extension.weight, shipDefName, extension.incomingSkyfallerDefName, extension.leavingSkyfallerDefName, extension.rotation);
                if (visual != null)
                {
                    yield return visual;
                }
            }
        }

        private static ShuttleVisual FromNames(string id, float weight, string shipThingDefName, string incomingSkyfallerDefName, string leavingSkyfallerDefName, Rot4 rotation)
        {
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
                weight = Math.Max(0.001f, weight),
                rotation = rotation,
                shipThingDef = payload,
                incomingSkyfallerDef = incoming,
                leavingSkyfallerDef = leaving
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
                Rot4.East);
        }
    }
}
