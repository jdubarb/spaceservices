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
    public sealed class MainTabWindow_SpaceServices : MainTabWindow
    {
        private static Vector2 scrollPosition;

        public override Vector2 RequestedTabSize => new Vector2(700f, 640f);

        public override void DoWindowContents(Rect rect)
        {
            Map map = Find.CurrentMap;
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, 900f);
            Widgets.BeginScrollView(rect, ref scrollPosition, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("JDB_SpaceServices_Panel_Title".Translate());
            listing.GapLine();
            if (map == null)
            {
                listing.Label("JDB_SpaceServices_Panel_NoMap".Translate());
                listing.End();
                Widgets.EndScrollView();
                return;
            }

            DrawOverview(listing, map);
            DrawNativePanelButtons(listing);
            if (DebugSettings.godMode)
            {
                DrawDebugControls(listing, map);
            }

            listing.End();
            Widgets.EndScrollView();
        }

        private static void DrawOverview(Listing_Standard listing, Map map)
        {
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            SpaceServiceEligibility eligibility = SpaceServiceMapDetector.EvaluateServiceAccess(map);
            listing.Label("JDB_SpaceServices_Panel_MapStatus".Translate(eligibility.allowed ? "eligible" : "blocked"));
            if (!eligibility.allowed && eligibility.blockReasons.Count > 0)
            {
                listing.Label(string.Join(", ", eligibility.blockReasons.ToArray()));
            }

            int hospitalGroups = ServiceDebugLimits.CountActiveGroups(map, "hospital");
            int hospitalPatients = ServiceDebugLimits.CountActiveServicePawns(map, "hospital");
            int hospitalityGroups = ServiceDebugLimits.CountActiveGroups(map, "hospitality");
            int hospitalityPawns = ServiceDebugLimits.CountActiveServicePawns(map, "hospitality");
            int pads = ServicePadUtility.AllServicePadBuildings(map).Count();
            int patientPads = ServicePadUtility.CountServicePads(map, ServiceUse.Patient);
            int guestPads = ServicePadUtility.CountServicePads(map, ServiceUse.Guest);

            listing.Label("JDB_SpaceServices_Panel_Pads".Translate(pads, patientPads, guestPads));
            listing.Label("JDB_SpaceServices_Panel_HospitalSummary".Translate(hospitalGroups, hospitalPatients));
            listing.Label("JDB_SpaceServices_Panel_HospitalitySummary".Translate(hospitalityGroups, hospitalityPawns));
        }

        private static void DrawNativePanelButtons(Listing_Standard listing)
        {
            listing.GapLine();
            listing.Label("JDB_SpaceServices_Panel_NativePanels".Translate());
            Rect row = listing.GetRect(36f);
            float width = (row.width - 8f) / 2f;
            if (Widgets.ButtonText(new Rect(row.x, row.y, width, row.height), "JDB_SpaceServices_Panel_OpenHospital".Translate()))
            {
                SpaceServicesMainButtonUtility.OpenNativeTab("Patients");
            }
            if (Widgets.ButtonText(new Rect(row.x + width + 8f, row.y, width, row.height), "JDB_SpaceServices_Panel_OpenHospitality".Translate()))
            {
                SpaceServicesMainButtonUtility.OpenNativeTab("Guests");
            }
        }

        private static void DrawDebugControls(Listing_Standard listing, Map map)
        {
            SpaceServicesMapComponent comp = map.GetComponent<SpaceServicesMapComponent>();
            if (comp == null)
            {
                return;
            }

            listing.GapLine();
            listing.Label("JDB_SpaceServices_Panel_DebugLimits".Translate());
            comp.debugHospitalPatientLimit = LimitSlider(listing, "JDB_SpaceServices_Panel_HospitalPatientLimit", comp.debugHospitalPatientLimit, 20);
            comp.debugHospitalityGroupLimit = LimitSlider(listing, "JDB_SpaceServices_Panel_HospitalityGroupLimit", comp.debugHospitalityGroupLimit, 20);
            comp.debugHospitalityPawnLimit = LimitSlider(listing, "JDB_SpaceServices_Panel_HospitalityPawnLimit", comp.debugHospitalityPawnLimit, 60);

            listing.GapLine();
            listing.Label("JDB_SpaceServices_Panel_DebugActions".Translate());
            Rect row = listing.GetRect(32f);
            float width = (row.width - 8f) / 2f;
            if (Widgets.ButtonText(new Rect(row.x, row.y, width, row.height), "JDB_SpaceServices_Panel_ReportMap".Translate()))
            {
                SpaceServiceEligibility eligibility = SpaceServiceMapDetector.EvaluateServiceAccess(map);
                ServiceDebugUtility.Log(ServiceLogIntegration.Core, eligibility.ToLogString(map));
                Messages.Message(eligibility.allowed ? "Space Services: Map Eligible" : "Space Services: Map Blocked", MessageTypeDefOf.NeutralEvent, false);
            }
            if (Widgets.ButtonText(new Rect(row.x + width + 8f, row.y, width, row.height), "JDB_SpaceServices_Panel_ClearReservations".Translate()))
            {
                comp.ClearAllServiceReservations("debug panel cleared service reservations");
            }

            row = listing.GetRect(32f);
            if (Widgets.ButtonText(new Rect(row.x, row.y, width, row.height), "JDB_SpaceServices_Panel_RemoveShuttles".Translate()))
            {
                int removed = ServiceShuttleUtility.CleanupAllServiceShuttles(map);
                Messages.Message("Space Services: Removed " + removed + " Service Shuttles", MessageTypeDefOf.NeutralEvent, false);
            }
            if (Widgets.ButtonText(new Rect(row.x + width + 8f, row.y, width, row.height), "JDB_SpaceServices_Panel_ResetTraffic".Translate()))
            {
                comp.DebugResetServiceTraffic("debug panel reset service traffic");
            }
        }

        private static int LimitSlider(Listing_Standard listing, string translationKey, int value, int max)
        {
            int noLimitValue = max + 1;
            int sliderValue = value < 0 ? noLimitValue : Mathf.Clamp(value, 0, max);
            string display = value < 0 ? "no limit" : value == 0 ? "none" : value.ToString();
            float next = listing.SliderLabeled(translationKey.Translate(display), sliderValue, 0, noLimitValue, 0.5f, (translationKey + "Desc").Translate());
            int rounded = Mathf.RoundToInt(next);
            return rounded >= noLimitValue ? -1 : rounded;
        }
    }
}
