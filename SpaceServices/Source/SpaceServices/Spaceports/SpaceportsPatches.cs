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
    public static class SpaceportsPatches
    {
        public static void Install(Harmony harmony)
        {
            Type utils = AccessTools.TypeByName("Spaceports.Utils");
            if (utils == null)
            {
                return;
            }
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(utils, "IsMapInSpace"), typeof(SpaceportsPatchHandlers), postfix: nameof(SpaceportsPatchHandlers.SpaceportsIsMapInSpacePostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(utils, "SuitUpPawns"), typeof(SpaceportsPatchHandlers), prefix: nameof(SpaceportsPatchHandlers.SpaceportsSuitUpPawnsPrefix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(utils, "HospitalityShuttleCheck"), typeof(SpaceportsPatchHandlers), postfix: nameof(SpaceportsPatchHandlers.SpaceportsHospitalityShuttleCheckPostfix));
            OptionalModPatches.PatchIfExists(harmony, AccessTools.Method(utils, "CheckIfClearForLanding"), typeof(SpaceportsPatchHandlers), postfix: nameof(SpaceportsPatchHandlers.SpaceportsCheckIfClearForLandingPostfix));
            Log.Message("[Space Services] Spaceports bridge patches installed.");
        }
    }
}
