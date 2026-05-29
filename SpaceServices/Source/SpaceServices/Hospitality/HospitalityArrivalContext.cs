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
        }

        private static readonly Stack<Request> Requests = new Stack<Request>();

        public static void Push(Map map)
        {
            Requests.Push(new Request
            {
                map = map,
                pad = ServicePadUtility.TryFindRandomServicePad(map, ServiceUse.Guest)
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
    }
}
