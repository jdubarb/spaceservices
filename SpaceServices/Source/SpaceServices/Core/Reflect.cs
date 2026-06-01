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
    public static class Reflect
    {
        private static readonly Dictionary<string, PropertyInfo> PropertyCache = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, FieldInfo> FieldCache = new Dictionary<string, FieldInfo>();
        private static readonly HashSet<string> MissingProperties = new HashSet<string>();
        private static readonly HashSet<string> MissingFields = new HashSet<string>();

        public static object GetMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            try
            {
                Type type = obj.GetType();
                PropertyInfo property = CachedProperty(type, name);
                if (property != null)
                {
                    return property.GetValue(obj, null);
                }
                FieldInfo field = CachedField(type, name);
                return field == null ? null : field.GetValue(obj);
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogThrottled("reflect-get-" + obj.GetType().FullName + "." + name, "Reflection get failed for " + obj.GetType().FullName + "." + name + ": " + ex.GetType().Name + " " + ex.Message, GenDate.TicksPerHour);
                return null;
            }
        }

        public static bool BoolMember(object obj, string name)
        {
            return BoolMember(obj, name, false);
        }

        public static bool BoolMember(object obj, string name, bool fallback)
        {
            object value = GetMember(obj, name);
            return value is bool ? (bool)value : fallback;
        }

        public static bool BoolFromNested(object root, params string[] path)
        {
            object current = root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                current = GetMember(current, path[i]);
            }
            return BoolMember(current, path[path.Length - 1]);
        }

        public static string DefName(object obj)
        {
            Def def = obj as Def;
            if (def != null)
            {
                return def.defName ?? "";
            }
            object value = GetMember(obj, "defName");
            return value == null ? "" : Convert.ToString(value);
        }

        public static void SetMember(object obj, string name, object value)
        {
            if (obj == null || string.IsNullOrEmpty(name))
            {
                return;
            }
            try
            {
                Type type = obj.GetType();
                PropertyInfo property = CachedProperty(type, name);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(obj, value, null);
                    return;
                }
                FieldInfo field = CachedField(type, name);
                if (field != null)
                {
                    field.SetValue(obj, value);
                }
            }
            catch (Exception ex)
            {
                ServiceDebugUtility.LogThrottled("reflect-set-" + obj.GetType().FullName + "." + name, "Reflection set failed for " + obj.GetType().FullName + "." + name + ": " + ex.GetType().Name + " " + ex.Message, GenDate.TicksPerHour);
            }
        }

        private static PropertyInfo CachedProperty(Type type, string name)
        {
            string key = CacheKey(type, name);
            if (MissingProperties.Contains(key))
            {
                return null;
            }
            PropertyInfo property;
            if (PropertyCache.TryGetValue(key, out property))
            {
                return property;
            }
            property = AccessTools.Property(type, name);
            if (property == null)
            {
                MissingProperties.Add(key);
            }
            else
            {
                PropertyCache[key] = property;
            }
            return property;
        }

        private static FieldInfo CachedField(Type type, string name)
        {
            string key = CacheKey(type, name);
            if (MissingFields.Contains(key))
            {
                return null;
            }
            FieldInfo field;
            if (FieldCache.TryGetValue(key, out field))
            {
                return field;
            }
            field = AccessTools.Field(type, name);
            if (field == null)
            {
                MissingFields.Add(key);
            }
            else
            {
                FieldCache[key] = field;
            }
            return field;
        }

        private static string CacheKey(Type type, string name)
        {
            return (type == null ? "" : type.FullName) + "." + name;
        }
    }
}
