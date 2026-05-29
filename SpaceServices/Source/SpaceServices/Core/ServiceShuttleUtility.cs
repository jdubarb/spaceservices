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
            SpawnSkyfaller(map, cell, ShuttleVisual.Resolve(), true);
        }

        public static void SpawnDeparture(Map map, IntVec3 cell)
        {
            SpawnSkyfaller(map, cell, ShuttleVisual.Resolve(), false);
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
            ShuttleVisual visual = ShuttleVisual.Resolve();
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
            comp.ScheduleShuttleArrival(cell, visual == null || visual.shipThingDef == null ? null : visual.shipThingDef.defName, things, showDeparture);
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
                    SpawnDeparture(map, arrival.cell);
                }
                arrivals.RemoveAt(i);
            }
        }

        private static void SpawnContents(Map map, IntVec3 cell, List<Thing> things)
        {
            int index = 0;
            foreach (Thing thing in things ?? Enumerable.Empty<Thing>())
            {
                if (thing == null || thing.Destroyed)
                {
                    continue;
                }
                IntVec3 spawnCell = FindSpawnCell(cell, map, index++);
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
            SkyfallerMaker.SpawnSkyfaller(skyfallerDef, innerThing, cell, map);
        }

        public static void CleanupTouchdownShuttle(Map map, IntVec3 cell, string shuttleThingDefName)
        {
            ThingDef shuttleDef = DefDatabase<ThingDef>.GetNamedSilentFail(shuttleThingDefName);
            if (map == null || shuttleDef == null || !cell.IsValid)
            {
                return;
            }
            foreach (Thing thing in cell.GetThingList(map).ToList())
            {
                if (thing != null && thing.def == shuttleDef)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
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
        public ThingDef shipThingDef;
        public ThingDef incomingSkyfallerDef;
        public ThingDef leavingSkyfallerDef;

        public static ShuttleVisual Resolve()
        {
            ThingDef payload = DefDatabase<ThingDef>.GetNamedSilentFail("MLT_ServiceShuttlePayload");
            if (payload == null)
            {
                return null;
            }

            ThingDef incoming = DefDatabase<ThingDef>.GetNamedSilentFail("MLT_ServiceShuttleIncoming");
            ThingDef leaving = DefDatabase<ThingDef>.GetNamedSilentFail("MLT_ServiceShuttleLeaving");
            if (incoming == null || leaving == null)
            {
                return null;
            }
            return new ShuttleVisual
            {
                shipThingDef = payload,
                incomingSkyfallerDef = incoming,
                leavingSkyfallerDef = leaving
            };
        }
    }
}
