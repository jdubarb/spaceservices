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
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Discard), new[] { typeof(bool) })]
    public static class PawnDiscardCleanupPatch
    {
        public static void Prefix(Pawn __instance)
        {
            int cleaned = ServicePawnUtility.CleanupInvalidDirectRelations(__instance);
            if (cleaned > 0)
            {
                ServiceDebugUtility.LogAudit("Cleaned invalid direct relations before pawn discard: pawn=" + ServiceDebugUtility.PawnAuditSummary(__instance) + " count=" + cleaned);
            }
        }
    }
}
