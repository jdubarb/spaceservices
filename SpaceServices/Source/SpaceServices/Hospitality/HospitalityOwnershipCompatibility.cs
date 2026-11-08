#if RIMWORLD_1_7
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Verse;

namespace SpaceServices
{
    /// <summary>Opt-in ownership lifecycle correction for one inspected Hospitality binary.</summary>
    internal static class HospitalityOwnershipCompatibility
    {
        private const string ApprovedHospitality = "6c169fed1229d1ee18b478aabfc7cbf23f92dc401a641a33c938e8055b345d56";
        // The migration depends on the inspected cross-reference/PostLoadInit ordering.
        private const string ApprovedGame = "d55aa8e47fa3c5463ca85b0eccd51f195b4bbcd04f56da3af21c2f6688a81254";
        private static bool installed;
        private static string eligibility = "Startup validation pending";
        private static Type guestType;
        private static Type bedType;
        private static FieldInfo guestBed, guestLord, postLoaded, ownedBed, mothballed, postLoadObjects, uninstalledAssignees;
        private static MethodInfo clearOwnership;
        private sealed class SavedFormerGuest { }
        private static readonly ConditionalWeakTable<object, SavedFormerGuest> SavedFormerGuests =
            new ConditionalWeakTable<object, SavedFormerGuest>();

        internal static bool Active => installed && SpaceServicesMod.Settings?.hospitalityOwnershipWorkaround == true;
        internal static string Status => !installed ? "Inactive: " + eligibility :
            Active ? "Active: approved Hospitality and game builds. Existing saves are checked on load." :
            "Disabled (approved builds available). Enable before loading an affected save.";

        internal static void Install(Harmony harmony)
        {
            try
            {
                Assembly[] candidates = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetType("Hospitality.CompGuest", false) != null).ToArray();
                if (candidates.Length != 1)
                    throw new InvalidOperationException("Hospitality assembly missing or ambiguous");
                guestType = candidates[0].GetType("Hospitality.CompGuest", true);
                Assembly assembly = guestType.Assembly;
                var owners = LoadedModManager.RunningModsListForReading
                    .Where(m => m.assemblies.loadedAssemblies.Contains(assembly)).ToArray();
                if (owners.Length != 1 || !string.Equals(owners[0].PackageId, "Orion.Hospitality", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Hospitality package identity missing or ambiguous");
                if (Hash(assembly) != ApprovedHospitality)
                    throw new InvalidOperationException("Hospitality SHA-256 is not approved");
                if (Hash(typeof(Pawn_Ownership).Assembly) != ApprovedGame)
                    throw new InvalidOperationException("RimWorld SHA-256 is not approved (requires inspected 1.7.5000 build)");

                bedType = assembly.GetType("Hospitality.Building_GuestBed", true);
                if (!typeof(ThingComp).IsAssignableFrom(guestType) || !typeof(Building_Bed).IsAssignableFrom(bedType))
                    throw new InvalidOperationException("Hospitality type layout changed");
                guestBed = Field(guestType, "bed", bedType);
                guestLord = Field(guestType, "lord", typeof(Verse.AI.Group.Lord));
                postLoaded = Field(guestType, "postLoaded", typeof(bool));
                ownedBed = Field(typeof(Pawn_Ownership), "intOwnedBed", typeof(Building_Bed));
                Field(typeof(Pawn_Ownership), "pawn", typeof(Pawn));
                Field(typeof(Thing), "despawnedTick", typeof(int));
                Field(typeof(Pawn), "becameWorldPawnTickAbs", typeof(int));
                mothballed = Field(typeof(WorldPawns), "pawnsMothballed", typeof(HashSet<Pawn>));
                postLoadObjects = Field(typeof(PostLoadIniter), "saveablesToPostLoad", typeof(HashSet<IExposable>));
                uninstalledAssignees = Field(typeof(CompAssignableToPawn), "uninstalledAssignedPawns", typeof(List<Pawn>));
                clearOwnership = Method(guestType, "ClearOwnership", typeof(void));
                Method(guestType, "Leave", typeof(void), typeof(bool));
                Method(guestType, "ClaimBed", typeof(void), bedType);
                Method(typeof(Pawn_Ownership), "get_OwnedBed", typeof(Building_Bed));
                Method(typeof(Pawn_Ownership), "set_OwnedBed", typeof(void), typeof(Building_Bed));
                Method(typeof(Pawn_Ownership), "ExposeData", typeof(void));
                Method(typeof(CrossRefHandler), "ResolveAllCrossReferences", typeof(void));
                Method(typeof(ScribeLoader), "FinalizeLoading", typeof(void));
                MethodInfo upstreamPrefix = Method(assembly.GetType("Hospitality.Patches.Pawn_Ownership_Patch+UnclaimBed", true),
                    "Prefix", typeof(bool), typeof(Pawn));
                if (!upstreamPrefix.IsStatic) throw new InvalidOperationException("Hospitality prefix is no longer static");
                MethodInfo unclaim = Method(typeof(Pawn_Ownership), "UnclaimBed", typeof(bool));
                MethodInfo expose = Method(guestType, "PostExposeData", typeof(void));
                MethodInfo postLoad = Method(typeof(PostLoadIniter), "DoAllPostLoadInits", typeof(void));
                harmony.Patch(upstreamPrefix, prefix: new HarmonyMethod(typeof(HospitalityOwnershipCompatibility), nameof(DeferUpstreamClear)));
                harmony.Patch(unclaim, postfix: new HarmonyMethod(typeof(HospitalityOwnershipCompatibility), nameof(UnclaimPostfix)));
                harmony.Patch(expose, prefix: new HarmonyMethod(typeof(HospitalityOwnershipCompatibility), nameof(GuestExposePrefix)));
                harmony.Patch(postLoad, prefix: new HarmonyMethod(typeof(HospitalityOwnershipCompatibility), nameof(BeforePostLoad)));
                installed = true;
                eligibility = "Approved builds";
            }
            catch (Exception ex)
            {
                // Partial installation is inert: every handler checks installed as well as the preference.
                eligibility = ex.Message;
            }
            Log.Message("[Space Services] Experimental Hospitality ownership workaround: " + Status +
                " Build validation runs once at startup; restart after mod/game updates.");
        }

        private static string Hash(Assembly assembly)
        {
            using (Stream stream = File.OpenRead(assembly.Location))
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo field = AccessTools.DeclaredField(type, name);
            if (field == null || field.IsStatic || field.FieldType != fieldType)
                throw new InvalidOperationException("Field layout mismatch: " + type.FullName + "." + name);
            return field;
        }

        private static MethodInfo Method(Type type, string name, Type result, params Type[] args)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, args);
            if (method == null || method.ReturnType != result || method.ContainsGenericParameters ||
                !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(args))
                throw new InvalidOperationException("Method layout mismatch: " + type.FullName + "." + name);
            return method;
        }

