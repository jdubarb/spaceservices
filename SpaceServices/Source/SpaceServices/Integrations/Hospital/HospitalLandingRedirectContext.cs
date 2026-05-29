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
    public static class HospitalLandingRedirectContext
    {
        private struct Request
        {
            public Map map;
            public IntVec3 cell;
            public bool forced;
            public Thing tempLandingSpot;
        }

        private static readonly Stack<Request> Requests = new Stack<Request>();

        public static void Push(Map map, IntVec3 cell, Thing tempLandingSpot)
        {
            Requests.Push(new Request { map = map, cell = cell, forced = false, tempLandingSpot = tempLandingSpot });
        }

        public static void PushForced(Map map, IntVec3 cell)
        {
            Requests.Push(new Request { map = map, cell = cell, forced = true });
        }

        public static void Pop()
        {
            if (Requests.Count > 0)
            {
                Request request = Requests.Pop();
                if (request.tempLandingSpot != null && !request.tempLandingSpot.Destroyed)
                {
                    request.tempLandingSpot.Destroy(DestroyMode.Vanish);
                }
            }
        }

        public static Thing CreateTemporaryPatientLandingSpot(Map map, IntVec3 preferredCell)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("PatientLandingSpot");
            if (map == null || def == null || map.listerBuildings.AllBuildingsColonistOfDef(def).Any())
            {
                return null;
            }

            IntVec3 cell = TemporaryLandingSpotCell(map, preferredCell);
            if (!cell.IsValid)
            {
                return null;
            }

            Thing thing = ThingMaker.MakeThing(def);
            thing.SetFactionDirect(Faction.OfPlayer);
            // Hospital mass casualty code assumes at least one PatientLandingSpot exists before our drop redirect runs.
            return GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
        }

        private static IntVec3 TemporaryLandingSpotCell(Map map, IntVec3 preferredCell)
        {
            IEnumerable<IntVec3> cells = preferredCell.IsValid
                ? GenRadial.RadialCellsAround(preferredCell, 8f, true)
                : GenRadial.RadialCellsAround(map.Center, 12f, true);
            foreach (IntVec3 cell in cells)
            {
                if (cell.InBounds(map) && cell.Standable(map) && cell.GetEdifice(map) == null)
                {
                    return cell;
                }
            }
            return IntVec3.Invalid;
        }

        public static bool TryGetActiveCell(Map map, out IntVec3 cell)
        {
            foreach (Request request in Requests)
            {
                if (request.map == map && request.cell.IsValid)
                {
                    cell = request.cell;
                    return true;
                }
            }
            cell = IntVec3.Invalid;
            return false;
        }

        public static bool TryGetForcedCell(Map map, out IntVec3 cell)
        {
            foreach (Request request in Requests)
            {
                if (request.map == map && request.forced && request.cell.IsValid)
                {
                    cell = request.cell;
                    return true;
                }
            }
            cell = IntVec3.Invalid;
            return false;
        }
    }

}
