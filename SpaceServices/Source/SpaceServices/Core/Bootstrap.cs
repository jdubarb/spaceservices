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
        public const string PackageId = "jdubarb.spaceservices";
        public const string CategoryDefName = "JDB_SpaceServices";

        private static readonly Harmony Harmony = new Harmony(PackageId);

        static SpaceServicesBootstrap()
        {
            Harmony.PatchAll(Assembly.GetExecutingAssembly());
            ServicePadEventPatches.Install(Harmony);
            OptionalModPatches.Install(Harmony);
            LongEventHandler.ExecuteWhenFinished(ArchitectMenuPatch.InjectArchitectDesignators);
            ServiceDebugUtility.Log(ServiceLogIntegration.Core, "Loaded.");
        }
    }
}
