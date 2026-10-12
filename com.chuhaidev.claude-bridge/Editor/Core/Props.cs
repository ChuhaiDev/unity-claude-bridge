using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Claude.Bridge
{
    /// <summary>Lenient SerializedProperty lookup and JSON → property assignment.</summary>
    static class Props
    {
        static readonly Regex Index = new Regex(@"\[(\d+)\]");

        /// <summary>
        /// Find a property by friendly path: "color" → m_Color, "items[2].hp" → items.Array.data[2].hp,
        /// "size.x" → m_Size.x. Exact serialized paths always work too.
        /// </summary>
        public static SerializedProperty Find(SerializedObject so, string path)
        {
            var p = so.FindProperty(path);
            if (p != null) return p;

            var parts = Index.Replace(path, ".Array.data[$1]").Split('.');
            SerializedProperty cur = null;
            for (int i = 0; i < parts.Length; i++)
            {
                var seg = parts[i];
                if (seg == "Array" && i + 1 < parts.Length && parts[i + 1].StartsWith("data["))
                {
                    var idx = int.Parse(parts[i + 1].Substring(5).TrimEnd(']'));
                    if (cur == null || !cur.isArray) return null;
                    if (idx >= cur.arraySize) cur.arraySize = idx + 1;
                    cur = cur.GetArrayElementAtIndex(idx);
                    i++;
                    continue;
                }
                cur = cur == null ? FindTop(so, seg) : FindChild(cur, seg);
                if (cur == null) return null;
            }
            return cur;
        }

        static SerializedProperty FindTop(SerializedObject so, string name)
        {
            return so.FindProperty(name)
                ?? so.FindProperty("m_" + Cap(name))
                ?? Scan(so.GetIterator(), name, true);
        }

        static SerializedProperty FindChild(SerializedProperty parent, string name)
        {
            return parent.FindPropertyRelative(name)
                ?? parent.FindPropertyRelative("m_" + Cap(name))
                ?? Scan(parent.Copy(), name, false, parent.GetEndProperty());
        }

        /// <summary>Case-insensitive match on name or display name among direct children.</summary>
        static SerializedProperty Scan(SerializedProperty it, string name, bool root, SerializedProperty end = null)
        {
            bool enter = true;
            while (it.Next(enter))
            {
                if (end != null && SerializedProperty.EqualContents(it, end)) break;
                enter = false;
                var n = Norm(name);
                if (Norm(it.name) == n || Norm(Fmt.Short(it.name)) == n || Norm(it.displayName) == n)
                    return it.Copy();
            }
            return null;
        }

        public static string Suggest(SerializedObject so, string key)
        {
            var names = new List<string>();
            var it = so.GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false) names.Add(SceneTextWriter.Key(it.name));
            var best = names.Select(n => (n, d: Distance(Props.Norm(n), Props.Norm(key)))).OrderBy(x => x.d).Take(3).Select(x => x.n);
            return $" — closest: {string.Join(", ", best)}";
        }

        public static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        public static string Norm(string s) => s.Replace(" ", "").Replace("_", "").ToLowerInvariant();

        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static void Assign(SerializedProperty p, JToken v)
        {
            try { AssignCore(p, v); }
            catch (Exception e) when (!(e is ArgumentException && e.Message.StartsWith(p.propertyPath)))
            {
                throw new ArgumentException($"{p.propertyPath} ({Describe(p)}): can't take {v.ToString(Newtonsoft.Json.Formatting.None)} — {e.Message}");
            }
        }

        static string Describe(SerializedProperty p) =>
            p.propertyType == SerializedPropertyType.ObjectReference ? Types.OfPPtr(p).Name + " reference" : p.propertyType.ToString();

        static void AssignCore(SerializedProperty p, JToken v)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (v.Type == JTokenType.String) { p.intValue = EnumByName(p, v.ToString()); return; }
                    if (p.type == "long" || p.type == "ulong") p.longValue = v.Value<long>(); else p.intValue = v.Value<int>();
                    return;
                case SerializedPropertyType.Boolean: p.boolValue = v.Value<bool>(); return;
                case SerializedPropertyType.Float:
                    if (p.type == "double") p.doubleValue = v.Value<double>(); else p.floatValue = v.Value<float>();
                    return;
                case SerializedPropertyType.String: p.stringValue = v.Type == JTokenType.Null ? null : v.ToString(); return;
                case SerializedPropertyType.Character: p.intValue = v.ToString()[0]; return;
                case SerializedPropertyType.Color: p.colorValue = Color(v); return;
                case SerializedPropertyType.Enum: AssignEnum(p, v); return;
                case SerializedPropertyType.LayerMask: p.intValue = Mask(v); return;
                case SerializedPropertyType.Vector2: { var f = Floats(v, 2); p.vector2Value = new Vector2(f[0], f[1]); return; }
                case SerializedPropertyType.Vector3: { var f = Floats(v, 3); p.vector3Value = new Vector3(f[0], f[1], f[2]); return; }
                case SerializedPropertyType.Vector4: { var f = Floats(v, 4); p.vector4Value = new Vector4(f[0], f[1], f[2], f[3]); return; }
                case SerializedPropertyType.Vector2Int: { var f = Floats(v, 2); p.vector2IntValue = new Vector2Int((int)f[0], (int)f[1]); return; }
                case SerializedPropertyType.Vector3Int: { var f = Floats(v, 3); p.vector3IntValue = new Vector3Int((int)f[0], (int)f[1], (int)f[2]); return; }
                case SerializedPropertyType.Quaternion: { var f = Floats(v, 3); p.quaternionValue = Quaternion.Euler(f[0], f[1], f[2]); return; }
                case SerializedPropertyType.Rect: { var f = Floats(v, 4); p.rectValue = new Rect(f[0], f[1], f[2], f[3]); return; }
                case SerializedPropertyType.RectInt: { var f = Floats(v, 4); p.rectIntValue = new RectInt((int)f[0], (int)f[1], (int)f[2], (int)f[3]); return; }
                case SerializedPropertyType.Bounds: { var f = Floats(v, 6); p.boundsValue = new Bounds(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5])); return; }
                case SerializedPropertyType.ObjectReference:
                    p.objectReferenceValue = v.Type == JTokenType.Null ? null : Refs.Resolve(v.ToString(), Types.OfPPtr(p));
                    return;
                case SerializedPropertyType.ArraySize: p.intValue = v.Value<int>(); return;
            }

            if (p.isArray && v is JArray arr)
            {
                p.arraySize = arr.Count;
                for (int i = 0; i < arr.Count; i++) Assign(p.GetArrayElementAtIndex(i), arr[i]);
                return;
            }
            if (v is JObject obj && (p.propertyType == SerializedPropertyType.Generic || p.propertyType == SerializedPropertyType.ManagedReference))
            {
                foreach (var kv in obj)
                {
                    var child = FindChild(p, kv.Key) ?? throw new ArgumentException($"{p.propertyPath} has no field '{kv.Key}'");
                    AssignCore(child, kv.Value);
                }
                return;
            }
            throw new ArgumentException($"Can't assign {v.Type} to {p.propertyPath} ({p.propertyType}/{p.type})");
        }

        static void AssignEnum(SerializedProperty p, JToken v)
        {
            if (v.Type == JTokenType.Integer) { p.intValue = v.Value<int>(); return; }
            var s = v.ToString();
            var names = p.enumNames;
            var display = p.enumDisplayNames;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], s, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(display[i].Replace(" ", ""), s.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                {
                    p.enumValueIndex = i;
                    return;
                }
            }
            // C# API names often differ from the serialized display names (LightShadows.Soft vs "Soft Shadows"):
            // accept a unique word match.
            var n = Norm(s);
            var partial = Enumerable.Range(0, display.Length)
                .Where(i => Norm(display[i]).StartsWith(n) || Norm(display[i]).Contains(n)).ToList();
            if (partial.Count == 1) { p.enumValueIndex = partial[0]; return; }
            throw new ArgumentException($"{p.propertyPath}: '{s}' is not one of {string.Join(", ", display)}");
        }

        /// <summary>
        /// Many native enums serialize as plain ints (Rigidbody2D.m_Interpolate, m_Constraints…). Resolve a name — or
        /// "A|B" flags — through the enum types the component's C# API exposes, preferring one whose name resembles the field.
        /// </summary>
        static int EnumByName(SerializedProperty p, string s)
        {
            var owner = p.serializedObject.targetObject.GetType();
            var enums = owner.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(pr => (pr.Name, pr.PropertyType)).Concat(owner.GetFields().Select(f => (f.Name, f.FieldType)))
                .Where(x => x.Item2.IsEnum).ToList();
            var parts = s.Split('|', ',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            var key = Norm(Fmt.Short(p.name));
            var fits = enums.Where(e => parts.All(part => Enum.GetNames(e.Item2).Any(n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(e => Norm(e.Item1).StartsWith(key.Substring(0, Math.Min(5, key.Length))) ? 1 : 0).ToList();
            if (fits.Count == 0)
                throw new ArgumentException($"{p.propertyPath}: '{s}' isn't a number or a known enum name for {owner.Name}");
            var t = fits[0].Item2;
            int value = 0;
            foreach (var part in parts) value |= Convert.ToInt32(Enum.Parse(t, part, true));
            return value;
        }

        public static Color Color(JToken v)
        {
            if (v is JArray a)
            {
                var f = a.Select(x => x.Value<float>()).ToArray();
                // Accept 0-255 ints as well as 0-1 floats.
                if (f.Any(x => x > 1f)) f = f.Select(x => x / 255f).ToArray();
                return new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
            }
            var s = v.ToString();
            if (ColorUtility.TryParseHtmlString(s, out var c) || ColorUtility.TryParseHtmlString("#" + s, out c)) return c;
            throw new ArgumentException($"Bad color '{s}' (use #RRGGBB[AA], a name like 'red', or [r,g,b,a])");
        }

        static int Mask(JToken v)
        {
            if (v.Type == JTokenType.Integer) return v.Value<int>();
            var str = v.ToString();
            if (str.StartsWith("layers(")) str = str.Substring(7).TrimEnd(')');
            var names = v is JArray a ? a.Select(x => x.ToString()).ToArray() : str.Split('|', ',');
            if (names.Length == 1 && names[0] == "Everything") return -1;
            if (names.Length == 1 && names[0] == "Nothing") return 0;
            int m = 0;
            foreach (var n in names)
            {
                var l = LayerMask.NameToLayer(n.Trim());
                if (l < 0) throw new ArgumentException($"Unknown layer '{n}'");
                m |= 1 << l;
            }
            return m;
        }

        public static float[] Floats(JToken v, int min)
        {
            float[] f;
            if (v is JArray a) f = a.Select(x => x.Value<float>()).ToArray();
            else if (v is JObject o) f = new[] { "x", "y", "z", "w" }.Where(o.ContainsKey).Select(k => o[k].Value<float>()).ToArray();
            else f = v.ToString().Trim('(', ')').Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (f.Length < min) Array.Resize(ref f, min);
            return f;
        }
    }
}
