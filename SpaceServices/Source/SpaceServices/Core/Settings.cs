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
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_DebugLogging".Translate(), ref Settings.debugLogging);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_VerboseDevLogging".Translate(), ref Settings.verboseDevLogging);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_AutoExtract".Translate(), ref Settings.autoExtractFallback);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_Hospital".Translate(), ref Settings.enableHospital);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_Hospitality".Translate(), ref Settings.enableHospitality);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_HospitalityRequireBeds".Translate(), ref Settings.hospitalityRequireGuestBeds);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_HospitalityAutoDepartBedless".Translate(), ref Settings.hospitalityAutoDepartBedlessGuests);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_HospitalityVacuumGuard".Translate(), ref Settings.hospitalityVacuumGuard);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_HospitalityFallbackScheduler".Translate(), ref Settings.hospitalityFallbackScheduler);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_TrafficRateOverride".Translate(), ref Settings.trafficRateOverride);
            if (Settings.trafficRateOverride)
            {
                Settings.hospitalPatientTrafficRate = RateSlider(listing, "JDB_SpaceServices_Settings_HospitalPatientTrafficRate", Settings.hospitalPatientTrafficRate);
                Settings.hospitalMassCasualtyTrafficRate = RateSlider(listing, "JDB_SpaceServices_Settings_HospitalMassCasualtyTrafficRate", Settings.hospitalMassCasualtyTrafficRate);
                Settings.hospitalityVisitorTrafficRate = RateSlider(listing, "JDB_SpaceServices_Settings_HospitalityVisitorTrafficRate", Settings.hospitalityVisitorTrafficRate);
                listing.Label("JDB_SpaceServices_Settings_HospitalityFallbackRateNote".Translate());
            }
            else
            {
                Settings.hospitalityFallbackIntervalDays = listing.SliderLabeled(
                    "JDB_SpaceServices_Settings_HospitalityFallbackInterval".Translate(Settings.hospitalityFallbackIntervalDays.ToString("0.0")),
                    Settings.hospitalityFallbackIntervalDays,
                    0.5f,
                    5f);
            }
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_Spaceports".Translate(), ref Settings.enableSpaceportsBridge);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_RequirePad".Translate(), ref Settings.requireServicePadForArrivals);
            listing.CheckboxLabeled("JDB_SpaceServices_Settings_SealedNoSuit".Translate(), ref Settings.allowSealedNoSuitArrivals);
            listing.End();
            Settings.Write();
        }

        private static float RateSlider(Listing_Standard listing, string translationKey, float value)
        {
            float rounded = SpaceServicesSettings.QuantizeRate(value);
            float next = listing.SliderLabeled(translationKey.Translate(rounded.ToString("0.00")), rounded, 0f, 1f);
            return SpaceServicesSettings.QuantizeRate(next);
        }
    }

    public class SpaceServicesSettings : ModSettings
    {
        public bool debugLogging = true;
        public bool verboseDevLogging = false;
        public bool autoExtractFallback = true;
        public bool enableHospital = true;
        public bool enableHospitality = true;
        public bool hospitalityRequireGuestBeds = true;
        public bool hospitalityAutoDepartBedlessGuests = true;
        public bool hospitalityVacuumGuard = false;
        public bool hospitalityFallbackScheduler = true;
        public float hospitalityFallbackIntervalDays = 1.5f;
        public bool trafficRateOverride = true;
        public float hospitalPatientTrafficRate = 0.25f;
        public float hospitalMassCasualtyTrafficRate = 0.25f;
        public float hospitalityVisitorTrafficRate = 0.25f;
        public bool enableSpaceportsBridge = true;
        public bool requireServicePadForArrivals = false;
        public bool allowSealedNoSuitArrivals = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref debugLogging, "debugLogging", true);
            Scribe_Values.Look(ref verboseDevLogging, "verboseDevLogging", false);
            Scribe_Values.Look(ref autoExtractFallback, "autoExtractFallback", true);
            Scribe_Values.Look(ref enableHospital, "enableHospital", true);
            Scribe_Values.Look(ref enableHospitality, "enableHospitality", true);
            Scribe_Values.Look(ref hospitalityRequireGuestBeds, "hospitalityRequireGuestBeds", true);
            Scribe_Values.Look(ref hospitalityAutoDepartBedlessGuests, "hospitalityAutoDepartBedlessGuests", true);
            Scribe_Values.Look(ref hospitalityVacuumGuard, "hospitalityVacuumGuard", false);
            Scribe_Values.Look(ref hospitalityFallbackScheduler, "hospitalityFallbackScheduler", true);
            Scribe_Values.Look(ref hospitalityFallbackIntervalDays, "hospitalityFallbackIntervalDays", 1.5f);
            hospitalityFallbackIntervalDays = Mathf.Clamp(hospitalityFallbackIntervalDays, 0.5f, 5f);
            Scribe_Values.Look(ref trafficRateOverride, "trafficRateOverride", true);
            Scribe_Values.Look(ref hospitalPatientTrafficRate, "hospitalPatientTrafficRate", 0.25f);
            Scribe_Values.Look(ref hospitalMassCasualtyTrafficRate, "hospitalMassCasualtyTrafficRate", 0.25f);
            Scribe_Values.Look(ref hospitalityVisitorTrafficRate, "hospitalityVisitorTrafficRate", 0.25f);
            hospitalPatientTrafficRate = QuantizeRate(hospitalPatientTrafficRate);
            hospitalMassCasualtyTrafficRate = QuantizeRate(hospitalMassCasualtyTrafficRate);
            hospitalityVisitorTrafficRate = QuantizeRate(hospitalityVisitorTrafficRate);
            Scribe_Values.Look(ref enableSpaceportsBridge, "enableSpaceportsBridge", true);
            Scribe_Values.Look(ref requireServicePadForArrivals, "requireServicePadForArrivals", false);
            Scribe_Values.Look(ref allowSealedNoSuitArrivals, "allowSealedNoSuitArrivals", false);
        }

        public static float QuantizeRate(float value)
        {
            return Mathf.Clamp01(Mathf.Round(value / 0.05f) * 0.05f);
        }
    }
}
