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
    public static class HospitalityArrivalContext
    {
        private sealed class Request
        {
            public Map map;
            public Thing pad;
            public bool finalized;
            public bool suppressVisual;
        }

        private static readonly Stack<Request> Requests = new Stack<Request>();

        public static void Push(Map map)
        {
            Push(map, ServicePadUtility.TryFindRandomServicePad(map, ServiceUse.Guest), false);
        }

        public static void Push(Map map, Thing pad, bool suppressVisual)
        {
            Requests.Push(new Request
            {
                map = map,
                pad = pad,
                suppressVisual = suppressVisual
            });
        }

        public static void Pop()
        {
            if (Requests.Count > 0)
            {
                Requests.Pop();
            }
        }

        public static bool TryGetArrivalCell(Map map, out IntVec3 cell)
        {
            foreach (Request request in Requests)
            {
                if (request != null && request.map == map && request.pad != null && !request.pad.Destroyed)
                {
                    cell = request.pad.Position;
                    return true;
                }
            }
            cell = IntVec3.Invalid;
            return false;
        }

        public static bool TryGetArrivalPad(Map map, out Thing pad)
        {
            Request request = CurrentRequest(map);
            pad = request == null ? null : request.pad;
            return pad != null && !pad.Destroyed;
        }

        public static void FinalizeArrival(Map map)
        {
            Request request = CurrentRequest(map);
            if (request == null || request.finalized)
            {
                return;
            }
            request.finalized = true;
            if (request.pad == null || request.pad.Destroyed || request.suppressVisual)
            {
                return;
            }
            ShuttleVisual visual = ShuttleVisual.Resolve();
            if (visual == null)
            {
                return;
            }

            ServiceShuttleUtility.SpawnArrival(map, request.pad.Position);
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp != null)
            {
                comp.ScheduleShuttleArrival(
                    request.pad.Position,
                    visual.shipThingDef == null ? null : visual.shipThingDef.defName,
                    new List<Thing>(),
                    true);
            }
        }

        private static Request CurrentRequest(Map map)
        {
            foreach (Request request in Requests)
            {
                if (request != null && request.map == map && request.pad != null && !request.pad.Destroyed)
                {
                    return request;
                }
            }
            return null;
        }
    }

    public static class HospitalityDelayedIncidentContext
    {
        private sealed class Request
        {
            public Map map;
            public Thing pad;
        }

        private static readonly Stack<Request> Requests = new Stack<Request>();

        public static void Push(Map map, Thing pad)
        {
            Requests.Push(new Request { map = map, pad = pad });
        }

        public static void Pop()
        {
            if (Requests.Count > 0)
            {
                Requests.Pop();
            }
        }

        public static bool TryGetPad(Map map, out Thing pad)
        {
            foreach (Request request in Requests)
            {
                if (request != null && request.map == map && request.pad != null && !request.pad.Destroyed)
                {
                    pad = request.pad;
                    return true;
                }
            }
            pad = null;
            return false;
        }
    }
}
