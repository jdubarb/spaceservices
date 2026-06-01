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
    public static class OptionalModPatches
    {
        public static void Install(Harmony harmony)
        {
            HospitalPatches.Install(harmony);
            HospitalityPatches.Install(harmony);
            MedPodPatches.Install(harmony);
        }

        public static void PatchIfExists(Harmony harmony, MethodInfo method, Type handlerType, string prefix = null, string postfix = null)
        {
            if (method == null)
            {
                return;
            }
            try
            {
                HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(handlerType, prefix);
                HarmonyMethod post = postfix == null ? null : new HarmonyMethod(handlerType, postfix);
                harmony.Patch(method, pre, post, null);
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not patch " + method.FullDescription() + ": " + ex.Message);
            }
        }
    }
}
