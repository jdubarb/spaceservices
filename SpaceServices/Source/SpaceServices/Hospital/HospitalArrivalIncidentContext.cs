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
    public static class HospitalArrivalIncidentContext
    {
        private sealed class Request
        {
            public Map map;
            public List<IntVec3> cells = new List<IntVec3>();
            public int nextIndex;
            public bool massCasualty;
            public bool arrivalVisualUsed;
        }

        private static readonly Stack<Request> Requests = new Stack<Request>();
        private static readonly HashSet<int> RateDeniedPatientFallbacks = new HashSet<int>();

        public static void Push(Map map, bool massCasualty)
        {
            Request request = new Request { map = map, massCasualty = massCasualty };
            if (map != null && massCasualty)
            {
                request.cells = BuildArrivalCells(map);
                if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging)
                {
                    ServiceDebugUtility.LogVerbose(ServiceLogIntegration.Hospital, "Mass casualty arrival cells prepared=" + request.cells.Count);
                }
            }
            Requests.Push(request);
        }

        public static void Pop()
        {
            if (Requests.Count > 0)
            {
                Requests.Pop();
            }
        }

        public static void Pop(Map map)
        {
            if (Requests.Count > 0 && Requests.Peek().map == map)
            {
                Requests.Pop();
            }
        }

        public static void MarkPatientFallbackSuppressed(Map map)
        {
            if (map != null)
            {
                RateDeniedPatientFallbacks.Add(map.uniqueID);
            }
        }

        public static bool ConsumePatientFallbackSuppressed(Map map)
        {
            return map != null && RateDeniedPatientFallbacks.Remove(map.uniqueID);
        }

        public static bool TryGetNextArrivalCell(Map map, out IntVec3 cell)
        {
            foreach (Request request in Requests)
            {
                if (request.map != map || request.cells == null || request.cells.Count == 0)
                {
                    continue;
                }
                cell = request.cells[request.nextIndex % request.cells.Count];
                request.nextIndex++;
                return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }

        public static void ArrivalVisualFlags(Map map, out bool showArrival, out bool showDeparture)
        {
            showArrival = true;
            showDeparture = true;
            foreach (Request request in Requests)
            {
                if (request.map != map || !request.massCasualty)
                {
                    continue;
                }
                // Mass casualty patients are still separate Hospital drops; only the first one gets the shuttle visual.
                if (request.arrivalVisualUsed)
                {
                    showArrival = false;
                    showDeparture = false;
                }
                else
                {
                    request.arrivalVisualUsed = true;
                }
                return;
            }
        }

        public static bool IsMassCasualty(Map map)
        {
            foreach (Request request in Requests)
            {
                if (request.map == map && request.massCasualty)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<IntVec3> BuildArrivalCells(Map map)
        {
            Thing pad = ServicePadUtility.TryFindRandomServicePad(map, ServiceUse.Patient);
            if (pad == null)
            {
                return new List<IntVec3>();
            }

            IntVec3 center = pad.Position;
            List<IntVec3> cells = pad.OccupiedRect().Cells
                .Where(cell => cell.InBounds(map) && cell.Standable(map))
                .OrderBy(cell => cell.DistanceToSquared(center))
                .ToList();

            if (cells.Count == 0)
            {
                cells.Add(center);
            }
            return cells;
        }
    }
}
