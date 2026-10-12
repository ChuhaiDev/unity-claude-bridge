using System;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    static class World
    {
        /// <summary>World bounds of a target's renderers/colliders, or of everything in the loaded scenes.</summary>
        public static Bounds Bounds(string target)
        {
            var roots = target != null
                ? new[] { Refs.GameObject(target) }
                : Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded)
                    .SelectMany(s => s.GetRootGameObjects()).ToArray();
            Bounds? b = null;
            void Add(Bounds x) { if (b.HasValue) { var v = b.Value; v.Encapsulate(x); b = v; } else b = x; }
            foreach (var go in roots)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (r is TilemapRenderer && r.GetComponent<Tilemap>().GetUsedTilesCount() == 0) continue;
                    if (r is ParticleSystemRenderer) continue;
                    Add(r.bounds);
                }
                foreach (var c in go.GetComponentsInChildren<Collider2D>()) Add(c.bounds);
                foreach (var c in go.GetComponentsInChildren<Collider>()) Add(c.bounds);
            }
            if (!b.HasValue && target != null) b = new Bounds(roots[0].transform.position, Vector3.one * 4);
            return b ?? new Bounds(Vector3.zero, new Vector3(16, 9, 0));
        }
    }

    static class CaptureCommands
    {
        [CliCommand("capture_area",
            "Render a world-space area with a temporary orthographic copy of the main camera (same background/renderer, " +
            "does NOT touch the user's views). Frame a target's bounds, a world rect, or everything. Returns base64 PNG in 'image'.",
            Tags = new[] { "capture", "claude" })]
        static object CaptureArea(
            [CliArg("target", "Handle to frame. Omit (and omit rect) to frame the whole scene.")] string target = null,
            [CliArg("rect", "World rect [x, y, width, height] (x,y = bottom-left).")] float[] rect = null,
            [CliArg("width", "Image width px.")] int width = 960,
            [CliArg("height", "Image height px (0 = match the area's aspect).")] int height = 0,
            [CliArg("padding", "Margin around the area in world units.")] float padding = 1f)
        {
            var b = rect != null
                ? new Bounds(new Vector3(rect[0] + rect[2] / 2, rect[1] + rect[3] / 2), new Vector3(rect[2], rect[3]))
                : World.Bounds(target);
            if (rect == null) b.Expand(padding * 2);
            float bw = Mathf.Max(b.size.x, 0.1f), bh = Mathf.Max(b.size.y, 0.1f);
            width = Mathf.Clamp(width, 16, 4096);
            if (height <= 0) height = Mathf.Clamp(Mathf.RoundToInt(width * bh / bw), 16, 4096);

            var src = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
            var go = new GameObject("__claude_capture") { hideFlags = HideFlags.HideAndDontSave };
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            try
            {
                var cam = go.AddComponent<Camera>();
                if (src != null) cam.CopyFrom(src);
                else { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.gray; }
                cam.orthographic = true;
                cam.aspect = (float)width / height;
                cam.orthographicSize = Mathf.Max(bh / 2, bw / 2 / cam.aspect);
                var z = src != null ? src.transform.position.z : -10f;
                cam.transform.SetPositionAndRotation(new Vector3(b.center.x, b.center.y, z), Quaternion.identity);
                cam.targetTexture = rt;
                cam.enabled = false;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                Object.DestroyImmediate(tex);
                return new
                {
                    image = Convert.ToBase64String(png), width, height,
                    area = $"x={Fmt.F(b.min.x)}..{Fmt.F(b.max.x)} y={Fmt.F(b.min.y)}..{Fmt.F(b.max.y)}",
                    unitsPerPixel = Fmt.F(cam.orthographicSize * 2 / height),
                };
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(go);
            }
        }
    }
}
