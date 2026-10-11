using HarmonyLib;
using Il2Cpp;

namespace LastEpoch_Hud.Scripts.Mods.Mobs;

public class Mobs_Density
{
    public static bool CanRun()
    {
        if ((Scenes.IsGameScene()) && (!Save_Manager.instance.IsNullOrDestroyed()))
        {
            if (!Save_Manager.instance.data.IsNullOrDestroyed())
            {
                return Save_Manager.instance.data.Character.Cheats.Enable_DensityMultiplier;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }
    }

    // Diagnostic: last line logged, so per-frame spawn rolls don't flood the log.
    static string lastDensityLog = null;

    public static void Apply(SpawnerPlacementManager manager)
    {
        if (manager.IsNullOrDestroyed())
        {
            return;
        }
        float before = manager.defaultSpawnerDensity;
        bool alwaysRollBefore = manager.alwaysRollSpawnerDensity;
        bool canRun = CanRun();
        if (canRun)
        {
            manager.defaultSpawnerDensity = Save_Manager
                .instance
                .data
                .Character
                .Cheats
                .DensityMultiplier;
            manager.alwaysRollSpawnerDensity = false;
        }
        // Diagnostic: this overwrites the zone's density, monolith echoes included.
        // Logged even when off, to show the game's own value.
        string line =
            "[Density] Spawner density: scene="
            + Scenes.SceneName
            + ", multiplierOn="
            + canRun
            + ", before="
            + before
            + ", after="
            + manager.defaultSpawnerDensity
            + ", alwaysRoll before="
            + alwaysRollBefore
            + " after="
            + manager.alwaysRollSpawnerDensity;
        if (line != lastDensityLog)
        {
            lastDensityLog = line;
            Main.logger_instance?.Msg(line);
        }
    }

    [HarmonyPatch(typeof(SpawnerPlacementManager), "RollSpawners", new System.Type[] { })]
    public class RollSpawners_NoArgs
    {
        [HarmonyPrefix]
        static void Prefix(SpawnerPlacementManager __instance)
        {
            Apply(__instance);
        }
    }

    [HarmonyPatch(
        typeof(SpawnerPlacementManager),
        "RollSpawners",
        new System.Type[] { typeof(SpawnerPlacementRoom.SpawnerRuntimeConfig) }
    )]
    public class RollSpawners_Config
    {
        [HarmonyPrefix]
        static void Prefix(SpawnerPlacementManager __instance)
        {
            Apply(__instance);
        }
    }

    [HarmonyPatch(typeof(SpawnerPlacementManager), "rollSpawnersNPerFrame")]
    public class RollSpawners_PerFrame
    {
        [HarmonyPrefix]
        static void Prefix(SpawnerPlacementManager __instance)
        {
            Apply(__instance);
        }
    }
}
