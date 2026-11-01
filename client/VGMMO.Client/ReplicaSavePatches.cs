using HarmonyLib;
using Source.Util;

namespace VGMMO.Client;

/// <summary>Replicas are ephemeral mirrors of the server stream — they must never
/// be captured by a save. SaveCurrentState collects POI units (a persisted
/// replica showed up as a permanent stray after buggy spawns); despawn every
/// replica before the snapshot is built. The relay respawns them within a
/// frame; the save stays clean forever.</summary>
[HarmonyPatch(typeof(SaveGame), nameof(SaveGame.SaveCurrentState))]
public static class ReplicaSavePatches
{
    private static void Prefix()
    {
        MmoPlugin.Instance?.Session?.DespawnAllForSave();
    }
}
