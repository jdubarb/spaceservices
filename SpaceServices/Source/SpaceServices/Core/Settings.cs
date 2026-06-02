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
    public class SpaceServicesMod : Mod
    {
        private const float SettingsViewHeight = 900f;
        private static Vector2 settingsScrollPosition;

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
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, SettingsViewHeight);
            Widgets.BeginScrollView(inRect, ref settingsScrollPosition, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            // Settings are grouped by the debugging workflow: global behavior, per-integration logs,
            // service-specific rules, traffic pacing, then soft-mod compatibility.
            Section(listing, "JDB_SpaceServices_Settings_SectionGeneral");
            Checkbox(listing, "JDB_SpaceServices_Settings_DebugLogging", ref Settings.debugLogging);
            Checkbox(listing, "JDB_SpaceServices_Settings_AutoExtract", ref Settings.autoExtractFallback);
            Checkbox(listing, "JDB_SpaceServices_Settings_Hospital", ref Settings.enableHospital);
            Checkbox(listing, "JDB_SpaceServices_Settings_Hospitality", ref Settings.enableHospitality);
            Checkbox(listing, "JDB_SpaceServices_Settings_ReplaceExternalTabs", ref Settings.replaceExternalServiceTabs);

            Section(listing, "JDB_SpaceServices_Settings_SectionVerbose");
            Checkbox(listing, "JDB_SpaceServices_Settings_VerboseCoreLogging", ref Settings.verboseCoreLogging);
            Checkbox(listing, "JDB_SpaceServices_Settings_VerboseHospitalLogging", ref Settings.verboseHospitalLogging);
            Checkbox(listing, "JDB_SpaceServices_Settings_VerboseHospitalityLogging", ref Settings.verboseHospitalityLogging);

            Section(listing, "JDB_SpaceServices_Settings_SectionHospitality");
            Checkbox(listing, "JDB_SpaceServices_Settings_HospitalityRequireBeds", ref Settings.hospitalityRequireGuestBeds);
            Checkbox(listing, "JDB_SpaceServices_Settings_HospitalityAutoDepartBedless", ref Settings.hospitalityAutoDepartBedlessGuests);
            Checkbox(listing, "JDB_SpaceServices_Settings_HospitalityVacuumGuard", ref Settings.hospitalityVacuumGuard);
            Checkbox(listing, "JDB_SpaceServices_Settings_HospitalityFallbackScheduler", ref Settings.hospitalityFallbackScheduler);

            Section(listing, "JDB_SpaceServices_Settings_SectionTraffic");
            Checkbox(listing, "JDB_SpaceServices_Settings_TrafficRateOverride", ref Settings.trafficRateOverride);
            if (Settings.trafficRateOverride)
            {
                // Rates are deterministic accumulators, not chance rolls. At 0.25x, every fourth eligible attempt passes.
                Settings.hospitalPatientTrafficRate = RateSlider(listing, "JDB_SpaceServices_Settings_HospitalPatientTrafficRate", Settings.hospitalPatientTrafficRate);
                Settings.hospitalMassCasualtyTrafficRate = RateSlider(listing, "JDB_SpaceServices_Settings_HospitalMassCasualtyTrafficRate", Settings.hospitalMassCasualtyTrafficRate);
                Settings.hospitalityVisitorTrafficRate = RateSlider(listing, "JDB_SpaceServices_Settings_HospitalityVisitorTrafficRate", Settings.hospitalityVisitorTrafficRate);
                listing.Label("JDB_SpaceServices_Settings_HospitalityFallbackRateNote".Translate());
            }
            else
            {
                Settings.hospitalityFallbackIntervalDays = IntervalSlider(listing, "JDB_SpaceServices_Settings_HospitalityFallbackInterval", Settings.hospitalityFallbackIntervalDays);
            }

            Section(listing, "JDB_SpaceServices_Settings_SectionCompatibility");
            Checkbox(listing, "JDB_SpaceServices_Settings_SealedNoSuit", ref Settings.allowSealedNoSuitArrivals);

            Section(listing, "JDB_SpaceServices_Settings_SectionExperimental");
            Checkbox(listing, "JDB_SpaceServices_Settings_MedPodBridge", ref Settings.medPodServiceBridge);
            Checkbox(listing, "JDB_SpaceServices_Settings_SuppressMassCasualtyPreDropEffects", ref Settings.suppressMassCasualtyPreDropEffects);
            listing.End();
            Widgets.EndScrollView();
            Settings.Write();
            SpaceServicesMainButtonUtility.ApplyServiceTabVisibility();
        }

        private static void Section(Listing_Standard listing, string translationKey)
        {
            listing.GapLine();
            listing.Label(translationKey.Translate());
        }

        private static void Checkbox(Listing_Standard listing, string translationKey, ref bool value)
        {
            listing.CheckboxLabeled(translationKey.Translate(), ref value, (translationKey + "Desc").Translate());
        }

        private static float RateSlider(Listing_Standard listing, string translationKey, float value)
        {
            float rounded = SpaceServicesSettings.QuantizeRate(value);
            float next = listing.SliderLabeled(translationKey.Translate(rounded.ToString("0.00")), rounded, 0f, 1f, 0.5f, (translationKey + "Desc").Translate());
            return SpaceServicesSettings.QuantizeRate(next);
        }

        private static float IntervalSlider(Listing_Standard listing, string translationKey, float value)
        {
            float rounded = SpaceServicesSettings.QuantizeIntervalDays(value);
            float next = listing.SliderLabeled(translationKey.Translate(rounded.ToString("0.00")), rounded, 0.5f, 5f, 0.5f, (translationKey + "Desc").Translate());
            return SpaceServicesSettings.QuantizeIntervalDays(next);
        }
    }

    public class SpaceServicesSettings : ModSettings
    {
        public bool debugLogging = false;
        public bool verboseCoreLogging = false;
        public bool verboseHospitalLogging = false;
        public bool verboseHospitalityLogging = false;
        public bool verboseDevLogging = false;
        public bool autoExtractFallback = true;
        public bool enableHospital = true;
        public bool enableHospitality = true;
        public bool replaceExternalServiceTabs = true;
        public bool hospitalityRequireGuestBeds = true;
        public bool hospitalityAutoDepartBedlessGuests = true;
        public bool hospitalityVacuumGuard = false;
        public bool hospitalityFallbackScheduler = true;
        public float hospitalityFallbackIntervalDays = 1.5f;
        // Traffic rates are deliberately capped at 1.00x. Space services should not become an event overclocker.
        public bool trafficRateOverride = true;
        public float hospitalPatientTrafficRate = 0.25f;
        public float hospitalMassCasualtyTrafficRate = 0.25f;
        public float hospitalityVisitorTrafficRate = 0.25f;
        public bool allowSealedNoSuitArrivals = false;
        public bool medPodServiceBridge = false;
        public bool suppressMassCasualtyPreDropEffects = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
            // Keep the old all-verbose debug save field as a one-load migration path for test saves.
            Scribe_Values.Look(ref verboseDevLogging, "verboseDevLogging", false);
            Scribe_Values.Look(ref verboseCoreLogging, "verboseCoreLogging", false);
            Scribe_Values.Look(ref verboseHospitalLogging, "verboseHospitalLogging", false);
            Scribe_Values.Look(ref verboseHospitalityLogging, "verboseHospitalityLogging", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && verboseDevLogging)
            {
                verboseCoreLogging = true;
                verboseHospitalLogging = true;
                verboseHospitalityLogging = true;
                verboseDevLogging = false;
            }
            Scribe_Values.Look(ref autoExtractFallback, "autoExtractFallback", true);
            Scribe_Values.Look(ref enableHospital, "enableHospital", true);
            Scribe_Values.Look(ref enableHospitality, "enableHospitality", true);
            Scribe_Values.Look(ref replaceExternalServiceTabs, "replaceExternalServiceTabs", true);
            Scribe_Values.Look(ref hospitalityRequireGuestBeds, "hospitalityRequireGuestBeds", true);
            Scribe_Values.Look(ref hospitalityAutoDepartBedlessGuests, "hospitalityAutoDepartBedlessGuests", true);
            Scribe_Values.Look(ref hospitalityVacuumGuard, "hospitalityVacuumGuard", false);
            Scribe_Values.Look(ref hospitalityFallbackScheduler, "hospitalityFallbackScheduler", true);
            Scribe_Values.Look(ref hospitalityFallbackIntervalDays, "hospitalityFallbackIntervalDays", 1.5f);
            hospitalityFallbackIntervalDays = QuantizeIntervalDays(hospitalityFallbackIntervalDays);
            Scribe_Values.Look(ref trafficRateOverride, "trafficRateOverride", true);
            Scribe_Values.Look(ref hospitalPatientTrafficRate, "hospitalPatientTrafficRate", 0.25f);
            Scribe_Values.Look(ref hospitalMassCasualtyTrafficRate, "hospitalMassCasualtyTrafficRate", 0.25f);
            Scribe_Values.Look(ref hospitalityVisitorTrafficRate, "hospitalityVisitorTrafficRate", 0.25f);
            hospitalPatientTrafficRate = QuantizeRate(hospitalPatientTrafficRate);
            hospitalMassCasualtyTrafficRate = QuantizeRate(hospitalMassCasualtyTrafficRate);
            hospitalityVisitorTrafficRate = QuantizeRate(hospitalityVisitorTrafficRate);
            Scribe_Values.Look(ref allowSealedNoSuitArrivals, "allowSealedNoSuitArrivals", false);
            Scribe_Values.Look(ref medPodServiceBridge, "medPodServiceBridge", false);
            Scribe_Values.Look(ref suppressMassCasualtyPreDropEffects, "suppressMassCasualtyPreDropEffects", false);
        }

        public static float QuantizeRate(float value)
        {
            return Mathf.Clamp01(Mathf.Round(value / 0.05f) * 0.05f);
        }

        public static float QuantizeIntervalDays(float value)
        {
            return Mathf.Clamp(Mathf.Round(value / 0.25f) * 0.25f, 0.5f, 5f);
        }
    }
}