        private static ThingComp Guest(Pawn pawn) => pawn?.AllComps?.FirstOrDefault(c => c.GetType() == guestType);

        // Skip only Hospitality's clearing prefix, allowing vanilla UnclaimBed to see the guest bed.
        private static bool DeferUpstreamClear(ref bool __result)
        {
            if (!Active) return true;
            __result = true;
            return false;
        }

        private static void UnclaimPostfix(Pawn ___pawn)
        {
            if (!Active) return;
            ThingComp guest = Guest(___pawn);
            if (guest != null) clearOwnership.Invoke(guest, null);
        }

        private static void GuestExposePrefix(object __instance)
        {
            if (!Active || Scribe.mode != LoadSaveMode.LoadingVars) return;
            SavedFormerGuests.Remove(__instance);
            var node = Scribe.loader.curXmlParent;
            // Require explicit saved nulls, not nulls produced by a broken cross-reference.
            string arrived = node?["arrived"]?.InnerText;
            bool savedInactive = arrived == null || (bool.TryParse(arrived, out bool value) && !value);
            if (node?["bed"]?.InnerText == "null" && node["lord"]?.InnerText == "null" &&
                savedInactive)
                SavedFormerGuests.Add(__instance, new SavedFormerGuest());
        }

        private static void BeforePostLoad(PostLoadIniter __instance)
        {
            if (!Active || __instance != Scribe.loader.initer || Scribe.mode != LoadSaveMode.Inactive) return;
            var objects = (HashSet<IExposable>)postLoadObjects.GetValue(__instance);
            // This boundary is after ALL cross-references, before ANY PostLoadInit side effects.
            // Mothballed membership, not Spawned alone, excludes map pawns awaiting spawn setup.
            foreach (Pawn pawn in objects.OfType<WorldPawns>().SelectMany(world =>
                mothballed.GetValue(world) as IEnumerable<Pawn> ?? Enumerable.Empty<Pawn>()))
            {
                if (pawn == null || pawn.Spawned || pawn.Dead || pawn.Destroyed || pawn.IsColonist ||
                    pawn.despawnedTick <= 0 || pawn.becameWorldPawnTickAbs <= 0 || pawn.ownership == null) continue;
                ThingComp guest = Guest(pawn);
                if (guest == null || !SavedFormerGuests.TryGetValue(guest, out _) ||
                    guestBed.GetValue(guest) != null || guestLord.GetValue(guest) != null ||
                    (bool)postLoaded.GetValue(guest)) continue;
                Building_Bed bed = ownedBed.GetValue(pawn.ownership) as Building_Bed;
                if (bed == null || bed.GetType() != bedType || bed.Medical || bed.CompAssignableToPawn == null ||
                    bed.CompAssignableToPawn.AssignedPawnsForReading == null ||
                    bed.CompAssignableToPawn.AssignedPawnsForReading.Contains(pawn) ||
                    !(uninstalledAssignees.GetValue(bed.CompAssignableToPawn) is List<Pawn> uninstalled) ||
                    uninstalled.Count != 0) continue;
                // Do not call the setter or UnclaimBed: they evaluate unbound bedroom memories here.
                ownedBed.SetValue(pawn.ownership, null);
                Log.Message("[Space Services] Experimental Hospitality ownership migration cleared " +
                    pawn.GetUniqueLoadID() + " -> " + bed.GetUniqueLoadID() + " before PostLoadInit.");
            }
        }
    }
}
#endif
