using System;
using System.IO;
using BTD_Mod_Helper;
using HarmonyLib;
using MelonLoader.Utils;

namespace BTD6Versus;

/// Everything this mod does goes through here, because the failures that cost the most
/// time were the silent ones: a patch that never attached, a hook that threw inside the
/// il2cpp bridge, a lane that was never found. Each line also lands in VersusLog.txt next
/// to the game, so a session can be read back without digging through MelonLoader's log.
public static class Diagnostics
{
    private static readonly string LogPath =
        Path.Combine(MelonEnvironment.GameRootDirectory, "VersusLog.txt");

    private static bool started;

    public static void Begin(string version)
    {
        try
        {
            File.WriteAllText(LogPath, $"BTD6 Versus {version} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            started = true;
        }
        catch (Exception e)
        {
            ModHelper.Warning<Main>($"[vs] cannot write {LogPath}: {e.Message}");
        }
    }

    public static void Say(string line)
    {
        ModHelper.Msg<Main>($"[vs] {line}");
        Write("     " + line);
    }

    public static void Warn(string line)
    {
        ModHelper.Warning<Main>($"[vs] {line}");
        Write("WARN " + line);
    }

    /// Context first, then the whole exception. A bare message has repeatedly not been
    /// enough to tell which of several candidates actually broke.
    public static void Failed(string context, Exception e)
    {
        ModHelper.Error<Main>($"[vs] {context} failed: {e}");
        Write($"FAIL {context}: {e}");
    }

    /// Runs a piece of work so that a failure is reported with its name rather than
    /// vanishing into the bridge, and never escapes into the game's own code.
    public static void Guard(string context, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            Failed(context, e);
        }
    }

    /// A patch that silently fails to attach looks exactly like a feature that does not
    /// work, so every target is checked by name at startup and reported either way.
    public static void CheckPatch(Type type, string method)
    {
        try
        {
            var found = AccessTools.Method(type, method);
            if (found is null) Warn($"patch target missing: {type.Name}.{method}");
            else Say($"patch target found: {type.Name}.{method}");
        }
        catch (Exception e)
        {
            Failed($"looking up {type.Name}.{method}", e);
        }
    }

    private static void Write(string line)
    {
        if (!started) return;

        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch
        {
            // A failed write must never take the game down with it.
        }
    }
}
