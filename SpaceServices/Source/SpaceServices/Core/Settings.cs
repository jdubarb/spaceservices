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

}
