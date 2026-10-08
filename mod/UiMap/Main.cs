using System.Collections.Generic;
using System.IO;
using System.Text;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.ModOptions;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.UI;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

[assembly: MelonInfo(typeof(BTD6UiMap.Main), BTD6UiMap.ModHelperData.Name,
    BTD6UiMap.ModHelperData.Version, BTD6UiMap.ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonOptionalDependencies("BTD_Mod_Helper")]

namespace BTD6UiMap;

// A reference kit for building interface mods: where everything is, and what it is drawn
// with. Reads the live game and writes files; changes nothing.
public class Main : BloonsTD6Mod
{
    private static readonly ModSettingHotkey MapKey = new(KeyCode.U, HotkeyModifier.Shift)
    {
        displayName = "Write the interface tree to UiMap.txt"
    };

    private static readonly ModSettingHotkey SpritesKey = new(KeyCode.I, HotkeyModifier.Shift)
    {
        displayName = "Save every loaded sprite as a png"
    };

    private static string OutputDir => Path.Combine(MelonEnvironment.GameRootDirectory, "UiKit");

    public override void OnApplicationStart()
    {
        Directory.CreateDirectory(OutputDir);
        ModHelper.Msg<Main>($"UI Kit loaded. Shift+U maps the interface, Shift+I saves sprites, into {OutputDir}");
    }

    public override void OnUpdate()
    {
        if (MapKey.JustPressed()) WriteMap();
        if (SpritesKey.JustPressed()) WriteSprites();
    }

    private static void WriteMap()
    {
        var text = new StringBuilder();
        text.AppendLine("Every interface object in the scene.");
        text.AppendLine("pos and size are the RectTransform's anchoredPosition and sizeDelta,");
        text.AppendLine("anchor is anchorMin, and sprite names are what an Image is drawing.");
        text.AppendLine();

        var canvases = UnityEngine.Resources.FindObjectsOfTypeAll<Canvas>();
        var written = 0;

        foreach (var canvas in canvases)
        {
            if (canvas is null || !canvas.gameObject.scene.IsValid()) continue;

            text.AppendLine($"=== canvas {canvas.name}  sorting {canvas.sortingOrder}  " +
                            $"{(canvas.gameObject.active ? "on" : "off")}");
            Walk(canvas.transform, canvas.name, 1, text);
            text.AppendLine();
            written++;
        }

        var path = Path.Combine(OutputDir, "UiMap.txt");
        File.WriteAllText(path, text.ToString());
        ModHelper.Msg<Main>($"[uikit] {written} canvases written to {path}");
    }

    private static void Walk(Transform node, string path, int depth, StringBuilder text)
    {
        for (var i = 0; i < node.childCount; i++)
        {
            var child = node.GetChild(i);
            var name = child.name;

            // The buff icon list is hundreds of identical hidden entries and drowns the file.
            if (name.StartsWith("BuffIcon")) continue;

            var childPath = $"{path}/{name}";
            var indent = new string(' ', depth * 2);
            text.Append($"{indent}{name} [{(child.gameObject.active ? "on" : "off")}]");

            var rect = child.GetComponent<RectTransform>();
            if (rect is not null)
            {
                text.Append($" pos {rect.anchoredPosition.x:0},{rect.anchoredPosition.y:0}" +
                            $" size {rect.sizeDelta.x:0}x{rect.sizeDelta.y:0}" +
                            $" anchor {rect.anchorMin.x:0.##},{rect.anchorMin.y:0.##}" +
                            $" scale {rect.localScale.x:0.##}");
            }

            var image = child.GetComponent<Image>();
            if (image is not null && image.sprite is not null) text.Append($" sprite '{image.sprite.name}'");

            foreach (var component in child.GetComponents<Component>())
            {
                if (component is null || component is RectTransform || component is Transform) continue;
                text.Append($" <{component.GetIl2CppType().Name}>");
            }

            text.AppendLine($"    path: {childPath}");
            Walk(child, childPath, depth + 1, text);
        }
    }

    /// Sprites live in atlases and are not readable, so each one is drawn into a render
    /// texture and read back out before saving.
    private static void WriteSprites()
    {
        var folder = Path.Combine(OutputDir, "sprites");
        Directory.CreateDirectory(folder);

        var index = new StringBuilder();
        var seen = new HashSet<string>();
        var saved = 0;
        var skipped = 0;

        foreach (var sprite in UnityEngine.Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (sprite is null || string.IsNullOrEmpty(sprite.name) || !seen.Add(sprite.name)) continue;

            try
            {
                var png = Encode(sprite);
                if (png is null) { skipped++; continue; }

                var file = Safe(sprite.name) + ".png";
                File.WriteAllBytes(Path.Combine(folder, file), png);
                index.AppendLine($"{sprite.name}\t{file}\t{sprite.rect.width:0}x{sprite.rect.height:0}");
                saved++;
            }
            catch
            {
                skipped++;
            }
        }

        File.WriteAllText(Path.Combine(OutputDir, "sprites.txt"), index.ToString());
        ModHelper.Msg<Main>($"[uikit] {saved} sprites saved to {folder}, {skipped} skipped");
    }

    private static byte[] Encode(Sprite sprite)
    {
        var texture = sprite.texture;
        if (texture is null) return null;

        var width = (int) sprite.rect.width;
        var height = (int) sprite.rect.height;
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048) return null;

        var target = RenderTexture.GetTemporary(texture.width, texture.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;

        Graphics.Blit(texture, target);
        RenderTexture.active = target;

        var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(sprite.rect.x, texture.height - sprite.rect.y - height, width, height), 0, 0);
        readable.Apply();

        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);

        var png = ImageConversion.EncodeToPNG(readable);
        UnityEngine.Object.Destroy(readable);
        return png;
    }

    private static string Safe(string name)
    {
        var clean = new StringBuilder();
        foreach (var c in name)
        {
            clean.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }

        return clean.ToString();
    }
}
