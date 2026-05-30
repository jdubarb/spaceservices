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
        private static readonly Graphic HospitalOverlay = GraphicDatabase.Get<Graphic_Single>("Things/Building/SpaceServices/ServiceLandingPad_Hospital", ShaderDatabase.Cutout, OverlaySize, Color.white);
        private static readonly Graphic HospitalityOverlay = GraphicDatabase.Get<Graphic_Single>("Things/Building/SpaceServices/ServiceLandingPad_Hospitality", ShaderDatabase.Cutout, OverlaySize, Color.white);
        private static readonly Graphic TradeOverlay = GraphicDatabase.Get<Graphic_Single>("Things/Building/SpaceServices/ServiceLandingPad_Trade", ShaderDatabase.Cutout, OverlaySize, Color.white);

        public bool allowGuests = true;
        public bool allowPatients = true;
        public bool allowEmergency = true;
        public bool requireVacSafeRoof = false;
        public bool preferShuttle = true;
        public bool requirePower = false;
        public string reservedForGroup;
        public int reservedAtTick;

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref allowGuests, "allowGuests", true);
            Scribe_Values.Look(ref allowPatients, "allowPatients", true);
            Scribe_Values.Look(ref allowEmergency, "allowEmergency", true);
            Scribe_Values.Look(ref requireVacSafeRoof, "requireVacSafeRoof", false);
            Scribe_Values.Look(ref preferShuttle, "preferShuttle", true);
            Scribe_Values.Look(ref requirePower, "requirePower", false);
            Scribe_Values.Look(ref reservedForGroup, "reservedForGroup");
            Scribe_Values.Look(ref reservedAtTick, "reservedAtTick", 0);
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
            if (requirePower)
            {
                CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn)
                {
                    reason = "power is off";
                    return false;
                }
            }
            if (use == ServiceUse.Patient && !allowPatients)
            {
                reason = "patients are disabled";
                return false;
            }
            if (use == ServiceUse.Guest && !allowGuests)
            {
                reason = "guests are disabled";
                return false;
            }
            if (use == ServiceUse.Emergency && !allowEmergency)
            {
                reason = "emergency arrivals are disabled";
                return false;
            }
            if (!ServiceEnvironmentUtility.IsRoofAccessible(parent, out reason))
            {
                return false;
            }
            if (requireVacSafeRoof && !ServiceEnvironmentUtility.HasFullFlyThroughRoof(parent, out reason))
            {
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
                string vacRoofReason;
                bool vacRoofOk = !requireVacSafeRoof || SafeFullFlyThroughRoof(out vacRoofReason);
                bool usable = SafeGenerallyUsable(out string usableReason);

                builder.AppendLine("Space Services");
                builder.AppendLine("Mode: " + ModeLabel());
                builder.AppendLine("Reservation: " + ReservationLabel());
                builder.AppendLine("Damage: immune");
                builder.AppendLine("Vacuum exposed: " + (vacuum > 0.001f ? "yes (" + vacuum.ToStringPercent() + ")" : "no"));
                builder.AppendLine("Roof accessible: " + (roofAccessible ? "yes, " + SafeRoofAccessReport() : "no, " + roofReason));
                if (requireVacSafeRoof)
                {
                    builder.AppendLine("Vac-safe roof required: " + (vacRoofOk ? "satisfied" : "not satisfied"));
                }
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
            if (!allowGuests && !allowPatients && !allowEmergency)
            {
                reason = "all service modes disabled";
                return false;
            }

            string firstReason = null;
            string modeReason = null;
            if (allowPatients && MeetsUseRequirements(ServiceUse.Patient, out modeReason))
            {
                reason = null;
                return true;
            }
            firstReason = firstReason ?? modeReason;
            if (allowGuests && MeetsUseRequirements(ServiceUse.Guest, out modeReason))
            {
                reason = null;
                return true;
            }
            firstReason = firstReason ?? modeReason;
            if (allowEmergency && MeetsUseRequirements(ServiceUse.Emergency, out modeReason))
            {
                reason = null;
                return true;
            }
            reason = firstReason ?? "service settings block all modes";
            return false;
        }

        private string ModeLabel()
        {
            List<string> modes = new List<string>();
            if (allowPatients)
            {
                modes.Add("patients");
            }
            if (allowGuests)
            {
                modes.Add("guests");
            }
            if (allowEmergency)
            {
                modes.Add("emergency");
            }
            return modes.Count == 0 ? "disabled" : string.Join(", ", modes.ToArray());
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
            if (record.pawns == null || !record.pawns.Any(pawn => pawn != null && !pawn.Destroyed && (pawn.Spawned || pawn.MapHeld != null)))
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
            return (allowGuests && MeetsUseRequirements(ServiceUse.Guest)) ||
                (allowPatients && MeetsUseRequirements(ServiceUse.Patient)) ||
                (allowEmergency && MeetsUseRequirements(ServiceUse.Emergency));
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
                        ServiceLifecycleUtility.ClearGroupReservation(parent.MapHeld, oldReservation, "dev cleared pad reservation");
                        Messages.Message("Space Services: pad reservation cleared", parent, MessageTypeDefOf.NeutralEvent, false);
                    }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Spawn patient here",
                    action = delegate
                    {
                        if (!MeetsUseRequirements(ServiceUse.Patient, out string reason))
                        {
                            Messages.Message("Space Services: dev patient spawn blocked, " + reason, parent, MessageTypeDefOf.RejectInput, false);
                            return;
                        }
                        bool spawned = HospitalPatientFallback.TryExecutePatientArrival(null, ServiceDebugUtility.PatientArrivalParms(parent.MapHeld), parent.MapHeld, parent.Position);
                        Messages.Message(spawned ? "Space Services: dev patient spawned" : "Space Services: dev patient spawn failed", parent, MessageTypeDefOf.NeutralEvent, false);
                    }
                };
            }
        }

        public override void PostDraw()
        {
            base.PostDraw();
            Graphic overlay = CurrentOverlay();
            if (overlay == null || parent == null)
            {
                return;
            }
            Vector3 drawPos = parent.DrawPos;
            drawPos.y = AltitudeLayer.MetaOverlays.AltitudeFor() + 0.15f;
            overlay.Draw(drawPos, Rot4.North, parent);
        }

        private Graphic CurrentOverlay()
        {
            if (allowPatients && !allowGuests)
            {
                return HospitalOverlay;
            }
            if (allowGuests && !allowPatients)
            {
                return HospitalityOverlay;
            }
            if (!allowGuests && !allowPatients && allowEmergency)
            {
                return TradeOverlay;
            }
            return null;
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
        Patient,
        Emergency
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
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (building.TryGetComp<CompSpaceServicePad>() != null)
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
            foreach (Thing building in AllServicePadBuildings(map))
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
            List<Thing> pads = AllServicePads(map, use).ToList();
            if (pads.Count == 0)
            {
                return null;
            }
            return pads[Rand.Range(0, pads.Count)];
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
    }
}
