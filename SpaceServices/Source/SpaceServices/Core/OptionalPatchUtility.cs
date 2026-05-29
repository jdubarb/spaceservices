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
    public static class OptionalPatchUtility
    {
        public static void SuitPawnsInArgsPostfix(MethodBase __originalMethod, object[] __args)
        {
            Map map = FindMap(__args);
            if (map != null && !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            List<Pawn> pawns = PawnsFromArgs(__args).Distinct().ToList();
            foreach (Pawn pawn in pawns)
            {
                IntVec3 cell = pawn != null && pawn.Spawned ? pawn.Position : IntVec3.Invalid;
                VacSuitUtility.SuitPawnForEnvironment(pawn, map, cell);
            }
            string methodName = __originalMethod == null || __originalMethod.DeclaringType == null ? "" : __originalMethod.DeclaringType.FullName ?? "";
            if (map != null && pawns.Count > 0)
            {
                if (methodName.IndexOf("Hospital.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ServiceLifecycleUtility.RegisterPawns(map, "hospital", pawns);
                }
                else if (methodName.IndexOf("Hospitality.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ServiceLifecycleUtility.RegisterPawns(map, "hospitality", pawns);
                }
            }
        }

        public static IEnumerable<Pawn> PawnsFromArgs(object[] args)
        {
            foreach (object arg in args ?? new object[0])
            {
                Pawn pawn = arg as Pawn;
                if (pawn != null)
                {
                    yield return pawn;
                    continue;
                }
                IEnumerable<Pawn> pawns = arg as IEnumerable<Pawn>;
                if (pawns != null)
                {
                    foreach (Pawn p in pawns)
                    {
                        yield return p;
                    }
                }
            }
        }

        public static Map FindMap(object[] args)
        {
            foreach (object arg in args ?? new object[0])
            {
                Map map = arg as Map;
                if (map != null)
                {
                    return map;
                }
                IncidentParms parms = arg as IncidentParms;
                if (parms != null)
                {
                    Map targetMap = parms.target as Map;
                    if (targetMap != null)
                    {
                        return targetMap;
                    }
                }
                Pawn pawn = arg as Pawn;
                if (pawn != null && pawn.MapHeld != null)
                {
                    return pawn.MapHeld;
                }
            }
            return Find.CurrentMap;
        }
    }
}
