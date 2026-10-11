using HarmonyLib;
using Il2Cpp;

namespace LastEpoch_Hud.Scripts.Mods.Monoliths;

public class Monoliths_MobsDensity
{
    public static bool CanRun()
    {
        if ((Scenes.IsGameScene()) && (!Save_Manager.instance.IsNullOrDestroyed()))
        {
            if (!Save_Manager.instance.data.IsNullOrDestroyed())
            {
                return Save_Manager.instance.data.Scenes.Monoliths.Enable_MobsDensity;
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

    [HarmonyPatch(typeof(MonolithRun), "calculateIncreasedRarityAndExperienceFromMods")]
    public class MonolithRun_calculateIncreasedRarityAndExperienceFromMods
    {
        [HarmonyPostfix]
        static void Postfix(ref MonolithRun __instance)
        {
            if (__instance.timeline.IsNullOrDestroyed())
            {
                Main.logger_instance?.Msg("[Density] Monolith density: timeline is null");
                return;
            }
            float before = __instance.timeline.enemyDensityModifier;
            bool canRun = CanRun();
            if (canRun)
            {
                __instance.timeline.enemyDensityModifier = Save_Manager
                    .instance
                    .data
                    .Scenes
                    .Monoliths
                    .MobsDensity;
            }
            // Diagnostic: shows whether the postfix fires, and in which scene.
            Main.logger_instance?.Msg(
                "[Density] Monolith density: scene="
                    + Scenes.SceneName
                    + ", canRun="
                    + canRun
                    + ", timeline="
                    + __instance.timelineID
                    + ", before="
                    + before
                    + ", after="
                    + __instance.timeline.enemyDensityModifier
            );
        }
    }
}
