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
    public class Building_ServiceLandingPad : Building
    {
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map?.GetComponent<SpaceServicesMapComponent>()?.DirtyServicePadCache();
            ServicePadPrebuildModeUtility.ApplyPendingMode(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map oldMap = Map;
            base.DeSpawn(mode);
            oldMap?.GetComponent<SpaceServicesMapComponent>()?.DirtyServicePadCache();
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            GetComp<CompSpaceServicePad>()?.DrawModeOverlay(drawLoc);
        }
    }

    public class CompProperties_SpaceServicePad : CompProperties
    {
        public CompProperties_SpaceServicePad()
        {
            compClass = typeof(CompSpaceServicePad);
        }
    }

    [StaticConstructorOnStartup]
    public class CompSpaceServicePad : ThingComp
    {
        private const float OverlayDrawSize = 1.55f;
        private static readonly Vector2 OverlaySize = new Vector2(OverlayDrawSize, OverlayDrawSize);
        private static readonly Color OverlayTint = new Color(0.72f, 0.78f, 0.8f, 0.62f);
        private static readonly Material HospitalOverlayMat = MaterialPool.MatFrom("Things/Building/SpaceServices/ServiceLandingPad_Hospital", ShaderDatabase.Transparent, OverlayTint);
        private static readonly Material HospitalityOverlayMat = MaterialPool.MatFrom("Things/Building/SpaceServices/ServiceLandingPad_Hospitality", ShaderDatabase.Transparent, OverlayTint);
        private static readonly Texture2D SharedIcon = ContentFinder<Texture2D>.Get("Things/Building/SpaceServices/ServiceLandingPad");
        private static readonly Texture2D HospitalIcon = ContentFinder<Texture2D>.Get("Things/Building/SpaceServices/ServiceLandingPad_Hospital");
        private static readonly Texture2D HospitalityIcon = ContentFinder<Texture2D>.Get("Things/Building/SpaceServices/ServiceLandingPad_Hospitality");

        public ServicePadMode activeMode = ServicePadMode.Shared;
        private bool legacyAllowGuests = true;
        private bool legacyAllowPatients = true;
        private bool legacyAllowEmergency = true;
        private int modeVersion = 2;
        public bool requireVacSafeRoof = false;
        public bool preferShuttle = true;
        public bool requirePower = true;
        public string reservedForGroup;
        public int reservedAtTick;

        public override void PostExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                modeVersion = 3;
                SyncLegacyModeFlags();
            }

            Scribe_Values.Look(ref modeVersion, "modeVersion", 0);
            Scribe_Values.Look(ref activeMode, "activeMode", ServicePadMode.Shared);
            Scribe_Values.Look(ref legacyAllowGuests, "allowGuests", true);
            Scribe_Values.Look(ref legacyAllowPatients, "allowPatients", true);
            Scribe_Values.Look(ref legacyAllowEmergency, "allowEmergency", true);
            Scribe_Values.Look(ref requireVacSafeRoof, "requireVacSafeRoof", false);
            Scribe_Values.Look(ref preferShuttle, "preferShuttle", true);
            Scribe_Values.Look(ref requirePower, "requirePower", false);
            Scribe_Values.Look(ref reservedForGroup, "reservedForGroup");
            Scribe_Values.Look(ref reservedAtTick, "reservedAtTick", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (modeVersion < 2)
                {
                    MigrateLegacyModeFlags();
                    modeVersion = 3;
                }
            }
        }

        public bool IsUsableFor(ServiceUse use)
        {
            if (!string.IsNullOrEmpty(reservedForGroup))
            {
                return false;
            }
            return MeetsUseRequirements(use);
        }

        public bool MeetsUseRequirements(ServiceUse use)
        {
            string reason;
            return MeetsUseRequirements(use, out reason);
        }

        public bool MeetsUseRequirements(ServiceUse use, out string reason)
        {
            reason = null;
            if (parent == null || parent.Destroyed || parent.Map == null)
            {
                reason = "pad unavailable";
                return false;
            }
            if (!HasRequiredPower(out reason))
            {
                return false;
            }
            if (!AllowsUse(use))
            {
                reason = "pad mode is " + ModeLabel();
                return false;
            }
            if (!ServiceEnvironmentUtility.IsRoofAccessible(parent, out reason))
            {
                return false;
            }
            return true;
        }

        public bool MeetsOperationalRequirements(out string reason)
        {
            reason = null;
            if (parent == null || parent.Destroyed || parent.Map == null)
            {
                reason = "pad unavailable";
                return false;
            }
            if (!HasRequiredPower(out reason))
            {
                return false;
            }
            if (!ServiceEnvironmentUtility.IsRoofAccessible(parent, out reason))
            {
                return false;
            }
            return true;
        }

        private bool HasRequiredPower(out string reason)
        {
            reason = null;
            CompPowerTrader power = parent == null ? null : parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                reason = "power is off";
                return false;
            }
            return true;
        }

        public override string CompInspectStringExtra()
        {
            if (parent == null || parent.Map == null)
            {
                return null;
            }

            try
            {
                StringBuilder builder = new StringBuilder();
                SpaceServiceEligibility eligibility = SafeEligibility(parent.Map);
                float vacuum = SafeMaxVacuum();
                bool roofAccessible = SafeRoofAccessible(out string roofReason);
                bool usable = SafeGenerallyUsable(out string usableReason);
                SpaceServicesMapComponent comp = parent.Map.GetComponent<SpaceServicesMapComponent>();

                builder.AppendLine("Space Services");
                builder.AppendLine("Mode: " + ModeLabel());
                builder.AppendLine("Reservation: " + ReservationLabel());
                if (comp != null && comp.debugForceHospitalityDanger)
                {
                    builder.AppendLine("Debug danger: forced");
                }
                builder.AppendLine("Damage: immune");
                builder.AppendLine("Vacuum exposed: " + (vacuum > 0.001f ? "yes (" + vacuum.ToStringPercent() + ")" : "no"));
                builder.AppendLine("Roof accessible: " + (roofAccessible ? "yes, " + SafeRoofAccessReport() : "no, " + roofReason));
                builder.AppendLine("Map: " + (eligibility.allowed ? "eligible" : "blocked"));
                builder.Append("Overall: " + (usable ? "usable" : "not usable, " + usableReason));
                return builder.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                Log.Warning("[Space Services] Service pad inspect failed: " + ex.Message);
                return "Space Services\nStatus unavailable; see log.";
            }
        }

        private SpaceServiceEligibility SafeEligibility(Map map)
        {
            try
            {
                return SpaceServiceMapDetector.Evaluate(map);
            }
            catch (Exception ex)
            {
                SpaceServiceEligibility fallback = new SpaceServiceEligibility();
                fallback.blockReasons.Add("map check failed: " + ex.Message);
                return fallback;
            }
        }

        private float SafeMaxVacuum()
        {
            try
            {
                return ServiceEnvironmentUtility.GetMaxVacuum(parent);
            }
            catch
            {
                return 0f;
            }
        }

        private bool SafeRoofAccessible(out string reason)
        {
            try
            {
                return ServiceEnvironmentUtility.IsRoofAccessible(parent, out reason);
            }
            catch (Exception ex)
            {
                reason = "roof check failed: " + ex.Message;
                return false;
            }
        }

        private bool SafeFullFlyThroughRoof(out string reason)
        {
            try
            {
                return ServiceEnvironmentUtility.HasFullFlyThroughRoof(parent, out reason);
            }
            catch (Exception ex)
            {
                reason = "roof check failed: " + ex.Message;
                return false;
            }
        }

        private string SafeRoofAccessReport()
        {
            try
            {
                return ServiceEnvironmentUtility.RoofAccessReport(parent);
            }
            catch (Exception ex)
            {
                return "roof report failed: " + ex.Message;
            }
        }

        private bool SafeGenerallyUsable(out string reason)
        {
            try
            {
                return IsGenerallyUsable(out reason);
            }
            catch (Exception ex)
            {
                reason = "status check failed: " + ex.Message;
                return false;
            }
        }

        private bool IsGenerallyUsable(out string reason)
        {
            if (!SpaceServiceMapDetector.IsServiceEligible(parent.Map))
            {
                reason = "map is not space-service eligible";
                return false;
            }
            if (!string.IsNullOrEmpty(reservedForGroup))
            {
                reason = "reserved";
                return false;
            }
            if (CanServeAnyActiveUse(out string modeReason))
            {
                reason = null;
                return true;
            }
            reason = modeReason ?? "service settings block " + ModeLabel() + " mode";
            return false;
        }

        private bool CanServeAnyActiveUse(out string reason)
        {
            reason = null;
            if (AllowsUse(ServiceUse.Patient) && MeetsUseRequirements(ServiceUse.Patient, out reason))
            {
                return true;
            }
            if (AllowsUse(ServiceUse.Guest) && MeetsUseRequirements(ServiceUse.Guest, out reason))
            {
                return true;
            }
            return false;
        }

        private string ModeLabel()
        {
            if (activeMode == ServicePadMode.HospitalOnly)
            {
                return "hospital only";
            }
            if (activeMode == ServicePadMode.HospitalityOnly)
            {
                return "hospitality only";
            }
            if (activeMode == ServicePadMode.Shared)
            {
                return "shared";
            }
            if (activeMode == ServicePadMode.HospitalPriority)
            {
                return "hospital priority";
            }
            if (activeMode == ServicePadMode.HospitalityPriority)
            {
                return "hospitality priority";
            }
            return activeMode.ToString();
        }

        public bool TryReserve(string groupId)
        {
            if (string.IsNullOrEmpty(reservedForGroup))
            {
                reservedForGroup = groupId;
                reservedAtTick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                return true;
            }
            if (reservedForGroup == groupId)
            {
                if (reservedAtTick <= 0 && Find.TickManager != null)
                {
                    reservedAtTick = Find.TickManager.TicksGame;
                }
                return true;
            }
            return false;
        }

        public void Release(string groupId)
        {
            if (reservedForGroup == groupId)
            {
                reservedForGroup = null;
                reservedAtTick = 0;
            }
        }

        public void ForceRelease()
        {
            reservedForGroup = null;
            reservedAtTick = 0;
        }

        public bool WatchReservation(List<ServiceGroupRecord> records)
        {
            if (string.IsNullOrEmpty(reservedForGroup) || parent == null || parent.Destroyed || parent.Map == null)
            {
                return false;
            }
            if (!IsWatchdogEligible())
            {
                return false;
            }
            if (reservedAtTick <= 0)
            {
                reservedAtTick = Find.TickManager.TicksGame;
                return false;
            }
            if (Find.TickManager.TicksGame - reservedAtTick < ServicePadUtility.ReservationWatchdogTicks)
            {
                return false;
            }

            ServiceGroupRecord record = records == null ? null : records.FirstOrDefault(group => group != null && group.id == reservedForGroup);
            if (record == null)
            {
                ClearWatchedReservation("no matching service record");
                return true;
            }
            if (record.state == "completed")
            {
                ClearWatchedReservation("service record completed");
                return true;
            }
            if (record.reservedPad != null && record.reservedPad != parent)
            {
                ClearWatchedReservation("service record moved to another pad");
                return true;
            }
            if (record.pawns == null || !record.pawns.Any(pawn => !ServicePawnUtility.IsTerminalPawn(pawn) && (pawn.Spawned || pawn.MapHeld != null)))
            {
                record.state = "completed";
                ClearWatchedReservation("service record has no active pawns");
                return true;
            }
            if (record.serviceKind != "hospital")
            {
                ServiceLifecycleUtility.ClearGroupReservation(parent.Map, record.id, "pad reservation watchdog timeout");
                return true;
            }

            ServiceDebugUtility.LogThrottled("watchdog-kept-hospital-" + reservedForGroup, "Space Services reservation watchdog left hospital reservation intact: " + reservedForGroup, GenDate.TicksPerDay);
            return false;
        }

        private bool IsWatchdogEligible()
        {
            return CanServeAnyActiveUse(out _);
        }

        private void ClearWatchedReservation(string reason)
        {
            string oldReservation = reservedForGroup;
            ForceRelease();
            Log.Warning("[Space Services] Cleared stale service pad reservation " + oldReservation + " at " + parent.Position + ": " + reason);
        }

        private string ReservationLabel()
        {
            if (string.IsNullOrEmpty(reservedForGroup))
            {
                return "free";
            }
            if (reservedAtTick <= 0 || Find.TickManager == null)
            {
                return "reserved";
            }
            int ticks = Math.Max(0, Find.TickManager.TicksGame - reservedAtTick);
            return "reserved for " + GenDate.ToStringTicksToPeriod(ticks);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }
            foreach (Gizmo gizmo in ModeCommands(() => activeMode, mode => activeMode = mode))
            {
                yield return gizmo;
            }
            if (ShouldShowDevGizmos())
            {
                yield return DebugDangerToggle();
                yield return new Command_Action
                {
                    defaultLabel = "JDB_SpaceServices_Gizmo_ReportMap".Translate(),
                    defaultDesc = "JDB_SpaceServices_Gizmo_ReportMapDesc".Translate(),
                    action = delegate
                    {
                        SpaceServiceEligibility eligibility = SpaceServiceMapDetector.Evaluate(parent.MapHeld);
                        ServiceDebugUtility.Log(ServiceLogIntegration.Core, eligibility.ToLogString(parent.MapHeld));
                        Messages.Message(eligibility.allowed ? "Space Services: map eligible" : "Space Services: map blocked", parent, MessageTypeDefOf.NeutralEvent, false);
                    }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Clear service reservation",
                    action = delegate
                    {
                        string oldReservation = reservedForGroup;
                        ForceRelease();
                        ServiceLifecycleUtility.ClearGroupReservation(parent.MapHeld, oldReservation, "dev cleared pad reservation");
                        Messages.Message("Space Services: pad reservation cleared", parent, MessageTypeDefOf.NeutralEvent, false);
                    }
                };
            }
        }

        public void DrawModeOverlay(Vector3 baseDrawPos)
        {
            Material overlay = CurrentOverlayMaterial();
            if (overlay == null)
            {
                return;
            }
            // Keep the mode mark painted on the pad surface instead of floating over shuttles.
            Vector3 drawPos = baseDrawPos;
            drawPos.y = AltitudeLayer.FloorEmplacement.AltitudeFor() + 0.03f;
            Graphics.DrawMesh(MeshPool.GridPlane(OverlaySize), drawPos, Quaternion.identity, overlay, 0);
        }

        private Material CurrentOverlayMaterial()
        {
            if (Prioritizes(ServiceUse.Patient))
            {
                return HospitalOverlayMat;
            }
            if (Prioritizes(ServiceUse.Guest))
            {
                return HospitalityOverlayMat;
            }
            return null;
        }

        public bool AllowsUse(ServiceUse use)
        {
            if (use == ServiceUse.Patient)
            {
                return activeMode == ServicePadMode.HospitalOnly ||
                    activeMode == ServicePadMode.Shared ||
                    activeMode == ServicePadMode.HospitalPriority ||
                    activeMode == ServicePadMode.HospitalityPriority;
            }
            if (use == ServiceUse.Guest)
            {
                return activeMode == ServicePadMode.HospitalityOnly ||
                    activeMode == ServicePadMode.Shared ||
                    activeMode == ServicePadMode.HospitalPriority ||
                    activeMode == ServicePadMode.HospitalityPriority;
            }
            return false;
        }

        public bool Prioritizes(ServiceUse use)
        {
            if (!AllowsUse(use))
            {
                return false;
            }
            if (activeMode == ServicePadMode.HospitalOnly || activeMode == ServicePadMode.HospitalityOnly)
            {
                return true;
            }
            if (activeMode == ServicePadMode.Shared)
            {
                return false;
            }
            return (use == ServiceUse.Patient && activeMode == ServicePadMode.HospitalPriority) ||
                (use == ServiceUse.Guest && activeMode == ServicePadMode.HospitalityPriority);
        }

        public bool AllowsFullRate(ServiceUse use)
        {
            return AllowsUse(use) && (activeMode == ServicePadMode.Shared || Prioritizes(use));
        }

        public int PriorityRank(ServiceUse use)
        {
            if (!AllowsUse(use))
            {
                return 99;
            }
            if (activeMode == ServicePadMode.HospitalOnly || activeMode == ServicePadMode.HospitalityOnly)
            {
                return 0;
            }
            if (Prioritizes(use))
            {
                return 1;
            }
            if (activeMode == ServicePadMode.Shared)
            {
                return 2;
            }
            return 3;
        }

        private void MigrateLegacyModeFlags()
        {
            if (activeMode == ServicePadMode.Hospital)
            {
                activeMode = ServicePadMode.HospitalOnly;
            }
            else if (activeMode == ServicePadMode.Hospitality)
            {
                activeMode = ServicePadMode.HospitalityOnly;
            }
            else if (activeMode == ServicePadMode.Trade)
            {
                activeMode = ServicePadMode.HospitalityOnly;
            }
            else if (legacyAllowGuests && !legacyAllowPatients)
            {
                activeMode = ServicePadMode.HospitalityOnly;
            }
            else if (legacyAllowEmergency && !legacyAllowGuests && !legacyAllowPatients)
            {
                activeMode = ServicePadMode.HospitalityOnly;
            }
            else if (legacyAllowGuests && legacyAllowPatients)
            {
                activeMode = ServicePadMode.Shared;
            }
            else
            {
                activeMode = ServicePadMode.HospitalOnly;
            }
            SyncLegacyModeFlags();
        }

        private void SyncLegacyModeFlags()
        {
            legacyAllowPatients = AllowsUse(ServiceUse.Patient);
            legacyAllowGuests = AllowsUse(ServiceUse.Guest);
            legacyAllowEmergency = false;
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

        public static IEnumerable<Gizmo> ModeCommands(Func<ServicePadMode> getter, Action<ServicePadMode> setter)
        {
            yield return ModeCommand(ServicePadMode.HospitalOnly, "JDB_SpaceServices_Gizmo_ModeHospitalOnly", "JDB_SpaceServices_Gizmo_ModeHospitalOnlyDesc", HospitalIcon, getter, setter);
            yield return ModeCommand(ServicePadMode.HospitalityOnly, "JDB_SpaceServices_Gizmo_ModeHospitalityOnly", "JDB_SpaceServices_Gizmo_ModeHospitalityOnlyDesc", HospitalityIcon, getter, setter);
            yield return ModeCommand(ServicePadMode.Shared, "JDB_SpaceServices_Gizmo_ModeShared", "JDB_SpaceServices_Gizmo_ModeSharedDesc", SharedIcon, getter, setter);
            yield return ModeCommand(ServicePadMode.HospitalPriority, "JDB_SpaceServices_Gizmo_ModeHospitalPriority", "JDB_SpaceServices_Gizmo_ModeHospitalPriorityDesc", HospitalIcon, getter, setter);
            yield return ModeCommand(ServicePadMode.HospitalityPriority, "JDB_SpaceServices_Gizmo_ModeHospitalityPriority", "JDB_SpaceServices_Gizmo_ModeHospitalityPriorityDesc", HospitalityIcon, getter, setter);
        }

        private static Command_Toggle ModeCommand(ServicePadMode mode, string labelKey, string descKey, Texture2D icon, Func<ServicePadMode> getter, Action<ServicePadMode> setter)
        {
            return new Command_Toggle
            {
                defaultLabel = labelKey.Translate(),
                defaultDesc = descKey.Translate(),
                icon = icon,
                isActive = () => getter() == mode,
                toggleAction = delegate { setter(mode); }
            };
        }

        private static bool ShouldShowDevGizmos()
        {
            return DebugSettings.godMode;
        }

        private Command_Toggle DebugDangerToggle()
        {
            return new Command_Toggle
            {
                defaultLabel = "DEV: Force Hospitality danger",
                defaultDesc = "Pretend this map has an active Hospitality traffic threat so arrivals wave off and departures wait.",
                isActive = delegate
                {
                    SpaceServicesMapComponent comp = parent == null || parent.MapHeld == null ? null : parent.MapHeld.GetComponent<SpaceServicesMapComponent>();
                    return comp != null && comp.debugForceHospitalityDanger;
                },
                toggleAction = delegate
                {
                    SpaceServicesMapComponent comp = parent == null || parent.MapHeld == null ? null : parent.MapHeld.GetComponent<SpaceServicesMapComponent>();
                    if (comp != null)
                    {
                        comp.debugForceHospitalityDanger = !comp.debugForceHospitalityDanger;
                        Messages.Message("Space Services: debug Hospitality danger " + (comp.debugForceHospitalityDanger ? "on" : "off"), parent, MessageTypeDefOf.NeutralEvent, false);
                    }
                }
            };
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    public static class SpaceServicePadDamagePatch
    {
        public static bool Prefix(Thing __instance, ref DamageWorker.DamageResult __result)
        {
            if (__instance != null && __instance.TryGetComp<CompSpaceServicePad>() != null)
            {
                __result = new DamageWorker.DamageResult();
                return false;
            }
            return true;
        }
    }

    public enum ServiceUse
    {
        Guest,
        Patient
    }

    public enum ServicePadMode
    {
        HospitalOnly,
        HospitalityOnly,
        Shared,
        HospitalPriority,
        HospitalityPriority,
        Hospital,
        Hospitality,
        Trade
    }

    public static class ServicePadUtility
    {
        public const int ReservationWatchdogTicks = GenDate.TicksPerDay * 2;

        public static IEnumerable<Thing> AllServicePadBuildings(Map map)
        {
            if (map == null)
            {
                yield break;
            }
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            List<Thing> cachedPads = comp == null ? null : comp.CachedServicePadBuildings();
            if (cachedPads != null)
            {
                foreach (Thing pad in cachedPads)
                {
                    yield return pad;
                }
                yield break;
            }
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (building != null && building.TryGetComp<CompSpaceServicePad>() != null)
                {
                    yield return building;
                }
            }
        }

        public static IEnumerable<Thing> AllServicePads(Map map, ServiceUse use)
        {
            if (map == null)
            {
                yield break;
            }
            for (int rank = 0; rank <= 99; rank++)
            {
                bool yieldedAny = false;
                foreach (Thing building in AllServicePadBuildings(map))
                {
                    CompSpaceServicePad comp = building.TryGetComp<CompSpaceServicePad>();
                    if (comp != null && comp.PriorityRank(use) == rank && comp.IsUsableFor(use))
                    {
                        yieldedAny = true;
                        yield return building;
                    }
                }
                if (rank >= 3 && !yieldedAny)
                {
                    yield break;
                }
            }
        }

        public static bool TryFindServicePadCell(Map map, ServiceUse use, out IntVec3 cell)
        {
            Thing pad = TryFindRandomServicePad(map, use);
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

        public static Thing TryFindRandomServicePad(Map map, ServiceUse use)
        {
            List<Thing> bestPads = new List<Thing>();
            int bestRank = 100;
            foreach (Thing pad in AllServicePadBuildings(map))
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp == null || !comp.IsUsableFor(use))
                {
                    continue;
                }
                int rank = comp.PriorityRank(use);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    bestPads.Clear();
                }
                if (rank == bestRank)
                {
                    bestPads.Add(pad);
                }
            }
            if (bestPads.Count == 0)
            {
                return null;
            }
            return bestPads[Rand.Range(0, bestPads.Count)];
        }

        public static bool TryFindNearestServicePadCell(Map map, ServiceUse use, IntVec3 origin, out IntVec3 cell)
        {
            Thing pad = TryFindNearestServicePad(map, use, origin);
            if (pad != null)
            {
                cell = pad.Position;
                return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }

        public static Thing TryFindNearestServicePad(Map map, ServiceUse use, IntVec3 origin)
        {
            if (!origin.IsValid)
            {
                return TryFindRandomServicePad(map, use);
            }
            Thing best = null;
            int bestRank = 100;
            float bestDistance = float.MaxValue;
            foreach (Thing pad in AllServicePadBuildings(map))
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp == null || !comp.IsUsableFor(use))
                {
                    continue;
                }
                int rank = comp.PriorityRank(use);
                float distance = pad.Position.DistanceToSquared(origin);
                if (rank < bestRank || (rank == bestRank && distance < bestDistance))
                {
                    best = pad;
                    bestRank = rank;
                    bestDistance = distance;
                }
            }
            return best;
        }

        public static Thing TryReserveServicePad(Map map, ServiceUse use, string groupId)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                return null;
            }
            foreach (Thing pad in AllServicePads(map, use))
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                if (comp != null && comp.TryReserve(groupId))
                {
                    return pad;
                }
            }
            return null;
        }

        public static int CountServicePads(Map map, ServiceUse use)
        {
            return AllServicePads(map, use).Count();
        }

        public static bool PriorityThrottleAllows(Map map, ServiceUse use, out string reason)
        {
            List<Thing> pads = AllServicePads(map, use).ToList();
            if (pads.Count == 0)
            {
                reason = "no usable " + use + " service pad";
                return false;
            }
            if (pads.Any(pad =>
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                return comp != null && comp.AllowsFullRate(use);
            }))
            {
                reason = null;
                return true;
            }
            if (Rand.Chance(0.25f))
            {
                reason = null;
                return true;
            }
            reason = "only low-priority shared service pads available";
            return false;
        }

        public static string PriorityReadinessReport(Map map, ServiceUse use)
        {
            List<Thing> pads = AllServicePads(map, use).ToList();
            if (pads.Count == 0)
            {
                return "no usable " + use + " service pad";
            }
            bool hasPriority = pads.Any(pad =>
            {
                CompSpaceServicePad comp = pad.TryGetComp<CompSpaceServicePad>();
                return comp != null && comp.AllowsFullRate(use);
            });
            return hasPriority ? "priority or shared pad available" : "only low-priority shared service pads available";
        }
    }
}
