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
    public static class ServicePadUtility
    {
        public static IEnumerable<Thing> AllServicePads(Map map, ServiceUse use)
        {
            if (map == null)
            {
                yield break;
            }
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                CompSpaceServicePad comp = building.TryGetComp<CompSpaceServicePad>();
                if (comp != null && comp.IsUsableFor(use))
                {
                    yield return building;
                }
            }
        }

        public static bool TryFindServicePadCell(Map map, ServiceUse use, out IntVec3 cell)
        {
            Thing pad = TryFindRandomServicePad(map, use);
            if (pad != null)
            {
                cell = pad.Position;
                return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }

        public static Thing TryFindServicePad(Map map, ServiceUse use)
        {
            return AllServicePads(map, use).FirstOrDefault();
        }

        public static Thing TryFindRandomServicePad(Map map, ServiceUse use)
        {
            List<Thing> pads = AllServicePads(map, use).ToList();
            if (pads.Count == 0)
            {
                return null;
            }
            return pads[Rand.Range(0, pads.Count)];
        }

        public static Thing TryReserveServicePad(Map map, ServiceUse use, string groupId)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                return null;
            }
            foreach (Thing pad in AllServicePads(map, use))
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp != null && comp.TryReserve(groupId))
                {
                    return pad;
                }
            }
            return null;
        }

        public static int CountServicePads(Map map, ServiceUse use)
        {
            return AllServicePads(map, use).Count();
        }
    }

}
