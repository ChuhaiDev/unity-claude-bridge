using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>Compact, token-cheap text rendering of serialized values and object references.</summary>
    static class Fmt
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string F(float f) => f.ToString("0.###", Inv);
        public static string F(double f) => f.ToString("0.###", Inv);

        public static string V2(Vector2 v) => $"({F(v.x)},{F(v.y)})";
        public static string V3(Vector3 v) => v.z == 0 ? $"({F(v.x)},{F(v.y)})" : $"({F(v.x)},{F(v.y)},{F(v.z)})";
        public static string V3Full(Vector3 v) => $"({F(v.x)},{F(v.y)},{F(v.z)})";
        public static string V4(Vector4 v) => $"({F(v.x)},{F(v.y)},{F(v.z)},{F(v.w)})";

        public static string Color(Color c)
        {
            var s = "#" + ColorUtility.ToHtmlStringRGBA(c);
            return s.EndsWith("FF") ? s.Substring(0, 7) : s;
        }

        /// <summary>
        /// Reference format used everywhere (and accepted back by `set`):
        ///   asset main object      Assets/Art/hero.png
        ///   sub-asset              Assets/Art/hero.png#hero_0
        ///   scene GameObject       /Root/Child
        ///   scene component        /Root/Child:Rigidbody2D
        ///   built-in resource      builtin:Sprite-Lit-Default
        /// </summary>
        public static string Ref(Object o)
        {
            if (o == null) return "null";
            if (o is AssetImporter ai) return "importer:" + ai.assetPath;
            var path = AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(path))
            {
                if (path.StartsWith("Resources/unity_builtin") || path.StartsWith("Library/"))
                    return "builtin:" + o.name;
                return AssetDatabase.IsMainAsset(o) ? path : path + "#" + o.name;
            }
            if (o is GameObject go) return HierPath(go.transform);
            if (o is Component c) return HierPath(c.transform) + ":" + c.GetType().Name;
            return $"<{o.GetType().Name} {o.name}>";
        }

        public static string HierPath(Transform t) => ScenePath.Of(t);

        /// <summary>Render a property's value on one line. Structs/arrays are inlined up to a budget.</summary>
        public static string Prop(SerializedProperty p, int depth = 0)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    {
                        if (p.type == "ulong" || p.type == "long") return p.longValue.ToString(Inv);
                        var et = IntEnum(p);
                        return et != null ? EnumStr(et, p.intValue) : p.intValue.ToString(Inv);
                    }
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return p.type == "double" ? F(p.doubleValue) : F(p.floatValue);
                case SerializedPropertyType.String: return Quote(p.stringValue);
                case SerializedPropertyType.Color: return Color(p.colorValue);
                case SerializedPropertyType.ObjectReference: return Ref(p.objectReferenceValue);
                case SerializedPropertyType.ExposedReference: return Ref(p.exposedReferenceValue);
                case SerializedPropertyType.LayerMask: return LayerMaskStr(p.intValue);
                case SerializedPropertyType.Enum:
                    {
                        var i = p.enumValueIndex;
                        return i >= 0 && i < p.enumNames.Length ? p.enumNames[i] : p.intValue.ToString(Inv);
                    }
                case SerializedPropertyType.Vector2: return V2(p.vector2Value);
                case SerializedPropertyType.Vector3: return V3Full(p.vector3Value);
                case SerializedPropertyType.Vector4: return V4(p.vector4Value);
                case SerializedPropertyType.Vector2Int: return $"({p.vector2IntValue.x},{p.vector2IntValue.y})";
                case SerializedPropertyType.Vector3Int: return $"({p.vector3IntValue.x},{p.vector3IntValue.y},{p.vector3IntValue.z})";
                case SerializedPropertyType.Quaternion: return "euler" + V3Full(p.quaternionValue.eulerAngles);
                case SerializedPropertyType.Rect: { var r = p.rectValue; return $"rect({F(r.x)},{F(r.y)},{F(r.width)},{F(r.height)})"; }
                case SerializedPropertyType.RectInt: { var r = p.rectIntValue; return $"rect({r.x},{r.y},{r.width},{r.height})"; }
                case SerializedPropertyType.Bounds: { var b = p.boundsValue; return $"bounds(c={V3Full(b.center)} s={V3Full(b.size)})"; }
                case SerializedPropertyType.BoundsInt: { var b = p.boundsIntValue; return $"bounds(pos={b.position} size={b.size})"; }
                case SerializedPropertyType.ArraySize: return p.intValue.ToString(Inv);
                case SerializedPropertyType.Character: return "'" + (char)p.intValue + "'";
                case SerializedPropertyType.AnimationCurve: return $"curve({p.animationCurveValue?.length ?? 0} keys)";
                case SerializedPropertyType.Gradient: return "gradient";
                case SerializedPropertyType.Hash128: return p.hash128Value.ToString();
                case SerializedPropertyType.ManagedReference:
                    return string.IsNullOrEmpty(p.managedReferenceFullTypename) ? "null" : "ref " + p.managedReferenceFullTypename.Split(' ').Last() + Children(p, depth);
                case SerializedPropertyType.Generic:
                    if (p.isArray) return ArrayStr(p, depth);
                    return Children(p, depth);
                default:
                    return "?" + p.propertyType;
            }
        }

        static readonly Dictionary<(Type, string), Type> s_IntEnums = new Dictionary<(Type, string), Type>();

        /// <summary>Enum type behind an int-serialized field (m_Constraints → RigidbodyConstraints2D), matched by name.</summary>
        static Type IntEnum(SerializedProperty p)
        {
            var target = p.serializedObject.targetObject;
            if (target == null || p.depth != 0) return null;
            var key = (target.GetType(), p.name);
            if (s_IntEnums.TryGetValue(key, out var t)) return t;
            var n = Props.Norm(Short(p.name));
            t = target.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(pr => pr.PropertyType.IsEnum)
                .FirstOrDefault(pr => Props.Norm(pr.Name) == n || (n.Length >= 5 && Props.Norm(pr.Name).StartsWith(n.Substring(0, 5))))?.PropertyType;
            return s_IntEnums[key] = t;
        }

        static string EnumStr(Type t, int v)
        {
            if (Enum.IsDefined(t, v)) return Enum.GetName(t, v);
            if (t.IsDefined(typeof(FlagsAttribute), false))
                return string.Join("|", Enum.GetValues(t).Cast<object>().Select(Convert.ToInt32).Where(f => f != 0 && (v & f) == f && IsSingleBit(f)).Select(f => Enum.GetName(t, f)));
            return v.ToString(Inv);
        }

        static bool IsSingleBit(int f) => (f & (f - 1)) == 0;

        static string ArrayStr(SerializedProperty p, int depth)
        {
            int n = p.arraySize;
            if (n == 0) return "[]";
            if (depth >= 2 || n > 12) return $"[{n} items]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Prop(p.GetArrayElementAtIndex(i), depth + 1));
                if (sb.Length > 300) { sb.Append($", …{n} total"); break; }
            }
            return sb.Append("]").ToString();
        }

        static string Children(SerializedProperty p, int depth)
        {
            if (!p.hasVisibleChildren) return "{}";
            if (depth >= 2) return "{…}";
            var sb = new StringBuilder("{");
            var it = p.Copy();
            var end = p.GetEndProperty();
            bool first = true;
            if (it.NextVisible(true))
            {
                while (!SerializedProperty.EqualContents(it, end))
                {
                    if (!first) sb.Append(' ');
                    first = false;
                    sb.Append(it.name).Append('=').Append(Prop(it, depth + 1));
                    if (sb.Length > 300) { sb.Append(" …"); break; }
                    if (!it.NextVisible(false)) break;
                }
            }
            return sb.Append('}').ToString();
        }

        public static string LayerMaskStr(int mask)
        {
            if (mask == 0) return "Nothing";
            if (mask == -1) return "Everything";
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0)
                    names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? i.ToString() : LayerMask.LayerToName(i));
            return "layers(" + string.Join("|", names) + ")";
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            if (s.Length > 120) s = s.Substring(0, 120) + "…";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        }

        /// <summary>Strip Unity's m_ prefix for display: m_SortingOrder → sortingOrder.</summary>
        public static string Short(string name)
        {
            if (name.StartsWith("m_") && name.Length > 2)
                return char.ToLowerInvariant(name[2]) + name.Substring(3);
            return name;
        }
    }
}
