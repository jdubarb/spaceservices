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
    public static class SpaceServicesMainButtonUtility
    {
        public static void HideReplacedServiceTabs()
        {
            ApplyServiceTabVisibility();
        }

        public static void ApplyServiceTabVisibility()
        {
            bool showExternal = SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.replaceExternalServiceTabs;
            SetMainButtonVisible("Patients", showExternal);
            SetMainButtonVisible("Guests", showExternal);
        }

        public static bool OpenNativeTab(string defName)
        {
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                return false;
            }
            try
            {
                // Ask RimWorld to own the tab lifecycle; external MainTabWindows are not safe child widgets.
                object root = Find.MainTabsRoot;
                MethodInfo method = root == null ? null : root.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.Name == "SetCurrentTab" && candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(MainButtonDef)));
                if (method != null && TryInvokeSetCurrentTab(root, method, def))
                {
                    return true;
                }
                method = AccessTools.Method(typeof(MainButtonDef), "Notify_Active");
                if (method != null)
                {
                    method.Invoke(def, null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogWarning(ServiceLogIntegration.Core, "Could not open native " + defName + " tab: " + ex.Message);
            }
            return false;
        }

        private static bool TryInvokeSetCurrentTab(object root, MethodInfo method, MainButtonDef def)
        {
            ParameterInfo[] parameters = method.GetParameters();
            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                if (parameterType == typeof(MainButtonDef))
                {
                    args[i] = def;
                }
                else if (parameterType == typeof(bool))
                {
                    args[i] = true;
                }
                else
                {
                    args[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
                }
            }
            method.Invoke(root, args);
            return true;
        }

        private static void SetMainButtonVisible(string defName, bool visible)
        {
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                return;
            }
            FieldInfo field = AccessTools.Field(typeof(MainButtonDef), "buttonVisible");
            if (field == null)
            {
                return;
            }
            field.SetValue(def, visible);
        }
    }
}
