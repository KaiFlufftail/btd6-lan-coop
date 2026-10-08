using System.IO;
using System.Text;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.ModOptions;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(BTD6UiMap.Main), BTD6UiMap.ModHelperData.Name,
    BTD6UiMap.ModHelperData.Version, BTD6UiMap.ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonOptionalDependencies("BTD_Mod_Helper")]

namespace BTD6UiMap;

// Writes out what the in-game interface actually looks like, so panels can be placed
// against the real thing rather than guessed at.
public class Main : BloonsTD6Mod
{
    private static readonly ModSettingHotkey DumpKey = new(KeyCode.U, HotkeyModifier.Shift)
    {
        displayName = "Write the interface tree to UiMap.txt"
    };

    public override void OnApplicationStart() => ModHelper.Msg<Main>("UI Map loaded, Shift+U writes UiMap.txt.");

    public override void OnUpdate()
    {
        if (!DumpKey.JustPressed()) return;

        var text = new StringBuilder();
        var canvases = UnityEngine.Resources.FindObjectsOfTypeAll<Canvas>();
        var written = 0;

        foreach (var canvas in canvases)
        {
            if (canvas is null || !canvas.gameObject.scene.IsValid()) continue;

            text.AppendLine($"=== canvas {canvas.name} (sorting {canvas.sortingOrder}, " +
                            $"{(canvas.gameObject.active ? "on" : "off")})");
            Walk(canvas.transform, 1, text);
            written++;
        }

        var path = Path.Combine(MelonEnvironment.GameRootDirectory, "UiMap.txt");
        File.WriteAllText(path, text.ToString());
        ModHelper.Msg<Main>($"[uimap] {written} canvases written to {path}");
    }

    /// Depth is capped because the tower and bloon trees run very deep and the interesting
    /// furniture all sits near the top.
    private static void Walk(Transform node, int depth, StringBuilder text)
    {
        if (depth > 6) return;

        for (var i = 0; i < node.childCount; i++)
        {
            var child = node.GetChild(i);
            var rect = child.GetComponent<RectTransform>();
            var indent = new string(' ', depth * 2);
            var state = child.gameObject.active ? "on" : "off";

            text.Append($"{indent}{child.name} [{state}]");
            if (rect is not null)
            {
                text.Append($" pos {rect.anchoredPosition.x:0},{rect.anchoredPosition.y:0}" +
                            $" size {rect.sizeDelta.x:0}x{rect.sizeDelta.y:0}" +
                            $" anchor {rect.anchorMin.x:0.##},{rect.anchorMin.y:0.##}");
            }

            foreach (var component in child.GetComponents<Component>())
            {
                if (component is null || component is RectTransform || component is Transform) continue;
                text.Append($" <{component.GetIl2CppType().Name}>");
            }

            text.AppendLine();
            Walk(child, depth + 1, text);
        }
    }
}
