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
    public static class HospitalityPatches
    {
        public static void Install(Harmony harmony)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospitality)
            {
                return;
            }
            Type[] types =
            {
                AccessTools.TypeByName("Hospitality.Utilities.SpawnGroupUtility"),
                AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroup"),
                AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroupMax"),
                AccessTools.TypeByName("Hospitality.Spacer.IncidentWorker_VisitorGroupSpacer")
            };
            foreach (Type type in types.Where(t => t != null))
            {
                foreach (MethodInfo method in type.GetMethods(AccessTools.all).Where(m => m.DeclaringType == type && (m.Name == "TryDropSpawn" || m.Name == "SpawnGroup" || m.Name == "SpawnPawns" || m.Name == "SpawnVisitor" || m.Name == "GeneratePawns")))
                {
                    OptionalModPatches.PatchIfExists(harmony, method, typeof(OptionalPatchUtility), postfix: nameof(OptionalPatchUtility.SuitPawnsInArgsPostfix));
                }
            }
        }
    }
}
