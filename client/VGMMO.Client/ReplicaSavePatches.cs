using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Source.Util;

namespace VGMMO.Client;

/// <summary>Replicas are ephemeral mirrors of the server stream — they must never
/// be captured by a save. Two layers:
/// 1. Prefix on SaveCurrentState: every save path (autosave, manual, DoSave)
///    funnels through it; despawn all replicas + pose state before the snapshot
///    is built (relay respawns them within a frame).
/// 2. Postfix on ReportWriteResult: self-healing scrub of the actual save file
///    — if a replica unit EVER lands in a written save (bypass, future change,
///    older build), it is stripped right after the write, so no save file can
///    carry one even retroactively. Also repairs any stray file on the next
///    save of that slot.</summary>
[HarmonyPatch(typeof(SaveGame), nameof(SaveGame.SaveCurrentState))]
public static class ReplicaSavePatches
{
    private static void Prefix()
    {
        MmoPlugin.Instance?.Session?.DespawnAllForSave();
    }
}

[HarmonyPatch(typeof(SaveGame), "ReportWriteResult")]
public static class ReplicaSaveGuard
{
    private static readonly string[] MarkerNames = { "Shadow", "Vega Sable", "Orion Vale" };

    private static void Postfix(SaveSnapshot snapshot, Exception error)
    {
        if (error != null) return;
        try
        {
            if (!File.Exists(snapshot.filePath)) return;
            var raw = File.ReadAllBytes(snapshot.filePath);
            var txt = Decompress(raw);
            if (txt == null) return;
            if (!txt.Contains("Kinder (shadow)") && !txt.Contains("\"customName\":\"Shadow\"")
                && !txt.Contains("\"customName\":\"Vega Sable\"") && !txt.Contains("\"customName\":\"Orion Vale\""))
                return;
            var root = JToken.Parse(txt);
            var removed = StripReplicaUnits(root);
            if (removed == 0) return;
            File.WriteAllBytes(snapshot.filePath, Compress(root.ToString(Newtonsoft.Json.Formatting.None)));
            MmoPlugin.Instance?.LogSource.LogWarning($"[save-guard] scrubbed {removed} replicated unit(s) from {snapshot.saveName}");
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[save-guard] scrub failed: " + e.Message);
        }
    }

    private static int StripReplicaUnits(JToken node)
    {
        int removed = 0;
        if (node is JArray arr)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is JObject o && IsReplica(o)) { arr.RemoveAt(i); removed++; }
            }
            foreach (var child in arr.Children().ToList()) removed += StripReplicaUnits(child);
        }
        else if (node is JObject obj)
        {
            foreach (var child in obj.Properties().Select(p => p.Value).ToList()) removed += StripReplicaUnits(child);
        }
        return removed;
    }

    private static bool IsReplica(JObject o)
    {
        var custom = (string?)o["customName"];
        if (custom != null && Array.IndexOf(MarkerNames, custom) >= 0) return true;
        var cmdr = o["commander"] as JObject;
        if (cmdr != null && (cmdr["lastName"]?.Value<string>() ?? "").Contains("(shadow)")) return true;
        return false;
    }

    private static byte[] Compress(string json)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new StreamWriter(gz)) w.Write(json);
        return ms.ToArray();
    }

    private static string? Decompress(byte[] raw)
    {
        try
        {
            using var ms = new MemoryStream(raw);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var r = new StreamReader(gz);
            return r.ReadToEnd();
        }
        catch { return null; }
    }
}
