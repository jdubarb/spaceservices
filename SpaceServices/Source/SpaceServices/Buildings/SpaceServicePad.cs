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

            StringBuilder builder = new StringBuilder();
            SpaceServiceEligibility eligibility = SpaceServiceMapDetector.Evaluate(parent.Map);
            float vacuum = ServiceEnvironmentUtility.GetMaxVacuum(parent);
            bool roofAccessible = ServiceEnvironmentUtility.IsRoofAccessible(parent, out string roofReason);
            string vacRoofReason;
            bool vacRoofOk = !requireVacSafeRoof || ServiceEnvironmentUtility.HasFullFlyThroughRoof(parent, out vacRoofReason);
            bool usable = IsGenerallyUsable(out string usableReason);

            builder.AppendLine("Space Services");
            builder.AppendLine("Mode: " + ModeLabel());
            builder.AppendLine("Reservation: " + (string.IsNullOrEmpty(reservedForGroup) ? "free" : "reserved"));
            builder.AppendLine("Damage: immune");
            builder.AppendLine("Vacuum exposed: " + (vacuum > 0.001f ? "yes (" + vacuum.ToStringPercent() + ")" : "no"));
            builder.AppendLine("Roof accessible: " + (roofAccessible ? "yes, " + ServiceEnvironmentUtility.RoofAccessReport(parent) : "no, " + roofReason));
            if (requireVacSafeRoof)
            {
                builder.AppendLine("Vac-safe roof required: " + (vacRoofOk ? "satisfied" : "not satisfied"));
            }
            builder.AppendLine("Map: " + (eligibility.allowed ? "eligible" : "blocked"));
            builder.Append("Overall: " + (usable ? "usable" : "not usable, " + usableReason));
            return builder.ToString().TrimEnd();
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

}
