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

}
