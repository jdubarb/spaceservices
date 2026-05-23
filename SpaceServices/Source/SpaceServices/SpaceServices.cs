using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
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
        private const int StaleReferenceCleanupVersion = 2;
        private int nextDebugTick;
        private int nextLifecycleTick;
        private bool staleReferenceCleanupDone;
        private int staleReferenceCleanupVersion;

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
            Scribe_Values.Look(ref staleReferenceCleanupDone, "staleReferenceCleanupDone", false);
            Scribe_Values.Look(ref staleReferenceCleanupVersion, "staleReferenceCleanupVersion", 0);
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (Find.TickManager.TicksGame >= nextLifecycleTick)
            {
                nextLifecycleTick = Find.TickManager.TicksGame + 250;
                if (!staleReferenceCleanupDone || staleReferenceCleanupVersion < StaleReferenceCleanupVersion)
                {
                    staleReferenceCleanupDone = true;
                    staleReferenceCleanupVersion = StaleReferenceCleanupVersion;
                    StaleReferenceCleanupUtility.CleanupAfterLoad(map);
                }
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

        public void ForceRelease()
        {
            reservedForGroup = null;
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
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Clear service reservation",
                    action = delegate
                    {
                        string oldReservation = reservedForGroup;
                        ForceRelease();
                        ServiceLifecycleUtility.ReleaseGroup(parent.MapHeld, oldReservation, "dev cleared pad reservation");
                        Messages.Message("Space Services: pad reservation cleared", parent, MessageTypeDefOf.NeutralEvent, false);
                    }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Spawn patient here",
                    action = delegate
                    {
                        bool spawned = HospitalPatientFallback.TryExecutePatientArrival(null, ServiceDebugUtility.PatientArrivalParms(parent.MapHeld), parent.MapHeld, parent.Position);
                        Messages.Message(spawned ? "Space Services: dev patient spawned" : "Space Services: dev patient spawn failed", parent, MessageTypeDefOf.NeutralEvent, false);
                    }
                };
            }
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

        public static int CountServicePads(Map map, ServiceUse use)
        {
            return AllServicePads(map, use).Count();
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

        public static bool ReleaseGroup(Map map, string groupId, string reason)
        {
            if (map == null || string.IsNullOrEmpty(groupId))
            {
                return false;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return false;
            }
            ServiceGroupRecord record = comp.serviceGroups.FirstOrDefault(group => group != null && group.id == groupId);
            if (record == null)
            {
                return false;
            }
            BeginDeparture(map, record, reason);
            Log.Message("[Space Services] Released service group " + groupId + ": " + reason);
            return true;
        }

        public static bool RequestDepartureForPawn(Pawn pawn, string reason)
        {
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record))
            {
                return false;
            }
            BeginDeparture(map, record, reason);
            return true;
        }

        public static bool ReleasePawn(Pawn pawn, string reason)
        {
            Map map;
            ServiceGroupRecord record;
            if (!TryFindRecordForPawn(pawn, out map, out record))
            {
                return false;
            }
            ReleaseRecord(record);
            record.state = "completed";
            Log.Message("[Space Services] Released service group " + record.id + ": " + reason);
            return true;
        }

        private static bool TryFindRecordForPawn(Pawn pawn, out Map map, out ServiceGroupRecord record)
        {
            map = null;
            record = null;
            if (pawn == null)
            {
                return false;
            }
            IEnumerable<Map> maps = Find.Maps ?? Enumerable.Empty<Map>();
            Map heldMap = pawn.MapHeld;
            if (heldMap != null)
            {
                maps = new[] { heldMap }.Concat(maps.Where(candidate => candidate != heldMap));
            }
            foreach (Map candidate in maps)
            {
                SpaceServicesMapComponent comp = candidate == null ? null : candidate.GetComponent<SpaceServicesMapComponent>();
                ServiceGroupRecord found = comp == null || comp.serviceGroups == null ? null : comp.serviceGroups.FirstOrDefault(group =>
                    group != null &&
                    group.state != "completed" &&
                    group.pawns != null &&
                    group.pawns.Contains(pawn));
                if (found != null)
                {
                    map = candidate;
                    record = found;
                    return true;
                }
            }
            return false;
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
                    if (ReadyForExtraction(record))
                    {
                        DepartureUtility.CompleteDeparture(map, record, "service pawns reached departure pad");
                    }
                    else
                    {
                        GuideDepartingPawnsToPad(record);
                    }
                    if (Find.TickManager.TicksGame > record.departureRequestedTick + GenDate.TicksPerHour)
                    {
                        DepartureUtility.CompleteDeparture(map, record, "departure timeout fallback");
                    }
                    continue;
                }
                if (record.serviceKind != "hospital" && record.pawns.Any(IsTryingToLeave))
                {
                    BeginDeparture(map, record, "service lord entered departure state");
                    continue;
                }
                if (Find.TickManager.TicksGame > record.timeoutTick)
                {
                    BeginDeparture(map, record, "service visit timeout");
                }
            }
        }

        private static void BeginDeparture(Map map, ServiceGroupRecord record, string reason)
        {
            if (record == null || record.state == "completed")
            {
                return;
            }
            if (record.reservedPad == null || ReadyForExtraction(record))
            {
                DepartureUtility.CompleteDeparture(map, record, reason);
                return;
            }
            if (record.state != "departing")
            {
                record.state = "departing";
                record.departureRequestedTick = Find.TickManager.TicksGame;
                Log.Message("[Space Services] Routing " + record.serviceKind + " service group " + record.id + " to departure pad: " + reason);
            }
            GuideDepartingPawnsToPad(record);
        }

        private static bool ReadyForExtraction(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.pawns.Count == 0)
            {
                return false;
            }
            IntVec3 cell = record.reservedPad == null ? IntVec3.Invalid : record.reservedPad.Position;
            if (!cell.IsValid)
            {
                return true;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                if (pawn.Spawned && !pawn.Position.InHorDistOf(cell, 3f))
                {
                    return false;
                }
            }
            return true;
        }

        private static void GuideDepartingPawnsToPad(ServiceGroupRecord record)
        {
            if (record == null || record.pawns == null || record.reservedPad == null)
            {
                return;
            }
            IntVec3 cell = record.reservedPad.Position;
            Map map = record.reservedPad.Map;
            if (map == null)
            {
                return;
            }
            foreach (Pawn pawn in record.pawns)
            {
                if (pawn == null || !pawn.Spawned || pawn.Downed || pawn.Position.InHorDistOf(cell, 3f))
                {
                    continue;
                }
                if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                job.locomotionUrgency = LocomotionUrgency.Jog;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
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

    public static class StaleReferenceCleanupUtility
    {
        public static void CleanupAfterLoad(Map map)
        {
            if (map == null)
            {
                return;
            }

            int removedHospitalPatients = CleanupHospitalPatients(map);
            int removedLordPawns = CleanupLordPawnLists(map);
            int removedServicePawns = CleanupServiceGroups(map);

            if (removedHospitalPatients > 0 || removedLordPawns > 0 || removedServicePawns > 0)
            {
                Log.Message("[Space Services] cleaned stale service references: hospitalPatients=" + removedHospitalPatients + ", lordPawns=" + removedLordPawns + ", servicePawns=" + removedServicePawns);
            }
        }

        private static int CleanupHospitalPatients(Map map)
        {
            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            IDictionary patients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            if (patients == null)
            {
                return 0;
            }

            List<object> removeKeys = new List<object>();
            foreach (object key in patients.Keys)
            {
                Pawn pawn = key as Pawn;
                object patientData = key == null ? null : patients[key];
                if (pawn == null || pawn.Destroyed || IsSyntheticFallbackPatientData(patientData))
                {
                    removeKeys.Add(key);
                }
            }

            foreach (object key in removeKeys)
            {
                patients.Remove(key);
            }
            return removeKeys.Count;
        }

        private static bool IsSyntheticFallbackPatientData(object patientData)
        {
            if (patientData == null)
            {
                return false;
            }
            return string.Equals(Reflect.GetMember(patientData, "Diagnosis") as string, "wounds", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Reflect.GetMember(patientData, "Cure") as string, "tend to wounds", StringComparison.OrdinalIgnoreCase);
        }

        private static int CleanupLordPawnLists(Map map)
        {
            if (map.lordManager == null || map.lordManager.lords == null)
            {
                return 0;
            }

            int removed = 0;
            foreach (Lord lord in map.lordManager.lords)
            {
                if (lord == null || lord.ownedPawns == null)
                {
                    continue;
                }
                removed += lord.ownedPawns.RemoveAll(pawn => pawn == null);
            }
            return removed;
        }

        private static int CleanupServiceGroups(Map map)
        {
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null || comp.serviceGroups == null)
            {
                return 0;
            }

            object hospital = HospitalIncidentGate.FindHospitalComponent(map);
            IDictionary hospitalPatients = hospital == null ? null : Reflect.GetMember(hospital, "Patients") as IDictionary;
            int removed = 0;
            foreach (ServiceGroupRecord record in comp.serviceGroups)
            {
                if (record == null || record.pawns == null)
                {
                    continue;
                }
                int before = record.pawns.Count;
                record.pawns = record.pawns.Where(pawn => pawn != null && !pawn.Destroyed).Distinct().ToList();
                removed += before - record.pawns.Count;
                if (record.serviceKind == "hospital" && hospitalPatients != null)
                {
                    before = record.pawns.Count;
                    record.pawns = record.pawns.Where(pawn => hospitalPatients.Contains(pawn)).ToList();
                    removed += before - record.pawns.Count;
                    if (before > 0 && record.pawns.Count == 0)
                    {
                        ReleaseServiceRecord(record);
                        record.state = "completed";
                    }
                }
            }
            return removed;
        }

        private static void ReleaseServiceRecord(ServiceGroupRecord record)
        {
            CompSpaceServicePad pad = record == null || record.reservedPad == null ? null : record.reservedPad.TryGetComp<CompSpaceServicePad>();
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
                return (SpaceServicesMod.Settings == null || SpaceServicesMod.Settings.enableHospital) &&
                    HasRequiredPad(map, ServiceUse.Patient) &&
                    HospitalIncidentGate.CanAcceptHospitalIncident(incidentDefName, map);
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

    public static class HospitalIncidentGate
    {
        public static bool CanAcceptHospitalIncident(string incidentDefName, Map map)
        {
            object hospital = FindHospitalComponent(map);
            if (hospital == null)
            {
                return false;
            }
            if (!CallBool(hospital, "IsOpen", true))
            {
                return false;
            }
            if (incidentDefName == "MassCasualtyEvent" && !Reflect.BoolMember(hospital, "MassCasualties", true))
            {
                return false;
            }
            int freeBeds = CallInt(hospital, "FreeMedicalBeds", -1);
            if (freeBeds == 0 || CallBool(hospital, "IsFull", false))
            {
                return false;
            }
            if (!Reflect.BoolMember(hospital, "AcceptDanger", false) && HospitalDangersOnMap(map))
            {
                return false;
            }
            if (SpaceServiceMapDetector.IsServiceEligible(map) && ServicePadUtility.CountServicePads(map, ServiceUse.Patient) <= 0)
            {
                return false;
            }
            return true;
        }

        public static string ReadinessReport(string incidentDefName, Map map)
        {
            object hospital = FindHospitalComponent(map);
            if (hospital == null)
            {
                return "no HospitalMapComponent";
            }
            return "open=" + CallBool(hospital, "IsOpen", true) +
                ", freeBeds=" + CallInt(hospital, "FreeMedicalBeds", -1) +
                ", bedCount=" + CallInt(hospital, "BedCount", -1) +
                ", full=" + CallBool(hospital, "IsFull", false) +
                ", freePatientPads=" + ServicePadUtility.CountServicePads(map, ServiceUse.Patient) +
                ", massCasualties=" + Reflect.BoolMember(hospital, "MassCasualties", true) +
                ", acceptDanger=" + Reflect.BoolMember(hospital, "AcceptDanger", false) +
                ", danger=" + HospitalDangersOnMap(map);
        }

        public static object FindHospitalComponent(Map map)
        {
            if (map == null || map.components == null)
            {
                return null;
            }
            return map.components.FirstOrDefault(comp => comp != null && (comp.GetType().FullName ?? "") == "Hospital.HospitalMapComponent");
        }

        private static bool HospitalDangersOnMap(Map map)
        {
            Type patientUtility = AccessTools.TypeByName("Hospital.Utilities.PatientUtility");
            MethodInfo method = patientUtility == null ? null : AccessTools.Method(patientUtility, "DangersOnMap");
            if (method == null)
            {
                return GenHostility.AnyHostileActiveThreatToPlayer(map, true);
            }

            object[] args = { map, null };
            try
            {
                object result = method.Invoke(null, args);
                return result is bool && (bool)result;
            }
            catch
            {
                return GenHostility.AnyHostileActiveThreatToPlayer(map, true);
            }
        }

        private static bool CallBool(object target, string methodName, bool fallback)
        {
            MethodInfo method = AccessTools.Method(target.GetType(), methodName);
            if (method == null)
            {
                return fallback;
            }
            try
            {
                object result = method.Invoke(target, null);
                return result is bool ? (bool)result : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static int CallInt(object target, string methodName, int fallback)
        {
            MethodInfo method = AccessTools.Method(target.GetType(), methodName);
            if (method == null)
            {
                return fallback;
            }
            try
            {
                object result = method.Invoke(target, null);
                return result is int ? (int)result : fallback;
            }
            catch
            {
                return fallback;
            }
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
            Type incidentHelper = AccessTools.TypeByName("Hospital.IncidentHelper");
            if (incidentHelper != null)
            {
                PatchIfExists(harmony, AccessTools.Method(incidentHelper, "CanSpawnPatient"), postfix: nameof(OptionalPatchHandlers.HospitalCanSpawnPatientPostfix));
                PatchIfExists(harmony, AccessTools.Method(incidentHelper, "TryFindEntryCell"), postfix: nameof(OptionalPatchHandlers.HospitalTryFindEntryCellPostfix));
                PatchIfExists(harmony, AccessTools.Method(incidentHelper, "SetUpNewPatient"), postfix: nameof(OptionalPatchHandlers.SuitPawnsInArgsPostfix));
            }
            Type patientArrival = AccessTools.TypeByName("Hospital.IncidentWorker_PatientArrives");
            if (patientArrival != null)
            {
                PatchIfExists(harmony, AccessTools.Method(patientArrival, "TryExecuteWorker"), prefix: nameof(OptionalPatchHandlers.HospitalPatientArrivesTryExecutePrefix), postfix: nameof(OptionalPatchHandlers.HospitalPatientArrivesTryExecutePostfix));
                PatchIfExists(harmony, AccessTools.Method(patientArrival, "SpawnPatient"), prefix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPostfix));
            }
            Type massCasualty = AccessTools.TypeByName("Hospital.IncidentWorker_MassCasualtyEvent");
            if (massCasualty != null)
            {
                PatchIfExists(harmony, AccessTools.Method(massCasualty, "TryExecuteWorker"), prefix: nameof(OptionalPatchHandlers.HospitalPatientArrivesTryExecutePrefix));
                foreach (MethodInfo method in massCasualty.GetMethods(AccessTools.all).Where(m => m.Name == "SpawnPatient"))
                {
                    PatchIfExists(harmony, method, prefix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPrefix), postfix: nameof(OptionalPatchHandlers.HospitalSpawnPatientPostfix));
                }
            }
            Type hospitalComponent = AccessTools.TypeByName("Hospital.HospitalMapComponent");
            if (hospitalComponent != null)
            {
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeaves"), postfix: nameof(OptionalPatchHandlers.HospitalPatientDeparturePostfix));
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "DismissPatient"), postfix: nameof(OptionalPatchHandlers.HospitalPatientDeparturePostfix));
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientLeftTheMap"), postfix: nameof(OptionalPatchHandlers.HospitalPatientGonePostfix));
                PatchIfExists(harmony, AccessTools.Method(hospitalComponent, "PatientDied"), postfix: nameof(OptionalPatchHandlers.HospitalPatientGonePostfix));
            }
            PatchIfExists(harmony, AccessTools.Method(typeof(DropPodUtility), "MakeDropPodAt", new[] { typeof(IntVec3), typeof(Map), typeof(ActiveTransporterInfo), typeof(Faction) }), prefix: nameof(OptionalPatchHandlers.HospitalDropPodAtPrefix));
            if (incidentHelper != null || patientArrival != null || massCasualty != null)
            {
                Log.Message("[Space Services] Hospital bridge patches installed.");
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

        public static void HospitalCanSpawnPatientPostfix(Map map, ref bool __result)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            __result = ServiceIncidentUtility.ShouldForceAllow("PatientArrives", map);
        }

        public static bool HospitalPatientArrivesTryExecutePrefix(IncidentParms parms, ref bool __result)
        {
            Map map = parms == null ? null : parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return true;
            }
            if (HospitalIncidentGate.CanAcceptHospitalIncident("PatientArrives", map))
            {
                return true;
            }
            Log.Message("[Space Services] Hospital patient incident blocked: " + HospitalIncidentGate.ReadinessReport("PatientArrives", map));
            __result = false;
            return false;
        }

        public static void HospitalTryFindEntryCellPostfix(Map map, ref IntVec3 cell, ref bool __result)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out IntVec3 serviceCell))
            {
                cell = serviceCell;
                __result = true;
            }
        }

        public static void HospitalSpawnPatientPrefix(object[] __args)
        {
            Map map = FindMap(__args);
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                HospitalLandingRedirectContext.Push(null, IntVec3.Invalid);
                return;
            }

            IntVec3 cell;
            if (!HospitalLandingRedirectContext.TryGetForcedCell(map, out cell) &&
                !ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out cell))
            {
                cell = IntVec3.Invalid;
            }
            foreach (Pawn pawn in PawnsFromArgs(__args))
            {
                VacSuitUtility.SuitPawnForVacuum(pawn);
            }
            HospitalLandingRedirectContext.Push(map, cell);
        }

        public static void HospitalSpawnPatientPostfix(object[] __args)
        {
            try
            {
                Map map = FindMap(__args);
                List<Pawn> pawns = PawnsFromArgs(__args).Distinct().ToList();
                foreach (Pawn pawn in pawns)
                {
                    VacSuitUtility.SuitPawnForVacuum(pawn);
                }
                if (map != null && pawns.Count > 0 && SpaceServiceMapDetector.IsServiceEligible(map))
                {
                    ServiceLifecycleUtility.RegisterPawns(map, "hospital", pawns);
                }
            }
            finally
            {
                HospitalLandingRedirectContext.Pop();
            }
        }

        public static void HospitalDropPodAtPrefix(ref IntVec3 c, Map map, ActiveTransporterInfo info, Faction faction)
        {
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (HospitalLandingRedirectContext.TryGetActiveCell(map, out IntVec3 cell) && cell.IsValid)
            {
                c = cell;
            }
            if (info != null && info.innerContainer != null)
            {
                List<Pawn> pawns = new List<Pawn>();
                foreach (Thing thing in info.innerContainer)
                {
                    Pawn pawn = thing as Pawn;
                    if (pawn != null)
                    {
                        pawns.Add(pawn);
                    }
                }
                VacSuitUtility.SuitPawnsForVacuum(pawns);
            }
        }

        public static void HospitalPatientDeparturePostfix(Pawn pawn)
        {
            ServiceLifecycleUtility.RequestDepartureForPawn(pawn, "Hospital requested patient departure");
        }

        public static void HospitalPatientGonePostfix(Pawn pawn)
        {
            ServiceLifecycleUtility.ReleasePawn(pawn, "Hospital removed patient from map");
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

        public static void HospitalPatientArrivesTryExecutePostfix(object __instance, IncidentParms parms, ref bool __result)
        {
            if (__result || __instance == null || parms == null)
            {
                return;
            }
            Map map = parms.target as Map;
            if (map == null || !SpaceServiceMapDetector.IsServiceEligible(map))
            {
                return;
            }
            if (!HospitalIncidentGate.CanAcceptHospitalIncident("PatientArrives", map))
            {
                Log.Message("[Space Services] Hospital PatientArrives returned false; fallback blocked: " + HospitalIncidentGate.ReadinessReport("PatientArrives", map));
                return;
            }
            try
            {
                __result = HospitalPatientFallback.TryExecutePatientArrival(__instance, parms, map, IntVec3.Invalid);
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Hospital patient fallback failed: " + ex);
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

    public static class HospitalLandingRedirectContext
    {
        private struct Request
        {
            public Map map;
            public IntVec3 cell;
            public bool forced;
        }

        private static readonly Stack<Request> Requests = new Stack<Request>();

        public static void Push(Map map, IntVec3 cell)
        {
            Requests.Push(new Request { map = map, cell = cell, forced = false });
        }

        public static void PushForced(Map map, IntVec3 cell)
        {
            Requests.Push(new Request { map = map, cell = cell, forced = true });
        }

        public static void Pop()
        {
            if (Requests.Count > 0)
            {
                Requests.Pop();
            }
        }

        public static bool TryGetActiveCell(Map map, out IntVec3 cell)
        {
            foreach (Request request in Requests)
            {
                if (request.map == map && request.cell.IsValid)
                {
                    cell = request.cell;
                    return true;
                }
            }
            cell = IntVec3.Invalid;
            return false;
        }

        public static bool TryGetForcedCell(Map map, out IntVec3 cell)
        {
            foreach (Request request in Requests)
            {
                if (request.map == map && request.forced && request.cell.IsValid)
                {
                    cell = request.cell;
                    return true;
                }
            }
            cell = IntVec3.Invalid;
            return false;
        }
    }

    public static class HospitalPatientFallback
    {
        public static bool TryExecutePatientArrival(object worker, IncidentParms parms, Map map, IntVec3 forcedCell)
        {
            worker = ResolvePatientArrivalWorker(worker);
            MethodInfo spawnPatient = worker == null ? null : worker.GetType().GetMethods(AccessTools.all).FirstOrDefault(method =>
                method.Name == "SpawnPatient" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(Map) &&
                method.GetParameters()[1].ParameterType == typeof(Pawn));
            if (worker == null || spawnPatient == null)
            {
                Log.Message("[Space Services] Hospital fallback could not find Hospital.IncidentWorker_PatientArrives.SpawnPatient.");
                return false;
            }

            List<Pawn> pawns = ResolvePatientPawns(worker, parms, map);
            if (pawns.Count == 0)
            {
                Log.Message("[Space Services] Hospital fallback could not generate a patient pawn.");
                return false;
            }

            IntVec3 cell = forcedCell.IsValid ? forcedCell : IntVec3.Invalid;
            if (!cell.IsValid && !ServicePadUtility.TryFindServicePadCell(map, ServiceUse.Patient, out cell))
            {
                cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 12);
            }

            foreach (Pawn pawn in pawns)
            {
                VacSuitUtility.SuitPawnForVacuum(pawn);
                HospitalLandingRedirectContext.PushForced(map, cell);
                try
                {
                    spawnPatient.Invoke(worker, new object[] { map, pawn });
                }
                finally
                {
                    HospitalLandingRedirectContext.Pop();
                }
            }

            TryCreateHospitalLord(worker, parms, map, pawns);
            ServiceLifecycleUtility.RegisterPawns(map, "hospital", pawns);
            SendPatientArrivalNotice(pawns[0]);
            Log.Message("[Space Services] Hospital fallback ran real patient arrival pawns=" + pawns.Count);
            return true;
        }

        private static object ResolvePatientArrivalWorker(object worker)
        {
            if (worker != null)
            {
                return worker;
            }
            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("PatientArrives");
            worker = def == null ? null : def.Worker;
            if (worker != null)
            {
                return worker;
            }
            Type type = AccessTools.TypeByName("Hospital.IncidentWorker_PatientArrives");
            return type == null ? null : Activator.CreateInstance(type);
        }

        private static List<Pawn> ResolvePatientPawns(object worker, IncidentParms parms, Map map)
        {
            Faction faction = parms.faction ?? Find.FactionManager.FirstFactionOfDef(FactionDefOf.OutlanderCivil);
            parms.faction = faction;
            Type helper = AccessTools.TypeByName("Hospital.IncidentHelper");
            MethodInfo generatePawn = helper == null ? null : AccessTools.Method(helper, "GeneratePawn", new[] { typeof(Faction) });
            Pawn pawn = null;
            if (generatePawn != null)
            {
                try
                {
                    pawn = generatePawn.Invoke(null, new object[] { faction }) as Pawn;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Space Services] Hospital fallback could not use IncidentHelper.GeneratePawn: " + ex.Message);
                }
            }
            if (pawn == null)
            {
                PawnKindDef kind = parms.pawnKind ?? PawnKindDefOf.Villager;
                pawn = PawnGenerator.GeneratePawn(kind, faction, map.Tile);
            }
            return pawn == null ? new List<Pawn>() : new List<Pawn> { pawn };
        }

        private static void TryCreateHospitalLord(object worker, IncidentParms parms, Map map, List<Pawn> pawns)
        {
            if (worker == null || parms == null || map == null || pawns == null || pawns.Count == 0)
            {
                return;
            }
            MethodInfo createLordJob = worker.GetType().GetMethods(AccessTools.all).FirstOrDefault(method =>
                method.Name == "CreateLordJob" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(IncidentParms));
            if (createLordJob == null)
            {
                return;
            }
            try
            {
                LordJob lordJob = createLordJob.Invoke(worker, new object[] { parms, pawns }) as LordJob;
                if (lordJob != null)
                {
                    LordMaker.MakeNewLord(parms.faction, lordJob, map, pawns);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Hospital fallback could not create patient lord: " + ex.Message);
            }
        }

        private static void SendPatientArrivalNotice(Pawn pawn)
        {
            Messages.Message("Space Services: patient arrived", pawn, MessageTypeDefOf.NeutralEvent, false);
        }
    }

    public static class ServiceDebugUtility
    {
        public static IncidentParms PatientArrivalParms(Map map)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
            parms.target = map;
            parms.pawnKind = PawnKindDefOf.Villager;
            parms.faction = Find.FactionManager.FirstFactionOfDef(FactionDefOf.OutlanderCivil);
            return parms;
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
            return BoolMember(obj, name, false);
        }

        public static bool BoolMember(object obj, string name, bool fallback)
        {
            object value = GetMember(obj, name);
            return value is bool ? (bool)value : fallback;
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

        public static void SetMember(object obj, string name, object value)
        {
            if (obj == null || string.IsNullOrEmpty(name))
            {
                return;
            }
            Type type = obj.GetType();
            PropertyInfo property = AccessTools.Property(type, name);
            if (property != null && property.CanWrite)
            {
                property.SetValue(obj, value, null);
                return;
            }
            FieldInfo field = AccessTools.Field(type, name);
            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }
    }
}
