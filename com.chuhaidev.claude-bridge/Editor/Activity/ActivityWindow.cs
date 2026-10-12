using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>
    /// The agent's client (tools/u) appends one JSON line per call to Library/ClaudeBridge/activity.jsonl.
    /// This window tails that file so the human can see — and click through to — everything the agent did.
    /// </summary>
    static class ActivityFeed
    {
        public static string LogPath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library/ClaudeBridge/activity.jsonl");

        public static void ShowToast(string text)
        {
            foreach (SceneView sv in SceneView.sceneViews)
                sv.ShowNotification(new GUIContent("Claude: " + text), 4.0);
        }

        public static Object ResolveRef(string r)
        {
            try
            {
                if (r.StartsWith("GlobalObjectId") && GlobalObjectId.TryParse(r, out var gid))
                    return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                return Refs.Resolve(r);
            }
            catch { return null; }
        }
    }

    class ActivityWindow : EditorWindow
    {
        class Entry
        {
            public string time, kind, cmd, args, err;
            public bool ok, write;
            public long ms;
            public string[] refs;
        }

        readonly List<Entry> m_Entries = new List<Entry>();
        long m_ReadPos;
        Vector2 m_Scroll;
        bool m_StickToBottom = true;
        [SerializeField] bool m_Follow = true;
        [SerializeField] bool m_ShowReads;
        DateTime m_LastActivity = DateTime.MinValue;
        GUIStyle m_Note, m_Args, m_Err;

        [MenuItem("Window/Claude Activity %#k")]
        public static void Open() => GetWindow<ActivityWindow>("Claude Activity").Show();

        void OnEnable()
        {
            m_Entries.Clear();
            m_ReadPos = 0;
            Poll(initial: true);
        }

        void OnInspectorUpdate()
        {
            if (Poll(initial: false)) Repaint();
            else if ((DateTime.Now - m_LastActivity).TotalSeconds < 12) Repaint(); // keep the "active" badge fresh
        }

        bool Poll(bool initial)
        {
            var path = ActivityFeed.LogPath;
            if (!File.Exists(path)) return false;
            var len = new FileInfo(path).Length;
            if (len < m_ReadPos) { m_Entries.Clear(); m_ReadPos = 0; } // truncated / cleared
            if (len == m_ReadPos) return false;

            string chunk;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // On first load only show the tail.
                if (initial && len > 256 * 1024) m_ReadPos = len - 256 * 1024;
                fs.Seek(m_ReadPos, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs)) chunk = sr.ReadToEnd();
            }
            var lastNl = chunk.LastIndexOf('\n');
            if (lastNl < 0) return false;
            m_ReadPos += System.Text.Encoding.UTF8.GetByteCount(chunk.Substring(0, lastNl + 1));

            Entry newestWrite = null;
            foreach (var line in chunk.Substring(0, lastNl).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var j = JObject.Parse(line);
                    var e = new Entry
                    {
                        time = (string)j["t"], kind = (string)j["kind"] ?? "cmd", cmd = (string)j["cmd"], args = (string)j["args"],
                        err = (string)j["err"], ok = (bool?)j["ok"] ?? true, write = (bool?)j["write"] ?? false,
                        ms = (long?)j["ms"] ?? 0, refs = j["refs"]?.Select(x => (string)x).ToArray() ?? Array.Empty<string>(),
                    };
                    m_Entries.Add(e);
                    if (!initial && e.write && e.refs.Length > 0) newestWrite = e;
                }
                catch { /* partial or foreign line */ }
            }
            if (m_Entries.Count > 1000) m_Entries.RemoveRange(0, m_Entries.Count - 1000);
            if (!initial) m_LastActivity = DateTime.Now;
            if (m_Follow && newestWrite != null)
            {
                var o = newestWrite.refs.Select(ActivityFeed.ResolveRef).FirstOrDefault(x => x != null);
                if (o != null) EditorGUIUtility.PingObject(o);
                SceneView.RepaintAll();
            }
            if (m_StickToBottom) m_Scroll.y = float.MaxValue;
            return true;
        }

        void Styles()
        {
            if (m_Note != null) return;
            m_Note = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true, fontSize = 12 };
            m_Args = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = false };
            m_Err = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            m_Err.normal.textColor = new Color(1f, .45f, .4f);
        }

        void OnGUI()
        {
            Styles();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var active = (DateTime.Now - m_LastActivity).TotalSeconds < 10;
                var prev = GUI.color;
                GUI.color = active ? new Color(.4f, 1f, .5f) : Color.gray;
                GUILayout.Label(active ? "● working" : "○ idle", EditorStyles.toolbarButton, GUILayout.Width(70));
                GUI.color = prev;
                m_Follow = GUILayout.Toggle(m_Follow, new GUIContent("Follow", "Ping objects as Claude changes them"), EditorStyles.toolbarButton);
                m_ShowReads = GUILayout.Toggle(m_ShowReads, new GUIContent("Show reads", "Also list read-only calls (snapshot, inspect, screenshots…)"), EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton))
                {
                    try { File.WriteAllText(ActivityFeed.LogPath, ""); } catch { }
                    m_Entries.Clear();
                    m_ReadPos = 0;
                }
            }

            if (m_Entries.Count == 0)
            {
                EditorGUILayout.HelpBox("No activity yet. Everything Claude does in this editor shows up here; click an entry to select the objects it touched.", MessageType.Info);
                return;
            }

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var e in m_Entries)
            {
                if (e.kind == "cmd" && !e.write && !m_ShowReads && e.ok) continue;
                if (e.kind == "note")
                {
                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField($"▶ {e.args}", m_Note);
                    continue;
                }
                var rect = EditorGUILayout.BeginVertical();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(e.time ?? "", EditorStyles.miniLabel, GUILayout.Width(52));
                    GUILayout.Label(e.ok ? "✓" : "✗", GUILayout.Width(14));
                    GUILayout.Label(e.cmd, e.write ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (e.ms > 0) GUILayout.Label($"{e.ms} ms", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                }
                if (!string.IsNullOrEmpty(e.args)) EditorGUILayout.LabelField(e.args, m_Args);
                if (!e.ok && !string.IsNullOrEmpty(e.err)) EditorGUILayout.LabelField(e.err, m_Err);
                EditorGUILayout.EndVertical();

                if (e.refs.Length > 0)
                {
                    EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
                    if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                    {
                        var objs = e.refs.Select(ActivityFeed.ResolveRef).Where(o => o != null).ToArray();
                        if (objs.Length > 0)
                        {
                            Selection.objects = objs;
                            EditorGUIUtility.PingObject(objs[0]);
                            if (Event.current.clickCount == 2 && SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
                        }
                        Event.current.Use();
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            // Stick to the bottom unless the user scrolled up.
            if (Event.current.type == EventType.ScrollWheel && Event.current.delta.y != 0) m_StickToBottom = Event.current.delta.y > 0;
        }
    }
}
