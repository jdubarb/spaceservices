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
        public static object GetMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            Type type = obj.GetType();
            PropertyInfo property = AccessTools.Property(type, name);
            if (property != null)
            {
                return property.GetValue(obj, null);
            }
            FieldInfo field = AccessTools.Field(type, name);
            return field == null ? null : field.GetValue(obj);
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
            Type type = obj.GetType();
            PropertyInfo property = AccessTools.Property(type, name);
            if (property != null && property.CanWrite)
            {
                property.SetValue(obj, value, null);
                return;
            }
            FieldInfo field = AccessTools.Field(type, name);
            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }
    }
}
