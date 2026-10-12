using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Editor.Authoring;
using UnityEditor;
#if CLAUDE_2D_SPRITE
using UnityEditor.U2D.Sprites;
#endif
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>2D asset authoring: placeholder sprites, sprite import/slicing, tiles.</summary>
    static class SpriteCommands
    {
        [CliCommand("make_sprite",
            "Generate a placeholder sprite PNG (square, circle, triangle, diamond, capsule, rounded) as a REAL asset, " +
            "imported as a pixel-art Sprite (point filter, uncompressed). Default white so it can be tinted via SpriteRenderer.color. " +
            "Overwrites an existing file at the same path (GUID kept, so references survive).",
            Tags = new[] { "assets", "claude" })]
        static object MakeSprite(
            [CliArg("path", "Assets/…/name.png", Required = true)] string path,
            [CliArg("shape", "square | circle | triangle | diamond | capsule | rounded")] string shape = "square",
            [CliArg("size", "[width, height] in pixels.")] int[] size = null,
            [CliArg("color", "Fill color (#hex, name, or [r,g,b,a]).")] JToken color = null,
            [CliArg("outline", "Optional 1px outline color.")] JToken outline = null,
            [CliArg("ppu", "Pixels per unit. Default = width (sprite is 1 unit wide).")] int ppu = 0,
            [CliArg("pivot", "center | bottom | top | left | right | [x,y] normalized.")] JToken pivot = null)
        {
            path = ProjectPaths.Resolve(path, out var err) ?? throw new ArgumentException(err);
            if (!path.EndsWith(".png")) throw new ArgumentException("path must end in .png");
            int w = size?.Length > 0 ? size[0] : 32, h = size?.Length > 1 ? size[1] : w;
            var fill = color != null ? Props.Color(color) : Color.white;
            Color? line = outline != null ? Props.Color(outline) : (Color?)null;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool In(int xx, int yy) => xx >= 0 && yy >= 0 && xx < w && yy < h && Inside(shape, xx, yy, w, h);
                    if (!In(x, y)) { px[y * w + x] = new Color32(0, 0, 0, 0); continue; }
                    bool edge = line.HasValue && (!In(x - 1, y) || !In(x + 1, y) || !In(x, y - 1) || !In(x, y + 1));
                    px[y * w + x] = edge ? line.Value : fill;
                }
            tex.SetPixels32(px);
            tex.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ProjectPaths.ProjectRoot, path)));
            File.WriteAllBytes(Path.Combine(ProjectPaths.ProjectRoot, path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = TextureImporterType.Sprite;
            imp.spriteImportMode = SpriteImportMode.Single;
            imp.spritePixelsPerUnit = ppu > 0 ? ppu : w;
            imp.filterMode = FilterMode.Point;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.mipmapEnabled = false;
            imp.alphaIsTransparency = true;
            var settings = new TextureImporterSettings();
            imp.ReadTextureSettings(settings);
            var (align, pv) = Pivot(pivot);
            settings.spriteAlignment = (int)align;
            settings.spritePivot = pv;
            imp.SetTextureSettings(settings);
            imp.SaveAndReimport();

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            return new { text = $"+ {Fmt.Ref(sprite)} ({w}x{h}px, {Fmt.F(w / imp.spritePixelsPerUnit)}x{Fmt.F(h / imp.spritePixelsPerUnit)} units)" };
        }

        static bool Inside(string shape, int x, int y, int w, int h)
        {
            // Pixel centers in normalized [-1,1] space.
            float u = (x + .5f) / w * 2 - 1, v = (y + .5f) / h * 2 - 1;
            switch ((shape ?? "square").ToLowerInvariant())
            {
                case "square": return true;
                case "circle": return u * u + v * v <= 1f;
                case "diamond": return Mathf.Abs(u) + Mathf.Abs(v) <= 1f;
                case "triangle": return v >= -1f && Mathf.Abs(u) <= (1f - v) / 2f; // apex at top
                case "capsule":
                    {
                        // Vertical capsule if taller, horizontal otherwise.
                        float r = Mathf.Min(w, h) / 2f, px = x + .5f, py = y + .5f;
                        float cx = Mathf.Clamp(px, r, w - r), cy = Mathf.Clamp(py, r, h - r);
                        return (px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r;
                    }
                case "rounded":
                    {
                        float r = Mathf.Min(w, h) / 4f, px = x + .5f, py = y + .5f;
                        float cx = Mathf.Clamp(px, r, w - r), cy = Mathf.Clamp(py, r, h - r);
                        return (px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r;
                    }
                default: throw new ArgumentException($"Unknown shape '{shape}'");
            }
        }

        static (SpriteAlignment, Vector2) Pivot(JToken p)
        {
            if (p == null) return (SpriteAlignment.Center, new Vector2(.5f, .5f));
            if (p is JArray) { var f = Props.Floats(p, 2); return (SpriteAlignment.Custom, new Vector2(f[0], f[1])); }
            switch (p.ToString().ToLowerInvariant())
            {
                case "center": return (SpriteAlignment.Center, new Vector2(.5f, .5f));
                case "bottom": return (SpriteAlignment.BottomCenter, new Vector2(.5f, 0));
                case "top": return (SpriteAlignment.TopCenter, new Vector2(.5f, 1));
                case "left": return (SpriteAlignment.LeftCenter, new Vector2(0, .5f));
                case "right": return (SpriteAlignment.RightCenter, new Vector2(1, .5f));
                case "bottomleft": return (SpriteAlignment.BottomLeft, Vector2.zero);
                default: throw new ArgumentException($"Unknown pivot '{p}'");
            }
        }

        [CliCommand("sprite_import",
            "Configure a texture as Sprite(s): pixels-per-unit, filter, compression, pivot, and optional grid slicing " +
            "(cell size or columns×rows; fully transparent cells skipped). Sliced sprites are named <file>_<n> in reading order. " +
            "Pass a folder to apply the same settings to every texture inside it.",
            Tags = new[] { "assets/import", "claude" })]
        static object SpriteImport(
            [CliArg("path", "Texture path, or a folder.", Required = true)] string path,
            [CliArg("ppu", "Pixels per unit.")] float ppu = 0,
            [CliArg("filter", "point | bilinear | trilinear")] string filter = null,
            [CliArg("compression", "none | low | normal | high")] string compression = null,
            [CliArg("pivot", "center | bottom | top | left | right | bottomleft | [x,y]")] JToken pivot = null,
            [CliArg("cell", "Slice into a grid of [w,h]-pixel cells.")] int[] cell = null,
            [CliArg("grid", "Slice into [columns, rows].")] int[] grid = null,
            [CliArg("offset", "Grid offset [x,y] px.")] int[] offset = null,
            [CliArg("padding", "Grid padding [x,y] px between cells.")] int[] padding = null,
            [CliArg("keep_empty", "Keep fully transparent cells when slicing.")] bool keepEmpty = false)
        {
            path = ProjectPaths.Resolve(path, out var err) ?? throw new ArgumentException(err);
            var paths = AssetDatabase.IsValidFolder(path)
                ? AssetDatabase.FindAssets("t:Texture2D", new[] { path }).Select(AssetDatabase.GUIDToAssetPath).ToArray()
                : new[] { path };
            var sb = new StringBuilder();
            foreach (var p in paths)
                sb.AppendLine(ImportOne(p, ppu, filter, compression, pivot, cell, grid, offset, padding, keepEmpty));
            return new { text = sb.ToString() };
        }

        static string ImportOne(string path, float ppu, string filter, string compression, JToken pivot,
            int[] cell, int[] grid, int[] offset, int[] padding, bool keepEmpty)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter ?? throw new ArgumentException($"{path} is not a texture");
#if !CLAUDE_2D_SPRITE
            if (cell != null || grid != null)
                throw new InvalidOperationException("Grid slicing needs the 2D Sprite package: u package_add com.unity.2d.sprite confirm=true");
#endif
            imp.textureType = TextureImporterType.Sprite;
            imp.alphaIsTransparency = true;
            imp.mipmapEnabled = false;
            if (ppu > 0) imp.spritePixelsPerUnit = ppu;
            if (filter != null) imp.filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), filter, true);
            if (compression != null)
                imp.textureCompression = compression.ToLowerInvariant() switch
                {
                    "none" => TextureImporterCompression.Uncompressed,
                    "low" => TextureImporterCompression.CompressedLQ,
                    "high" => TextureImporterCompression.CompressedHQ,
                    _ => TextureImporterCompression.Compressed,
                };
            var (align, pv) = Pivot(pivot);
            bool slicing = cell != null || grid != null;

            if (!slicing)
            {
                if (imp.spriteImportMode == SpriteImportMode.None) imp.spriteImportMode = SpriteImportMode.Single;
                if (pivot != null)
                {
                    var s = new TextureImporterSettings();
                    imp.ReadTextureSettings(s);
                    s.spriteAlignment = (int)align;
                    s.spritePivot = pv;
                    imp.SetTextureSettings(s);
                }
                imp.SaveAndReimport();
                return $"{path}: {imp.spriteImportMode} ppu={imp.spritePixelsPerUnit} filter={imp.filterMode}";
            }

#if !CLAUDE_2D_SPRITE
            throw new InvalidOperationException("unreachable");
#else
            // Read pixels straight from disk so the importer's readability/compression don't matter.
            var tex = new Texture2D(2, 2);
            tex.LoadImage(File.ReadAllBytes(Path.Combine(ProjectPaths.ProjectRoot, path)));
            int W = tex.width, H = tex.height;
            int ox = offset?[0] ?? 0, oy = offset?[1] ?? 0, padX = padding?[0] ?? 0, padY = padding?[1] ?? 0;
            int cw, ch;
            if (cell != null) { cw = cell[0]; ch = cell.Length > 1 ? cell[1] : cell[0]; }
            else { cw = (W - ox - padX * (grid[0] - 1)) / grid[0]; ch = (H - oy - padY * (grid[1] - 1)) / grid[1]; }

            imp.spriteImportMode = SpriteImportMode.Multiple;
            var factory = new SpriteDataProviderFactories();
            factory.Init();
            var dp = factory.GetSpriteEditorDataProviderFromObject(imp);
            dp.InitSpriteEditorDataProvider();

            // Reuse existing sprite IDs by name so references to slices survive a re-slice.
            var existing = dp.GetSpriteRects().ToDictionary(r => r.name, r => r.spriteID);
            var baseName = Path.GetFileNameWithoutExtension(path);
            var rects = new List<SpriteRect>();
            int n = 0;
            // Reading order: top row first, left to right (texture y=0 is the bottom).
            for (int top = oy; top + ch <= H; top += ch + padY)
                for (int x = ox; x + cw <= W; x += cw + padX)
                {
                    int y = H - top - ch;
                    if (!keepEmpty && Empty(tex, x, y, cw, ch)) continue;
                    var name = $"{baseName}_{n++}";
                    rects.Add(new SpriteRect
                    {
                        name = name,
                        rect = new Rect(x, y, cw, ch),
                        alignment = align,
                        pivot = pv,
                        spriteID = existing.TryGetValue(name, out var id) ? id : GUID.Generate(),
                    });
                }
            Object.DestroyImmediate(tex);

            dp.SetSpriteRects(rects.ToArray());
            var names = dp.GetDataProvider<ISpriteNameFileIdDataProvider>();
            names?.SetNameFileIdPairs(rects.Select(r => new SpriteNameFileIdPair(r.name, r.spriteID)).ToList());
            dp.Apply();
            imp.SaveAndReimport();
            return $"{path}: {rects.Count} sprites of {cw}x{ch}px ({baseName}_0..{baseName}_{rects.Count - 1}) ppu={imp.spritePixelsPerUnit} filter={imp.filterMode}";
#endif
        }

        static bool Empty(Texture2D t, int x, int y, int w, int h)
        {
            var px = t.GetPixels(x, y, w, h);
            for (int i = 0; i < px.Length; i++) if (px[i].a > 0.01f) return false;
            return true;
        }

        [CliCommand("make_tiles",
            "Create Tile assets for tilemaps from sprites. 'sprites' is a sprite ref, or a texture path (one tile per sprite in it). " +
            "Tiles are written to 'folder' named after their sprite. Existing tiles are updated in place.",
            Tags = new[] { "assets", "claude" })]
        static object MakeTiles(
            [CliArg("sprites", "Sprite ref or texture path.", Required = true)] string sprites,
            [CliArg("folder", "Destination folder, e.g. Assets/Tiles.", Required = true)] string folder,
            [CliArg("collider", "none | sprite | grid")] string collider = "sprite",
            [CliArg("color", "Optional tint.")] JToken color = null)
        {
            folder = ProjectPaths.Resolve(folder, out var err) ?? throw new ArgumentException(err);
            Folders.Ensure(folder);
            Sprite[] list;
            if (sprites.Contains('#')) list = new[] { (Sprite)Refs.Resolve(sprites, typeof(Sprite)) };
            else list = AssetDatabase.LoadAllAssetsAtPath(sprites).OfType<Sprite>().OrderBy(s => s.name, new NaturalOrder()).ToArray();
            if (list.Length == 0) throw new ArgumentException($"No sprites in {sprites}");

            var made = new List<string>();
            // One import pass for all tiles instead of one refresh per CreateAsset.
            AssetDatabase.StartAssetEditing();
            try
            {
            foreach (var s in list)
            {
                var path = $"{folder}/{s.name}.asset";
                var tile = AssetDatabase.LoadAssetAtPath<Tile>(path);
                bool isNew = tile == null;
                if (isNew) tile = ScriptableObject.CreateInstance<Tile>();
                tile.sprite = s;
                tile.colliderType = (Tile.ColliderType)Enum.Parse(typeof(Tile.ColliderType), collider, true);
                if (color != null) tile.color = Props.Color(color);
                if (isNew) AssetDatabase.CreateAsset(tile, path); else EditorUtility.SetDirty(tile);
                made.Add(path);
            }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.SaveAssets();
            return new { text = made.Count <= 6 ? string.Join("\n", made.Select(m => "+ " + m)) : $"+ {made.Count} tiles in {folder}/ ({Path.GetFileName(made[0])} … {Path.GetFileName(made[made.Count - 1])})" };
        }

        class NaturalOrder : IComparer<string>
        {
            public int Compare(string a, string b) => EditorUtility.NaturalCompare(a, b);
        }
    }
}
