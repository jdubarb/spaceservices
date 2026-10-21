#if RIMWORLD_1_7
using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;

namespace SpaceServices
{
    /// <summary>Keep Room Service's injected bed data valid on pre-integration saves.</summary>
    internal static class RoomServiceLoadCompatibility
    {
        private const string CompTypeName = "HospitalityRoomService.CompSolicitationBed";
        private sealed class MissingCooldownData { }
        private static readonly ConditionalWeakTable<object, MissingCooldownData> MissingData =
            new ConditionalWeakTable<object, MissingCooldownData>();

        internal static void Install(Harmony harmony)
        {
            Type compType = AccessTools.TypeByName(CompTypeName);
            if (compType == null)
            {
                return;
            }
            // Fail closed if upstream changes the serializer's field layout.
            if (AccessTools.Field(compType, "cooldownUntilTick")?.FieldType == typeof(Dictionary<Pawn, int>) &&
                AccessTools.Field(compType, "enabled")?.FieldType == typeof(bool))
            {
                OptionalModPatches.PatchIfExists(harmony, AccessTools.DeclaredMethod(compType, "PostExposeData"),
                    typeof(RoomServiceLoadCompatibility), prefix: nameof(ExposeDataPrefix));
            }
            // Room Service's startup injector may run before or after ours.
            // Finish after all startup constructors, before any map is loaded.
            LongEventHandler.ExecuteWhenFinished(RemoveNonBedComponents);
        }

        private static void RemoveNonBedComponents()
        {
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.thingClass != null && typeof(Building_Bed).IsAssignableFrom(def.thingClass))
                {
                    continue;
                }
                // bed_humanlike defaults true for ordinary buildings too. It is
                // not evidence that geysers, pads, or other buildings are beds.
                def.comps?.RemoveAll(comp => comp.compClass?.FullName == CompTypeName);
            }
        }

        private static bool ExposeDataPrefix(object __instance, ref bool ___enabled,
            ref Dictionary<Pawn, int> ___cooldownUntilTick)
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                MissingData.Remove(__instance);
                if (Scribe.loader.curXmlParent["roomService_cooldowns"] == null)
                {
                    // A reference-keyed dictionary absent during LoadingVars
                    // must also be skipped during ResolvingCrossRefs.
                    MissingData.Add(__instance, new MissingCooldownData());
                    ___cooldownUntilTick = new Dictionary<Pawn, int>();
                    Scribe_Values.Look(ref ___enabled, "roomService_enabled");
                    return false;
                }
            }
            if (MissingData.TryGetValue(__instance, out _))
            {
                if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    MissingData.Remove(__instance);
                    return Scribe.mode == LoadSaveMode.Saving;
                }
                return false;
            }
            return true;
        }
    }
}
#endif
