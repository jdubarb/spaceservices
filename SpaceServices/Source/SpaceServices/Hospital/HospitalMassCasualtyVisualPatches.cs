using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace SpaceServices
{
    public static class HospitalMassCasualtyVisualPatches
    {
        public static void Install(Harmony harmony)
        {
            foreach (MethodBase method in TargetMethods())
            {
                try
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(HospitalMassCasualtyVisualPatches), nameof(SuppressDuringMassCasualtyPreDropPrefix)));
                }
                catch (Exception ex)
                {
                    ServiceDebugUtility.LogWarning(ServiceLogIntegration.Hospital, "Could not patch mass casualty visual effect method " + method.Name + ": " + ex.Message);
                }
            }
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return StaticMethods(typeof(FilthMaker), "TryMakeFilth", "MakeFilth")
                .Concat(StaticMethods(typeof(FleckMaker)))
                .Concat(StaticMethods(typeof(MoteMaker)));
        }

        private static IEnumerable<MethodBase> StaticMethods(Type type, params string[] methodNames)
        {
            HashSet<string> names = methodNames == null || methodNames.Length == 0 ? null : new HashSet<string>(methodNames);
            foreach (MethodInfo method in type.GetMethods(AccessTools.all))
            {
                if (!method.IsStatic || method.ContainsGenericParameters)
                {
                    continue;
                }
                if (names != null && !names.Contains(method.Name))
                {
                    continue;
                }
                yield return method;
            }
        }

        public static bool SuppressDuringMassCasualtyPreDropPrefix()
        {
            // Hospital applies wounds before the drop pod skyfaller visually lands.
            // Suppressing only inside that tiny call window avoids the early red cloud without hiding the pod landing.
            return !HospitalMassCasualtyVisualContext.ShouldSuppress;
        }
    }

    public static class HospitalMassCasualtyVisualContext
    {
        private static readonly Stack<bool> ActiveScopes = new Stack<bool>();

        public static bool ShouldSuppress
        {
            get
            {
                return ActiveScopes.Count > 0 && ActiveScopes.Peek() &&
                    SpaceServicesMod.Settings != null &&
                    SpaceServicesMod.Settings.suppressMassCasualtyPreDropEffects;
            }
        }

        public static void Push(bool massCasualty)
        {
            ActiveScopes.Push(massCasualty);
        }

        public static void Pop()
        {
            if (ActiveScopes.Count > 0)
            {
                ActiveScopes.Pop();
            }
        }
    }
}
