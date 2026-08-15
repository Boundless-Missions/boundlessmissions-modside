/*
 * LsReflect.cs – Tiny reflection helpers shared by the life-support adapters.
 *
 * Every life-support / DeepFreeze mod is optional and accessed purely by reflection
 * (the build only references the stock KSP assemblies). These helpers mirror the
 * defensive style of PhysicsRangeManager.cs: look things up by name, cache them, and
 * swallow every failure so a missing or differently-shaped mod is a safe no-op.
 */

using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GeneKerman
{
    internal static class LsReflect
    {
        /// <summary>Loaded assembly whose simple name matches (case-insensitive), or null.</summary>
        public static Assembly FindAssembly(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    string n = la?.assembly?.GetName().Name;
                    if (!string.IsNullOrEmpty(n) &&
                        string.Equals(n, simpleName, StringComparison.OrdinalIgnoreCase))
                        return la.assembly;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        /// <summary>True if any loaded assembly's simple name matches.</summary>
        public static bool HasAssembly(string simpleName) => FindAssembly(simpleName) != null;

        /// <summary>Resolve a type by full name from a named assembly (null on any miss).</summary>
        public static Type FindType(string assemblyName, string fullTypeName)
        {
            Assembly asm = FindAssembly(assemblyName);
            if (asm == null) return null;
            try { return asm.GetType(fullTypeName, false); }
            catch { return null; }
        }

        /// <summary>Resolve a type by full name from whichever of several candidate
        /// assemblies holds it. Kerbalism needs this: it ships as KerbalismBootstrap.dll
        /// and side-loads the real "Kerbalism" assembly from a .kbin during startup, so
        /// which name carries the types depends on when we look.</summary>
        public static Type FindTypeAny(string fullTypeName, params string[] assemblyNames)
        {
            if (assemblyNames == null) return null;
            foreach (var name in assemblyNames)
            {
                Type t = FindType(name, fullTypeName);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Value of a static property/field by name on a type, or null.</summary>
        public static object GetStatic(Type type, string memberName)
        {
            if (type == null) return null;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                PropertyInfo pi = type.GetProperty(memberName, F);
                if (pi != null) return pi.GetValue(null, null);
                FieldInfo fi = type.GetField(memberName, F);
                if (fi != null) return fi.GetValue(null);
            }
            catch { /* ignore */ }
            return null;
        }

        /// <summary>Read an instance property/field by name (handles C# auto-property
        /// backing fields too). Returns null on any miss.</summary>
        public static object GetMember(object target, string memberName)
        {
            if (target == null) return null;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                Type t = target.GetType();
                PropertyInfo pi = t.GetProperty(memberName, F);
                if (pi != null && pi.CanRead) return pi.GetValue(target, null);
                FieldInfo fi = t.GetField(memberName, F);
                if (fi != null) return fi.GetValue(target);
            }
            catch { /* ignore */ }
            return null;
        }

        /// <summary>Write an instance property/field by name. Returns false on any miss.</summary>
        public static bool SetMember(object target, string memberName, object value)
        {
            if (target == null) return false;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                Type t = target.GetType();
                PropertyInfo pi = t.GetProperty(memberName, F);
                if (pi != null && pi.CanWrite) { pi.SetValue(target, value, null); return true; }
                FieldInfo fi = t.GetField(memberName, F);
                if (fi != null) { fi.SetValue(target, value); return true; }
            }
            catch { /* ignore */ }
            return false;
        }

        /// <summary>Invoke an instance method by name (first overload matching arg count),
        /// returning its result or null. Best-effort; never throws.</summary>
        public static object Invoke(object target, string methodName, params object[] args)
        {
            if (target == null) return null;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                int n = args?.Length ?? 0;
                MethodInfo mi = target.GetType().GetMethods(F)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == n);
                return mi?.Invoke(target, args);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] LsReflect.Invoke {methodName} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Invoke a static method by name (first overload matching arg count),
        /// returning its result or null. Best-effort; never throws.</summary>
        public static object InvokeStatic(Type type, string methodName, params object[] args)
        {
            if (type == null) return null;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                int n = args?.Length ?? 0;
                MethodInfo mi = type.GetMethods(F)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == n);
                return mi?.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] LsReflect.InvokeStatic {methodName} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Every value in a dictionary-like container, or an empty list. Used to
        /// walk per-kerbal rule state without knowing the mod's value type.</summary>
        public static System.Collections.Generic.List<object> Values(object container)
        {
            var result = new System.Collections.Generic.List<object>();
            if (container == null) return result;
            try
            {
                if (container is System.Collections.IDictionary d)
                {
                    foreach (object v in d.Values) result.Add(v);
                    return result;
                }
                object values = GetMember(container, "Values");
                if (values is System.Collections.IEnumerable e)
                    foreach (object v in e) result.Add(v);
            }
            catch { /* ignore */ }
            return result;
        }

        /// <summary>Look up a value by string key in a dictionary-like container. Handles
        /// a plain Dictionary (via IDictionary) and KSP's DictionaryValueList (via its
        /// ContainsKey(string) + string indexer). Returns null on miss.</summary>
        public static object GetByKey(object container, string key)
        {
            if (container == null || key == null) return null;
            if (container is System.Collections.IDictionary nd)
                return nd.Contains(key) ? nd[key] : null;
            try
            {
                Type t = container.GetType();
                MethodInfo contains = t.GetMethod("ContainsKey", new[] { typeof(string) });
                if (contains != null && !(bool)contains.Invoke(container, new object[] { key }))
                    return null;
                MethodInfo getItem = t.GetMethod("get_Item", new[] { typeof(string) });
                if (getItem != null) return getItem.Invoke(container, new object[] { key });
            }
            catch { /* ignore */ }
            return null;
        }

        /// <summary>True if a dictionary-like container holds the given string key.</summary>
        public static bool ContainsKey(object container, string key)
        {
            if (container == null || key == null) return false;
            if (container is System.Collections.IDictionary nd) return nd.Contains(key);
            try
            {
                MethodInfo contains = container.GetType().GetMethod("ContainsKey", new[] { typeof(string) });
                if (contains != null) return (bool)contains.Invoke(container, new object[] { key });
            }
            catch { /* ignore */ }
            return false;
        }

        /// <summary>The current universe time, or 0 if Planetarium isn't ready.</summary>
        public static double Now()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0d; }
        }

        // Every status, so a kerbal parked by the emergency freeze (rosterStatus = Dead,
        // which keeps KSP's respawn timer off it) is still findable. The plain Crew list
        // would miss exactly the kerbals a thaw needs to look up.
        private static readonly ProtoCrewMember.RosterStatus[] AllStatuses =
        {
            ProtoCrewMember.RosterStatus.Assigned,
            ProtoCrewMember.RosterStatus.Available,
            ProtoCrewMember.RosterStatus.Dead,
            ProtoCrewMember.RosterStatus.Missing,
        };

        /// <summary>Locate a ProtoCrewMember in the current game's roster by name.</summary>
        public static ProtoCrewMember FindCrew(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName) || HighLogic.CurrentGame == null) return null;
            try
            {
                foreach (var pcm in HighLogic.CurrentGame.CrewRoster.Kerbals(AllStatuses))
                    if (pcm != null && pcm.name == kerbalName) return pcm;
                foreach (var pcm in HighLogic.CurrentGame.CrewRoster.Tourist)
                    if (pcm != null && pcm.name == kerbalName) return pcm;
            }
            catch { /* ignore */ }
            return null;
        }
    }
}
