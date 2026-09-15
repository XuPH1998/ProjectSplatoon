using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Splatoon.Config
{
    /// <summary>The Inspector and runtime validation share the authored Chinese names.</summary>
    public static class WeaponConfigLabels
    {
        static readonly Dictionary<string, string> Names = BuildNames();
        static Dictionary<string, string> BuildNames()
        {
            var names = new Dictionary<string, string>();
            foreach (var field in typeof(WeaponConfigAsset).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var label = field.GetCustomAttribute<InspectorNameAttribute>();
                if (label == null) continue;
                names[field.Name] = label.displayName;
                names[char.ToUpperInvariant(field.Name[0]) + field.Name.Substring(1)] = label.displayName;
            }
            return names;
        }
        public static string Name(string field) => Names.TryGetValue(field, out var label) ? label : "武器参数";
    }
}
