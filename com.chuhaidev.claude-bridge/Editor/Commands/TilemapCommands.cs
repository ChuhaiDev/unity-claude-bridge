using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Editor.Authoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>Tilemaps as ASCII: read a level as text, paint it back as text.</summary>
    static class TilemapCommands
    {
        [CliCommand("tilemap_create",
            "Create a Tilemap (with TilemapRenderer) under a Grid. Reuses the first Grid in the active scene unless 'grid' is given; creates one if none exists.",
            Tags = new[] { "gameobjects", "claude" })]
        static object Create(
            [CliArg("name", "Tilemap GameObject name.", Required = true)] string name,
            [CliArg("grid", "Parent Grid handle (optional).")] string grid = null,
            [CliArg("sorting_order", "TilemapRenderer sorting order.")] int sortingOrder = 0,
            [CliArg("sorting_layer", "Sorting layer name.")] string sortingLayer = null,
            [CliArg("collider", "Add TilemapCollider2D (+ CompositeCollider2D with a static Rigidbody2D).")] bool collider = false)
        {
            using (new AuthoringUndoScope($"Claude: create tilemap {name}"))
            {
                Grid g = grid != null ? (Grid)Refs.Resolve(grid, typeof(Grid)) : Object.FindAnyObjectByType<Grid>();
                if (g == null)
                {
                    var gridGo = new GameObject("Grid", typeof(Grid));
                    Undo.RegisterCreatedObjectUndo(gridGo, "Create Grid");
                    g = gridGo.GetComponent<Grid>();
                }
                var go = new GameObject(name, typeof(Tilemap), typeof(TilemapRenderer));
                Undo.RegisterCreatedObjectUndo(go, "Create Tilemap");
                go.transform.SetParent(g.transform, false);
                var r = go.GetComponent<TilemapRenderer>();
                r.sortingOrder = sortingOrder;
                if (sortingLayer != null) r.sortingLayerName = sortingLayer;
                if (collider)
                {
                    var rb = go.AddComponent<Rigidbody2D>();
                    rb.bodyType = RigidbodyType2D.Static;
                    var tc = go.AddComponent<TilemapCollider2D>();
                    go.AddComponent<CompositeCollider2D>();
                    tc.compositeOperation = Collider2D.CompositeOperation.Merge;
                }
                EditorSceneManager.MarkSceneDirty(go.scene); Persist.Save(go.scene);
                return new { tilemap = Fmt.Ref(go) };
            }
        }

        [CliCommand("tilemap_paint",
            "Paint a tilemap from ASCII art. 'ascii' rows are top-to-bottom; the FIRST character of the FIRST row lands on cell 'origin' " +
            "(x grows right, y grows up, so row r is y = origin.y - r). 'legend' maps each character to a Tile asset path (or null to erase). " +
            "Characters not in the legend (e.g. space) leave cells untouched. 'clear' wipes the tilemap first. One undo step.",
            Tags = new[] { "gameobjects", "claude" })]
        static object Paint(
            [CliArg("target", "Tilemap handle.", Required = true)] string target,
            [CliArg("ascii", "Rows separated by \\n.", Required = true)] string ascii,
            [CliArg("legend", "{\"#\": \"Assets/Tiles/Wall.asset\", \".\": null}", Required = true)] JObject legend,
            [CliArg("origin", "[x,y] cell of the top-left character. Default [0, rows-1] (bottom row at y=0).")] int[] origin = null,
            [CliArg("clear", "Clear all tiles first.")] bool clear = false)
        {
            var tm = (Tilemap)Refs.Resolve(target, typeof(Tilemap));
            var map = new Dictionary<char, TileBase>();
            foreach (var kv in legend)
            {
                if (kv.Key.Length != 1) throw new ArgumentException($"Legend keys must be single characters, got '{kv.Key}'");
                map[kv.Key[0]] = kv.Value.Type == JTokenType.Null ? null : (TileBase)Refs.Resolve(kv.Value.ToString(), typeof(TileBase));
            }

            var rows = ascii.Replace("\r", "").Split('\n');
            if (rows.Length > 0 && rows[rows.Length - 1].Length == 0) rows = rows.Take(rows.Length - 1).ToArray();
            int ox = origin?[0] ?? 0, oy = origin?[1] ?? rows.Length - 1;

            var pos = new List<Vector3Int>();
            var tiles = new List<TileBase>();
            for (int r = 0; r < rows.Length; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    if (map.TryGetValue(rows[r][c], out var t))
                    {
                        pos.Add(new Vector3Int(ox + c, oy - r, 0));
                        tiles.Add(t);
                    }

            Undo.RegisterCompleteObjectUndo(tm, "Claude: paint tilemap");
            if (clear) tm.ClearAllTiles();
            tm.SetTiles(pos.ToArray(), tiles.ToArray());
            tm.CompressBounds();
            EditorSceneManager.MarkSceneDirty(tm.gameObject.scene); Persist.Save(tm.gameObject.scene);
            return new { painted = pos.Count, summary = Summary(tm) };
        }

        [CliCommand("tilemap_fill",
            "Fill (or erase with tile=null) a rectangle of cells, inclusive corners. Optionally only the border ('outline').",
            Tags = new[] { "gameobjects", "claude" })]
        static object Fill(
            [CliArg("target", "Tilemap handle.", Required = true)] string target,
            [CliArg("from", "[x,y] corner.", Required = true)] int[] from,
            [CliArg("to", "[x,y] opposite corner.", Required = true)] int[] to,
            [CliArg("tile", "Tile asset path, or omit/null to erase.")] string tile = null,
            [CliArg("outline", "Only paint the rectangle's border.")] bool outline = false)
        {
            var tm = (Tilemap)Refs.Resolve(target, typeof(Tilemap));
            var t = string.IsNullOrEmpty(tile) || tile == "null" ? null : (TileBase)Refs.Resolve(tile, typeof(TileBase));
            int x0 = Math.Min(from[0], to[0]), x1 = Math.Max(from[0], to[0]), y0 = Math.Min(from[1], to[1]), y1 = Math.Max(from[1], to[1]);
            var pos = new List<Vector3Int>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (!outline || x == x0 || x == x1 || y == y0 || y == y1)
                        pos.Add(new Vector3Int(x, y, 0));
            Undo.RegisterCompleteObjectUndo(tm, "Claude: fill tilemap");
            tm.SetTiles(pos.ToArray(), Enumerable.Repeat(t, pos.Count).ToArray());
            tm.CompressBounds();
            EditorSceneManager.MarkSceneDirty(tm.gameObject.scene); Persist.Save(tm.gameObject.scene);
            return new { painted = pos.Count, summary = Summary(tm) };
        }

        [CliCommand("tilemap_read",
            "Render a tilemap as ASCII (top row first, each row prefixed with its y) plus a legend you can pass straight back to tilemap_paint. '.' = empty.",
            Tags = new[] { "gameobjects", "claude" })]
        static object Read(
            [CliArg("target", "Tilemap handle.", Required = true)] string target,
            [CliArg("region", "Optional [xmin, ymin, xmax, ymax] (inclusive). Default: used bounds.")] int[] region = null)
        {
            var tm = (Tilemap)Refs.Resolve(target, typeof(Tilemap));
            tm.CompressBounds();
            var b = tm.cellBounds;
            int x0 = region?[0] ?? b.xMin, y0 = region?[1] ?? b.yMin, x1 = region?[2] ?? b.xMax - 1, y1 = region?[3] ?? b.yMax - 1;
            if (x1 - x0 > 400 || y1 - y0 > 400) throw new ArgumentException("Region too large (max 400x400); pass 'region'.");

            var chars = new Dictionary<TileBase, char>();
            var used = new HashSet<char> { '.', ' ' };
            var sb = new StringBuilder();
            sb.AppendLine($"{Fmt.Ref(tm.gameObject)} x={x0}..{x1} y={y0}..{y1}  (origin for paint: [{x0},{y1}])");
            var body = new StringBuilder();
            int pad = Math.Max(y0.ToString().Length, y1.ToString().Length);
            for (int y = y1; y >= y0; y--)
            {
                body.Append(y.ToString().PadLeft(pad)).Append(' ');
                for (int x = x0; x <= x1; x++)
                {
                    var t = tm.GetTile(new Vector3Int(x, y, 0));
                    if (t == null) { body.Append('.'); continue; }
                    if (!chars.TryGetValue(t, out var ch)) chars[t] = ch = PickChar(t.name, used);
                    body.Append(ch);
                }
                body.AppendLine();
            }
            var legend = new JObject();
            foreach (var kv in chars) legend[kv.Value.ToString()] = Fmt.Ref(kv.Key);
            sb.Append("legend ").AppendLine(legend.ToString(Newtonsoft.Json.Formatting.None));
            sb.Append(body);
            return new { text = sb.ToString() };
        }

        static char PickChar(string name, HashSet<char> used)
        {
            var candidates = name.Where(char.IsLetterOrDigit).SelectMany(c => new[] { char.ToUpperInvariant(c), char.ToLowerInvariant(c) })
                .Concat("#@%&*+=~ox0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");
            foreach (var c in candidates)
                if (used.Add(c)) return c;
            return '?';
        }

        public static string Summary(Tilemap tm)
        {
            var b = tm.cellBounds;
            var counts = new Dictionary<string, int>();
            int n = 0;
            foreach (var p in b.allPositionsWithin)
            {
                var t = tm.GetTile(p);
                if (t == null) continue;
                n++;
                counts[t.name] = counts.TryGetValue(t.name, out var c) ? c + 1 : 1;
            }
            if (n == 0) return "empty";
            return $"cells={n} x={b.xMin}..{b.xMax - 1} y={b.yMin}..{b.yMax - 1} tiles={{{string.Join(",", counts.Select(kv => kv.Key + ":" + kv.Value))}}}";
        }
    }
}
