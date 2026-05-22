using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SpaceServices
{
    [StaticConstructorOnStartup]
    public static class SpaceServicesBootstrap
    {
        public const string PackageId = "mlt.spaceservices.onesix";
        public const string CategoryDefName = "MLT_SpaceServices";

        private static readonly Harmony Harmony = new Harmony(PackageId);

        static SpaceServicesBootstrap()
        {
            Harmony.PatchAll(Assembly.GetExecutingAssembly());
            OptionalModPatches.Install(Harmony);
            LongEventHandler.ExecuteWhenFinished(ArchitectMenuPatch.InjectArchitectDesignators);
            Log.Message("[Space Services] Loaded.");
        }
    }

    public class SpaceServicesMod : Mod
    {
        public static SpaceServicesSettings Settings;

        public SpaceServicesMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<SpaceServicesSettings>();
        }

        public override string SettingsCategory()
        {
            return "Space Services";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("MLT_SpaceServices_Settings_DebugLogging".Translate(), ref Settings.debugLogging);
            listing.CheckboxLabeled("MLT_SpaceServices_Settings_AutoExtract".Translate(), ref Settings.autoExtractFallback);
            listing.CheckboxLabeled("MLT_SpaceServices_Settings_Hospital".Translate(), ref Settings.enableHospital);
            listing.CheckboxLabeled("MLT_SpaceServices_Settings_Hospitality".Translate(), ref Settings.enableHospitality);
            listing.CheckboxLabeled("MLT_SpaceServices_Settings_Spaceports".Translate(), ref Settings.enableSpaceportsBridge);
            listing.CheckboxLabeled("MLT_SpaceServices_Settings_RequirePad".Translate(), ref Settings.requireServicePadForArrivals);
            listing.CheckboxLabeled("MLT_SpaceServices_Settings_SealedNoSuit".Translate(), ref Settings.allowSealedNoSuitArrivals);
            listing.End();
            Settings.Write();
        }
    }

    public class SpaceServicesSettings : ModSettings
    {
        public bool debugLogging = true;
        public bool autoExtractFallback = true;
        public bool enableHospital = true;
        public bool enableHospitality = true;
        public bool enableSpaceportsBridge = true;
        public bool requireServicePadForArrivals = false;
        public bool allowSealedNoSuitArrivals = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref debugLogging, "debugLogging", true);
            Scribe_Values.Look(ref autoExtractFallback, "autoExtractFallback", true);
            Scribe_Values.Look(ref enableHospital, "enableHospital", true);
            Scribe_Values.Look(ref enableHospitality, "enableHospitality", true);
            Scribe_Values.Look(ref enableSpaceportsBridge, "enableSpaceportsBridge", true);
            Scribe_Values.Look(ref requireServicePadForArrivals, "requireServicePadForArrivals", false);
            Scribe_Values.Look(ref allowSealedNoSuitArrivals, "allowSealedNoSuitArrivals", false);
        }
    }

    public sealed class SpaceServicesMapComponent : MapComponent
    {
        public List<ServiceGroupRecord> serviceGroups = new List<ServiceGroupRecord>();
        private int nextDebugTick;
        private int nextLifecycleTick;

        public SpaceServicesMapComponent(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref serviceGroups, "serviceGroups", LookMode.Deep);
            if (serviceGroups == null)
            {
                serviceGroups = new List<ServiceGroupRecord>();
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (Find.TickManager.TicksGame >= nextLifecycleTick)
            {
                nextLifecycleTick = Find.TickManager.TicksGame + 250;
                ServiceLifecycleUtility.Tick(map, serviceGroups);
            }
            if (Find.TickManager.TicksGame < nextDebugTick)
            {
                return;
            }
            nextDebugTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging)
            {
                SpaceServiceEligibility eligibility = SpaceServiceMapDetector.Evaluate(map);
                Log.Message("[Space Services] " + eligibility.ToLogString(map));
            }
        }
    }

    public sealed class ServiceGroupRecord : IExposable
    {
        public string id;
        public string serviceKind;
        public string state = "arrived";
        public int arrivalTick;
        public int timeoutTick;
        public int departureRequestedTick;
        public Thing reservedPad;
        public List<Pawn> pawns = new List<Pawn>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref serviceKind, "serviceKind");
            Scribe_Values.Look(ref state, "state", "arrived");
            Scribe_Values.Look(ref arrivalTick, "arrivalTick", 0);
            Scribe_Values.Look(ref timeoutTick, "timeoutTick", 0);
            Scribe_Values.Look(ref departureRequestedTick, "departureRequestedTick", 0);
            Scribe_References.Look(ref reservedPad, "reservedPad");
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
        }
    }

    public class CompProperties_SpaceServicePad : CompProperties
    {
        public CompProperties_SpaceServicePad()
        {
            compClass = typeof(CompSpaceServicePad);
        }
    }

    public class CompSpaceServicePad : ThingComp
    {
        public bool allowGuests = true;
        public bool allowPatients = true;
        public bool allowEmergency = true;
        public bool requireVacSafeRoof = false;
        public bool preferShuttle = true;
        public bool requirePower = false;
        public string reservedForGroup;

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref allowGuests, "allowGuests", true);
            Scribe_Values.Look(ref allowPatients, "allowPatients", true);
            Scribe_Values.Look(ref allowEmergency, "allowEmergency", true);
            Scribe_Values.Look(ref requireVacSafeRoof, "requireVacSafeRoof", false);
            Scribe_Values.Look(ref preferShuttle, "preferShuttle", true);
            Scribe_Values.Look(ref requirePower, "requirePower", false);
            Scribe_Values.Look(ref reservedForGroup, "reservedForGroup");
        }

        public bool IsUsableFor(ServiceUse use)
        {
            if (!string.IsNullOrEmpty(reservedForGroup))
            {
                return false;
            }
            if (requirePower)
            {
                CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn)
                {
                    return false;
                }
            }
            if (use == ServiceUse.Patient && !allowPatients)
            {
                return false;
            }
            if (use == ServiceUse.Guest && !allowGuests)
            {
                return false;
            }
            if (use == ServiceUse.Emergency && !allowEmergency)
            {
                return false;
            }
            return true;
        }

        public bool TryReserve(string groupId)
        {
            if (string.IsNullOrEmpty(reservedForGroup) || reservedForGroup == groupId)
            {
                reservedForGroup = groupId;
                return true;
            }
            return false;
        }

        public void Release(string groupId)
        {
            if (reservedForGroup == groupId)
            {
                reservedForGroup = null;
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }
            yield return Toggle("MLT_SpaceServices_Gizmo_AllowGuests", () => allowGuests, v => allowGuests = v);
            yield return Toggle("MLT_SpaceServices_Gizmo_AllowPatients", () => allowPatients, v => allowPatients = v);
            yield return Toggle("MLT_SpaceServices_Gizmo_AllowEmergency", () => allowEmergency, v => allowEmergency = v);
            yield return Toggle("MLT_SpaceServices_Gizmo_RequireVacRoof", () => requireVacSafeRoof, v => requireVacSafeRoof = v);
            yield return Toggle("MLT_SpaceServices_Gizmo_PreferShuttle", () => preferShuttle, v => preferShuttle = v);
            yield return Toggle("MLT_SpaceServices_Gizmo_RequirePower", () => requirePower, v => requirePower = v);
            yield return new Command_Action
            {
                defaultLabel = "MLT_SpaceServices_Gizmo_ReportMap".Translate(),
                defaultDesc = "MLT_SpaceServices_Gizmo_ReportMapDesc".Translate(),
                action = delegate
                {
                    SpaceServiceEligibility eligibility = SpaceServiceMapDetector.Evaluate(parent.MapHeld);
                    Log.Message("[Space Services] " + eligibility.ToLogString(parent.MapHeld));
                    Messages.Message(eligibility.allowed ? "Space Services: map eligible" : "Space Services: map blocked", parent, MessageTypeDefOf.NeutralEvent, false);
                }
            };
        }

        private static Command_Toggle Toggle(string labelKey, Func<bool> getter, Action<bool> setter)
        {
            return new Command_Toggle
            {
                defaultLabel = labelKey.Translate(),
                isActive = getter,
                toggleAction = delegate { setter(!getter()); }
            };
        }
    }

    public enum ServiceUse
    {
        Guest,
        Patient,
        Emergency
    }

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
            Thing pad = TryFindServicePad(map, use);
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
    }

    public sealed class SpaceServiceEligibility
    {
        public bool allowed;
        public readonly List<string> allowReasons = new List<string>();
        public readonly List<string> blockReasons = new List<string>();

        public string ToLogString(Map map)
        {
            string mapId = map == null ? "null map" : "map " + map.uniqueID;
            return mapId + " allowed=" + allowed + " allow=[" + string.Join(", ", allowReasons.ToArray()) + "] block=[" + string.Join(", ", blockReasons.ToArray()) + "]";
        }
    }

    public static class SpaceServiceMapDetector
    {
        public static SpaceServiceEligibility Evaluate(Map map)
        {
            SpaceServiceEligibility result = new SpaceServiceEligibility();
            if (map == null)
            {
                result.blockReasons.Add("no map");
                return result;
            }

            object parent = Reflect.GetMember(map, "Parent");
            string parentDef = Reflect.DefName(parent);
            string parentType = parent == null ? "" : parent.GetType().FullName ?? "";

            if (parentDef == "Gravship" || parentType.EndsWith(".Gravship", StringComparison.Ordinal) || parentType == "RimWorld.Gravship")
            {
                result.blockReasons.Add("actual gravship parent");
            }
            if (parentDef == "QE_SpaceCustomSite" || parentDef == "QE_CustomMap_SpaceSubMap" || parentType.StartsWith("QuestEditor_Library.", StringComparison.Ordinal))
            {
                result.blockReasons.Add("temporary quest space map");
            }

            string layerDef = DefNameFromNested(map, "Tile", "LayerDef");
            if (Reflect.BoolFromNested(map, "Tile", "LayerDef", "isSpace"))
            {
                result.allowReasons.Add("tile layer is space:" + layerDef);
            }

            string biomeDef = Reflect.DefName(map.Biome);
            if (Reflect.BoolMember(map.Biome, "inVacuum"))
            {
                result.allowReasons.Add("biome is vacuum:" + biomeDef);
            }

            object orbitalDebris = Reflect.GetMember(map, "OrbitalDebris") ?? Reflect.GetMember(map, "orbitalDebris");
            if (Reflect.DefName(orbitalDebris) == "Asteroid" || Convert.ToString(orbitalDebris) == "Asteroid")
            {
                result.allowReasons.Add("orbital debris asteroid");
            }

            object generatorDef = Reflect.GetMember(map, "generatorDef") ?? Reflect.GetMember(map, "GeneratorDef");
            string generator = Reflect.DefName(generatorDef);
            if (ContainsAny(generator, "Asteroid", "Orbit", "Moon", "Station", "Space"))
            {
                result.allowReasons.Add("space-like generator:" + generator);
            }

            if (parentDef == "SpaceSettlement")
            {
                result.allowReasons.Add("space settlement parent");
            }
            if (TypeOrBaseNameContains(parent, "SpaceMapParent") || ContainsAny(parentType, "SpaceSettlement", "AsteroidMapParent", "Station", "OrbitalBase"))
            {
                result.allowReasons.Add("stationary space parent:" + parentType);
            }

            if (Reflect.BoolFromNested(map, "Tile", "LayerDef", "isSpace") == false && ContainsAny(layerDef, "SkyIsland", "Troposphere", "Stratosphere", "Mesosphere"))
            {
                result.blockReasons.Add("non-vacuum atmospheric or sky layer");
            }

            result.allowed = result.blockReasons.Count == 0 && result.allowReasons.Count > 0;
            return result;
        }

        public static bool IsServiceEligible(Map map)
        {
            return Evaluate(map).allowed;
        }

        private static string DefNameFromNested(object root, params string[] path)
        {
            object current = root;
            for (int i = 0; i < path.Length; i++)
            {
                current = Reflect.GetMember(current, path[i]);
            }
            return Reflect.DefName(current);
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int i = 0; i < needles.Length; i++)
            {
                if (value.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TypeOrBaseNameContains(object obj, string name)
        {
            for (Type type = obj == null ? null : obj.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || (type.FullName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public static class VacSuitUtility
    {
        private static readonly string[] AdultSuitDefs = { "Apparel_Vacsuit", "Apparel_VacsuitHelmet" };
        private static readonly string[] ChildSuitDefs = { "Apparel_VacsuitChildren", "Apparel_VacsuitHelmet" };

        public static void SuitPawnsForVacuum(IEnumerable<Pawn> pawns)
        {
            if (pawns == null)
            {
                return;
            }
            foreach (Pawn pawn in pawns)
            {
                SuitPawnForVacuum(pawn);
            }
        }

        public static void SuitPawnForVacuum(Pawn pawn)
        {
            if (pawn == null || pawn.apparel == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            string[] defs = pawn.DevelopmentalStage == DevelopmentalStage.Child ? ChildSuitDefs : AdultSuitDefs;
            for (int i = 0; i < defs.Length; i++)
            {
                TryWearIfNeeded(pawn, defs[i]);
            }
        }

        private static void TryWearIfNeeded(Pawn pawn, string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                return;
            }
            if (pawn.apparel.WornApparel.Any(worn => worn.def == def))
            {
                return;
            }
            Apparel newApparel = ThingMaker.MakeThing(def) as Apparel;
            if (newApparel == null)
            {
                return;
            }
            pawn.apparel.Wear(newApparel, false, true);
        }
    }

    public static class DepartureUtility
    {
        public static bool CompleteDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record == null || record.pawns == null)
            {
                return false;
            }

            record.state = "departing";
            record.departureRequestedTick = Find.TickManager.TicksGame;
            Log.Message("[Space Services] Departing " + record.serviceKind + " service group " + record.id + ": " + reason);

            bool completed = TryAutoExtract(record.pawns, reason);
            if (completed)
            {
                record.state = "completed";
                ReleaseReservation(record);
                Messages.Message("Space Services: service group departed", MessageTypeDefOf.NeutralEvent, false);
            }
            return completed;
        }

        public static bool TryAutoExtract(IEnumerable<Pawn> pawns, string reason)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.autoExtractFallback)
            {
                return false;
            }
            bool any = false;
            foreach (Pawn pawn in pawns ?? Enumerable.Empty<Pawn>())
            {
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                any = true;
                if (pawn.Spawned)
                {
                    NotifyLordPawnExited(pawn);
                    pawn.DeSpawn(DestroyMode.Vanish);
                }
                else if (pawn.MapHeld != null)
                {
                    pawn.Destroy(DestroyMode.Vanish);
                }
            }
            if (any)
            {
                Log.Message("[Space Services] Auto-extracted service pawns: " + reason);
            }
            return any;
        }

        private static void ReleaseReservation(ServiceGroupRecord record)
        {
            CompSpaceServicePad pad = record.reservedPad == null ? null : record.reservedPad.TryGetComp<CompSpaceServicePad>();
            if (pad != null)
            {
                pad.Release(record.id);
            }
            record.reservedPad = null;
        }

        private static void NotifyLordPawnExited(Pawn pawn)
        {
            try
            {
                Lord lord = pawn.GetLord();
                if (lord == null)
                {
                    return;
                }
                MethodInfo method = AccessTools.Method(typeof(Lord), "Notify_PawnLost", new[] { typeof(Pawn), typeof(PawnLostCondition) });
                if (method != null)
                {
                    method.Invoke(lord, new object[] { pawn, PawnLostCondition.ExitedMap });
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not notify lord before service departure: " + ex.Message);
            }
        }
    }

    public static class ServiceLifecycleUtility
    {
        public static void RegisterPawns(Map map, string kind, IEnumerable<Pawn> pawns)
        {
            if (map == null || pawns == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return;
            }

            List<Pawn> list = pawns.Where(p => p != null && !p.Destroyed).Distinct().ToList();
            if (list.Count == 0)
            {
                return;
            }

            ServiceGroupRecord existing = comp.serviceGroups.FirstOrDefault(active =>
                active != null &&
                active.state != "completed" &&
                active.pawns != null &&
                active.pawns.Any(pawn => list.Contains(pawn)));
            if (existing != null)
            {
                foreach (Pawn pawn in list)
                {
                    if (!existing.pawns.Contains(pawn))
                    {
                        existing.pawns.Add(pawn);
                    }
                }
                existing.timeoutTick = Math.Max(existing.timeoutTick, Find.TickManager.TicksGame + GenDate.TicksPerDay * 3);
                return;
            }

            ServiceUse use = kind == "hospital" ? ServiceUse.Patient : ServiceUse.Guest;
            ServiceGroupRecord record = new ServiceGroupRecord
            {
                id = "SS-" + Find.UniqueIDsManager.GetNextThingID(),
                serviceKind = kind,
                state = "arrived",
                arrivalTick = Find.TickManager.TicksGame,
                timeoutTick = Find.TickManager.TicksGame + GenDate.TicksPerDay * 3,
                pawns = list
            };

            Thing pad = ServicePadUtility.TryFindServicePad(map, use);
            if (pad != null)
            {
                CompSpaceServicePad padComp = pad.TryGetComp<CompSpaceServicePad>();
                if (padComp != null && padComp.TryReserve(record.id))
                {
                    record.reservedPad = pad;
                }
            }

            comp.serviceGroups.Add(record);
            Log.Message("[Space Services] Registered " + kind + " service group " + record.id + " pawns=" + list.Count + " padReserved=" + (record.reservedPad != null));
        }

        public static void Tick(Map map, List<ServiceGroupRecord> records)
        {
            if (map == null || records == null || records.Count == 0)
            {
                return;
            }
            for (int i = records.Count - 1; i >= 0; i--)
            {
                ServiceGroupRecord record = records[i];
                if (record == null || record.state == "completed")
                {
                    records.RemoveAt(i);
                    continue;
                }
                record.pawns = record.pawns == null ? new List<Pawn>() : record.pawns.Where(p => p != null && !p.Destroyed).Distinct().ToList();
                if (record.pawns.Count == 0)
                {
                    ReleaseRecord(record);
                    records.RemoveAt(i);
                    continue;
                }
                if (record.state == "departing")
                {
                    if (Find.TickManager.TicksGame > record.departureRequestedTick + GenDate.TicksPerHour)
                    {
                        DepartureUtility.CompleteDeparture(map, record, "departure timeout fallback");
                    }
                    continue;
                }
                if (record.pawns.Any(IsTryingToLeave))
                {
                    DepartureUtility.CompleteDeparture(map, record, "service lord entered departure state");
                    continue;
                }
                if (Find.TickManager.TicksGame > record.timeoutTick)
                {
                    DepartureUtility.CompleteDeparture(map, record, "service visit timeout");
                }
            }
        }

        private static bool IsTryingToLeave(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned)
            {
                return false;
            }
            Lord lord = pawn.GetLord();
            string lordJob = lord == null || lord.LordJob == null ? "" : lord.LordJob.GetType().FullName ?? "";
            if (ContainsAny(lordJob, "TravelAndExit", "ExitOnShuttle", "Depart"))
            {
                return true;
            }
            string duty = pawn.mindState == null || pawn.mindState.duty == null || pawn.mindState.duty.def == null ? "" : pawn.mindState.duty.def.defName;
            return ContainsAny(duty, "Exit", "Depart", "Leave");
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int i = 0; i < needles.Length; i++)
            {
                if (value.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static void ReleaseRecord(ServiceGroupRecord record)
        {
            CompSpaceServicePad pad = record.reservedPad == null ? null : record.reservedPad.TryGetComp<CompSpaceServicePad>();
            if (pad != null)
            {
                pad.Release(record.id);
            }
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), "CanFireNow")]
    public static class ServiceIncidentCanFireNowPatch
    {
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            if (__result || __instance == null || parms == null)
            {
                return;
            }

            Map map = parms.target as Map ?? Find.CurrentMap;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }

            IncidentDef incident = Reflect.GetMember(__instance, "def") as IncidentDef;
            if (!ServiceIncidentUtility.ShouldForceAllow(incident == null ? null : incident.defName, map))
            {
                return;
            }

            __result = true;
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.debugLogging)
            {
                Log.Message("[Space Services] allowed service incident on eligible space map: " + (incident == null ? "unknown" : incident.defName));
            }
        }
    }

    public static class ServiceIncidentUtility
    {
        private static readonly HashSet<string> HospitalIncidents = new HashSet<string>
        {
            "PatientArrives",
            "MassCasualtyEvent"
        };

        private static readonly HashSet<string> HospitalityIncidents = new HashSet<string>
        {
            "VisitorGroup",
            "VisitorGroupMax",
            "VisitorGroupSelectFaction",
            "VisitorGroupSpacerCruise",
            "VisitorGroupSpacerLuxury"
        };

        public static bool ShouldForceAllow(string incidentDefName, Map map)
        {
            if (string.IsNullOrEmpty(incidentDefName))
            {
                return false;
            }
            if (HospitalIncidents.Contains(incidentDefName))
            {
                return (SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableHospital) && HasRequiredPad(map, ServiceUse.Patient);
            }
            if (HospitalityIncidents.Contains(incidentDefName))
            {
                return (SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableHospitality) && HasRequiredPad(map, ServiceUse.Guest);
            }
            return false;
        }

        private static bool HasRequiredPad(Map map, ServiceUse use)
        {
            if (SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.requireServicePadForArrivals)
            {
                return true;
            }
            return ServicePadUtility.TryFindServicePad(map, use) != null;
        }
    }

    [HarmonyPatch(typeof(DesignationCategoryDef), "ResolveReferences")]
    public static class ArchitectMenuPatch
    {
        private static readonly string[] DuplicateBuildDefs =
        {
            "PodLauncher",
            "TransportPod",
            "ShipLandingBeacon",
            "PatientLandingSpot",
            "Spaceports_ShuttleLandingPad",
            "Spaceports_Beacon"
        };

        public static void Postfix(DesignationCategoryDef __instance)
        {
            if (__instance != null && __instance.defName == SpaceServicesBootstrap.CategoryDefName)
            {
                InjectArchitectDesignators();
            }
        }

        public static void InjectArchitectDesignators()
        {
            DesignationCategoryDef category = DefDatabase<DesignationCategoryDef>.GetNamedSilentFail(SpaceServicesBootstrap.CategoryDefName);
            if (category == null)
            {
                return;
            }
            FieldInfo field = AccessTools.Field(typeof(DesignationCategoryDef), "resolvedDesignators");
            List<Designator> designators = field == null ? null : field.GetValue(category) as List<Designator>;
            if (designators == null)
            {
                return;
            }

            HashSet<string> existing = new HashSet<string>(designators.OfType<Designator_Build>().Select(d => d.PlacingDef == null ? "" : d.PlacingDef.defName));
            for (int i = 0; i < DuplicateBuildDefs.Length; i++)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(DuplicateBuildDefs[i]);
                if (def == null || existing.Contains(def.defName))
                {
                    continue;
                }
                designators.Add(new Designator_Build(def));
                existing.Add(def.defName);
            }
        }
    }

    public static class OptionalModPatches
    {
        public static void Install(Harmony harmony)
        {
            TryPatchSpaceports(harmony);
            TryPatchHospital(harmony);
            TryPatchHospitality(harmony);
        }

        private static void TryPatchSpaceports(Harmony harmony)
        {
            Type utils = AccessTools.TypeByName("Spaceports.Utils");
            if (utils == null)
            {
                return;
            }
            PatchIfExists(harmony, AccessTools.Method(utils, "IsMapInSpace"), postfix: nameof(OptionalPatchHandlers.SpaceportsIsMapInSpacePostfix));
            PatchIfExists(harmony, AccessTools.Method(utils, "SuitUpPawns"), prefix: nameof(OptionalPatchHandlers.SpaceportsSuitUpPawnsPrefix));
            PatchIfExists(harmony, AccessTools.Method(utils, "HospitalityShuttleCheck"), postfix: nameof(OptionalPatchHandlers.SpaceportsHospitalityShuttleCheckPostfix));
            PatchIfExists(harmony, AccessTools.Method(utils, "CheckIfClearForLanding"), postfix: nameof(OptionalPatchHandlers.SpaceportsCheckIfClearForLandingPostfix));
            Log.Message("[Space Services] Spaceports bridge patches installed.");
        }

        private static void TryPatchHospital(Harmony harmony)
        {
            if (SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.enableHospital)
            {
                return;
            }
            Type patientUtility = AccessTools.TypeByName("Hospital.Utilities.PatientUtility") ?? AccessTools.TypeByName("Hospital.PatientUtility");
            if (patientUtility != null)
            {
                foreach (MethodInfo method in patientUtility.GetMethods(AccessTools.all).Where(m => m.Name == "SetUpNewPatient" || m.Name == "Arrive"))
                {
                    PatchIfExists(harmony, method, postfix: nameof(OptionalPatchHandlers.SuitPawnsInArgsPostfix));
                }
                MethodInfo landing = patientUtility.GetMethods(AccessTools.all).FirstOrDefault(m => m.Name == "TryFindSafeLandingSpotCloseToColony" && m.ReturnType == typeof(IntVec3));
                PatchIfExists(harmony, landing, postfix: nameof(OptionalPatchHandlers.HospitalLandingSpotPostfix));
            }
        }

        private static void TryPatchHospitality(Harmony harmony)
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
                    PatchIfExists(harmony, method, postfix: nameof(OptionalPatchHandlers.SuitPawnsInArgsPostfix));
                }
            }
        }

        private static void PatchIfExists(Harmony harmony, MethodInfo method, string prefix = null, string postfix = null)
        {
            if (method == null)
            {
                return;
            }
            try
            {
                HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(typeof(OptionalPatchHandlers), prefix);
                HarmonyMethod post = postfix == null ? null : new HarmonyMethod(typeof(OptionalPatchHandlers), postfix);
                harmony.Patch(method, pre, post, null);
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Could not patch " + method.FullDescription() + ": " + ex.Message);
            }
        }
    }

    public static class OptionalPatchHandlers
    {
        public static void SpaceportsIsMapInSpacePostfix(Map map, ref bool __result)
        {
            if (SpaceServicesMod.Settings != null && SpaceServicesMod.Settings.enableSpaceportsBridge && SpaceServiceMapDetector.IsServiceEligible(map))
            {
                __result = true;
            }
        }

        public static bool SpaceportsSuitUpPawnsPrefix(List<Pawn> pawns)
        {
            VacSuitUtility.SuitPawnsForVacuum(pawns);
            return true;
        }

        public static void SpaceportsHospitalityShuttleCheckPostfix(Map map, Faction faction, ref bool __result)
        {
            if (__result || SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.enableSpaceportsBridge || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (faction != null && faction.def != null && faction.def.techLevel == TechLevel.Neolithic)
            {
                return;
            }
            __result = true;
        }

        public static void SpaceportsCheckIfClearForLandingPostfix(Map map, int typeVal, ref bool __result)
        {
            if (__result || SpaceServicesMod.Settings == null || !SpaceServicesMod.Settings.enableSpaceportsBridge || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (typeVal != 1 && typeVal != 3)
            {
                return;
            }
            if (!SpaceServicesMod.Settings.enableHospitality || ServicePadUtility.TryFindServicePad(map, ServiceUse.Guest) == null)
            {
                return;
            }
            if (HasBlockingLandingCondition(map))
            {
                return;
            }
            __result = true;
        }

        public static void HospitalLandingSpotPostfix(object[] __args, ref IntVec3 __result)
        {
            Map map = FindMap(__args);
            if (map != null && SpaceServiceMapDetector.IsServiceEligible(map) && ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out IntVec3 cell))
            {
                __result = cell;
            }
        }

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
                VacSuitUtility.SuitPawnForVacuum(pawn);
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

        private static bool HasBlockingLandingCondition(Map map)
        {
            if (map == null)
            {
                return true;
            }
            GameConditionDef kessler = DefDatabase<GameConditionDef>.GetNamedSilentFail("Spaceports_KesslerSyndrome");
            if (kessler != null && map.gameConditionManager.ConditionIsActive(kessler))
            {
                return true;
            }
            if (GenHostility.AnyHostileActiveThreatToPlayer(map, true))
            {
                return true;
            }
            return false;
        }

        private static IEnumerable<Pawn> PawnsFromArgs(object[] args)
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

        private static Map FindMap(object[] args)
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

    public static class Reflect
    {
        public static object GetMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            Type type = obj.GetType();
            PropertyInfo property = AccessTools.Property(type, name);
            if (property != null)
            {
                return property.GetValue(obj, null);
            }
            FieldInfo field = AccessTools.Field(type, name);
            return field == null ? null : field.GetValue(obj);
        }

        public static bool BoolMember(object obj, string name)
        {
            object value = GetMember(obj, name);
            return value is bool && (bool)value;
        }

        public static bool BoolFromNested(object root, params string[] path)
        {
            object current = root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                current = GetMember(current, path[i]);
            }
            return BoolMember(current, path[path.Length - 1]);
        }

        public static string DefName(object obj)
        {
            Def def = obj as Def;
            if (def != null)
            {
                return def.defName ?? "";
            }
            object value = GetMember(obj, "defName");
            return value == null ? "" : Convert.ToString(value);
        }
    }
}
