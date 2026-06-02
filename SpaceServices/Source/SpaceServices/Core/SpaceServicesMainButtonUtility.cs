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
    public static class ServiceTabVisibilityPatches
    {
        public static void Install(Harmony harmony)
        {
            MethodInfo drawMethod = AccessTools.Method(typeof(MainButtonsRoot), "MainButtonsOnGUI");
            if (drawMethod == null)
            {
                ServiceDebugUtility.LogWarning(ServiceLogIntegration.Core, "Could not patch main button visibility; MainButtonsOnGUI was not found.");
                return;
            }
            harmony.Patch(drawMethod, prefix: new HarmonyMethod(typeof(ServiceTabVisibilityPatches), nameof(MainButtonsOnGuiPrefix)));
        }

        public static void MainButtonsOnGuiPrefix()
        {
            SpaceServicesMainButtonUtility.ApplyServiceTabVisibility(false);
        }
    }

    public static class SpaceServicesMainButtonUtility
    {
        private static readonly HashSet<string> ReplacedServiceTabDefNames = new HashSet<string>
        {
            "Patients",
            "Guests"
        };

        private static readonly HashSet<string> ReplacedServiceTabWindowNames = new HashSet<string>
        {
            "Hospital.MainTabWindow_Hospital",
            "Hospitality.MainTab.MainTabWindow_Hospitality"
        };

        public static void HideReplacedServiceTabs()
        {
            ApplyServiceTabVisibility();
        }

        public static void ApplyServiceTabVisibility(bool refreshCache = true)
        {
            bool showExternal = SpaceServicesMod.Settings != null && !SpaceServicesMod.Settings.replaceExternalServiceTabs;
            foreach (MainButtonDef def in DefDatabase<MainButtonDef>.AllDefsListForReading)
            {
                if (IsReplacedServiceTab(def))
                {
                    SetMainButtonVisible(def, showExternal);
                }
            }
            if (refreshCache)
            {
                RefreshMainButtonCache(showExternal);
            }
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

        private static bool IsReplacedServiceTab(MainButtonDef def)
        {
            if (def == null)
            {
                return false;
            }
            if (ReplacedServiceTabDefNames.Contains(def.defName))
            {
                return true;
            }
            string tabWindowClassName = def.tabWindowClass == null ? null : def.tabWindowClass.FullName;
            return tabWindowClassName != null && ReplacedServiceTabWindowNames.Contains(tabWindowClassName);
        }

        private static void SetMainButtonVisible(MainButtonDef def, bool visible)
        {
            FieldInfo field = AccessTools.Field(typeof(MainButtonDef), "buttonVisible");
            if (field == null)
            {
                return;
            }
            field.SetValue(def, visible);
        }

        private static void RefreshMainButtonCache(bool showExternal)
        {
            object buttonsRoot = Find.MainButtonsRoot;
            InvokeNoArgRefreshMethods(buttonsRoot);
            InvokeNoArgRefreshMethods(Find.MainTabsRoot);
            if (!showExternal)
            {
                PruneCachedButtonLists(buttonsRoot);
            }
        }

        private static void InvokeNoArgRefreshMethods(object root)
        {
            if (root == null)
            {
                return;
            }
            foreach (string methodName in new[] { "SetButtonsDirty", "SetDirty", "Recache", "Reinit" })
            {
                MethodInfo method = AccessTools.Method(root.GetType(), methodName);
                if (method != null && method.GetParameters().Length == 0)
                {
                    try
                    {
                        method.Invoke(root, null);
                    }
                    catch
                    {
                        // Some refresh hooks are version-specific; ignore the ones that are present but not usable.
                    }
                }
            }
        }

        private static void PruneCachedButtonLists(object buttonsRoot)
        {
            if (buttonsRoot == null)
            {
                return;
            }
            foreach (FieldInfo field in buttonsRoot.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(IList).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }
                if (!(field.GetValue(buttonsRoot) is IList list))
                {
                    continue;
                }
                if (list.IsReadOnly || list.IsFixedSize)
                {
                    continue;
                }
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is MainButtonDef def && IsReplacedServiceTab(def))
                    {
                        try
                        {
                            list.RemoveAt(i);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }
            }
        }
    }
}
