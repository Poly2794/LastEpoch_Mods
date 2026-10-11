using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2Cpp;
using Il2CppLE.Core;
using Il2CppLE.Factions;

namespace LastEpoch_Hud.Scripts.Mods.Diagnostics;

/// <summary>
/// Temporary diagnostic: logs random encounter spawn rates, the Weaver tree's spawn modifiers,
/// and every spawner's decisions per zone. Not for a PR.
/// </summary>
internal static class EchoSpawnRatesDump
{
    const string Tag = "[EchoRates] ";

    // Weaver values worth logging per zone: anything that changes what spawns or how often.
    static readonly Regex WeaverSpawnField = new Regex(
        "Nemesis|RunePrison|Cache|Favor|Omen|Morditas|Tomb|Cemetery|Possess|TimeBeast|Champion|Lizard|Spawn|EchoChain|Masque|Zerrick|Shade|EarnedWeaverPoints",
        RegexOptions.Compiled
    );

    static bool dataLogged = false;

    // Session tally, so a log can be summarised as "N echoes, mechanic X spawned in M of them".
    static int echoNumber = 0;

    // True from the start of an echo's encounter placement until the next zone's placement.
    // The spawners run inside TryPlaceRandomEncounters, so the echo starts in its Prefix.
    static bool inEcho = false;
    static readonly HashSet<string> spawnedThisEcho = new HashSet<string>();
    static readonly Dictionary<string, int> echoesWithSpawn = new Dictionary<string, int>();

    /// <summary>Counts each mechanic once per echo when its ExecuteSpawn runs.</summary>
    static void CountSpawn(string label)
    {
        const string suffix = " ExecuteSpawn";
        if (!inEcho || !label.EndsWith(suffix) || !spawnedThisEcho.Add(label))
        {
            return;
        }
        string mechanic = label.Substring(0, label.Length - suffix.Length);
        echoesWithSpawn.TryGetValue(mechanic, out int count);
        echoesWithSpawn[mechanic] = count + 1;
        // ExecuteSpawn running isn't proof something appeared (it may roll 0); compare with what you saw.
        var sb = new StringBuilder(
            Tag + "TALLY (echoes where ExecuteSpawn ran) as of ECHO #" + echoNumber + ":"
        );
        foreach (var entry in echoesWithSpawn)
        {
            sb.Append(' ')
                .Append(entry.Key)
                .Append(' ')
                .Append(entry.Value)
                .Append('/')
                .Append(echoNumber)
                .Append(';');
        }
        Main.logger_instance?.Msg(sb.ToString());
    }

    /// <summary>Logs the global spawn table and the Weaver per-point constants once per session.</summary>
    internal static void LogData()
    {
        if (dataLogged)
        {
            return;
        }
        RandomEncounterData d = RandomEncounterData.get();
        if (d == null)
        {
            Main.logger_instance?.Warning(Tag + "RandomEncounterData.get() returned null");
            return;
        }
        dataLogged = true;

        var sb = new StringBuilder(Tag + "RandomEncounterData (all numeric and bool fields)");
        AppendValues(sb, d.GetType(), d, BindingFlags.Instance, null);

        sb.Append("\n  Omen window tiers");
        var tiers = d.omenWindowTiers;
        if (tiers == null)
        {
            sb.Append("\n    (none)");
        }
        else
        {
            for (int i = 0; i < tiers.Length; i++)
            {
                RandomEncounterData.OmenWindowTierSettings t = tiers[i];
                sb.Append("\n    ")
                    .Append(t.windowType)
                    .Append(" T")
                    .Append(t.tier)
                    .Append(": monolith ")
                    .Append(t.monolithAllowed)
                    .Append(", corruption ")
                    .Append(t.corruptionMin)
                    .Append('-')
                    .Append(t.corruptionMax)
                    .Append(", weight ")
                    .Append(Num(t.monolithWeightAtMin))
                    .Append(" -> ")
                    .Append(Num(t.monolithWeightAtMax))
                    .Append(t.stopsAppearingAfterMaxCorruption ? ", stops after max" : "");
            }
        }

        sb.Append("\n  Favour cache chance multiplier for repeat echo attempts: ")
            .Append(Num(RandomFavourCacheSpawner.favourCacheChanceMultiplierForRepeatEchoAttempts));
        Main.logger_instance?.Msg(sb.ToString());

        var weaver = new StringBuilder(Tag + "Weaver tree constants (static)");
        AppendValues(weaver, typeof(TheWeaver), null, BindingFlags.Static, WeaverSpawnField);
        Main.logger_instance?.Msg(weaver.ToString());
    }

    /// <summary>Logs the zone's level, corruption, placement config and Weaver modifiers.</summary>
    static void LogZone(RandomEncounterTypes.EncounterPlacementData placementData, bool placed)
    {
        LogData();
        if (placementData == null)
        {
            return;
        }
        var sb = new StringBuilder(Tag + "Zone ").Append(Scenes.SceneName);
        RandomEncounterTypes.Context context = placementData.Context;
        if (context != null)
        {
            sb.Append(", level ")
                .Append(context.ZoneLevel)
                .Append(", corruption ")
                .Append(context.CurrentCorruption);
        }
        RandomEncounterTypes.MonolithData monolith = placementData.MonolithData;
        if (monolith != null && monolith.HasMonolithData)
        {
            sb.Insert(Tag.Length, "END ECHO #" + echoNumber + " | ");
            MonolithRun run = monolith.ActiveRun;
            if (run != null)
            {
                sb.Append("\n  timeline ")
                    .Append(run.timelineID)
                    .Append(", difficulty ")
                    .Append(run.difficultyIndex)
                    .Append(", depth ")
                    .Append(run.depth)
                    .Append(", stability ")
                    .Append(run.stability)
                    .Append(", active mods ")
                    .Append(run.mods?.Count ?? 0);
            }
            EchoWebIsland island = monolith.ActiveIsland;
            if (island != null)
            {
                // Chain echoes (Nemesis, OmenWindow, ...) can force a spawn regardless of the random rates.
                sb.Append("\n  island ")
                    .Append(island.islandType)
                    .Append(" at hex ")
                    .Append(island.hexIndex)
                    .Append(" zone ")
                    .Append(island.zoneIndex)
                    .Append(", chain ")
                    .Append(island.IsEchoChain ? island.chainType.ToString() : "none")
                    .Append(island.isEndOfChain ? " (end of chain)" : "")
                    .Append(", woven ")
                    .Append(island.IsWovenEcho ? island.wovenEchoType.ToString() : "no")
                    .Append(", stability reward ")
                    .Append(island.stabilityReward)
                    .Append(", reward index ")
                    .Append(
                        island.hasDroppableReward ? island.droppableRewardIndex.ToString() : "none"
                    )
                    .Append(", entered before ")
                    .Append(island.hasEnteredEcho);
            }
            sb.Append("\n  echo: ")
                .Append(monolith.HasMonolithData)
                .Append(", main echo scene ")
                .Append(monolith.IsMainEchoScene)
                .Append(", repeat attempt ")
                .Append(monolith.IsRepeatEchoAttempt)
                .Append(", objective ")
                .Append(monolith.ObjectiveType)
                .Append(", guaranteed nemesis ")
                .Append(monolith.HasGuaranteedPossessedNemesisInTombs)
                .Append(", guaranteed rune prison ")
                .Append(monolith.HasGuaranteedPossessedRunePrisonInTombs);
        }
        RandomEncounterTypes.PlacementConfigData config = placementData.SceneData?.Config;
        if (config != null)
        {
            sb.Append("\n  config");
            AppendValues(sb, config.GetType(), config, BindingFlags.Instance, null);
        }
        TheWeaver weaver = placementData.WeaverModifiers;
        if (weaver == null)
        {
            sb.Append("\n  weaver: none");
        }
        else
        {
            sb.Append("\n  weaver (from allocated points)");
            AppendValues(sb, typeof(TheWeaver), weaver, BindingFlags.Instance, WeaverSpawnField);
        }
        sb.Append("\n  TryPlaceRandomEncounters returned ").Append(placed);
        Main.logger_instance?.Msg(sb.ToString());
    }

    /// <summary>Appends every declared float/int/bool property, optionally filtered by name.</summary>
    static void AppendValues(
        StringBuilder sb,
        Type type,
        object instance,
        BindingFlags scope,
        Regex nameFilter
    )
    {
        var flags = BindingFlags.Public | BindingFlags.DeclaredOnly | scope;
        foreach (PropertyInfo p in type.GetProperties(flags))
        {
            Type t = p.PropertyType;
            if (t != typeof(float) && t != typeof(int) && t != typeof(bool))
            {
                continue;
            }
            if (p.GetIndexParameters().Length > 0 || !p.CanRead)
            {
                continue;
            }
            if (nameFilter != null && !nameFilter.IsMatch(p.Name))
            {
                continue;
            }
            string value;
            try
            {
                object v = p.GetValue(instance);
                value = v is float f ? Num(f) : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                value = "(error: " + ex.GetType().Name + ")";
            }
            sb.Append("\n    ").Append(p.Name).Append(": ").Append(value);
        }
    }

    // The spawner currently deciding (a stack, in case one spawner calls into another).
    // RNG calls made while it's non-empty are logged, which shows the final chance each spawner rolls.
    static readonly Stack<string> deciding = new Stack<string>();

    static void Enter(string mechanic, string step) => deciding.Push(mechanic + " " + step);

    static void Exit(bool? result = null)
    {
        if (deciding.Count == 0)
        {
            return;
        }
        string label = deciding.Pop();
        CountSpawn(label);
        Main.logger_instance?.Msg(Tag + label + (result.HasValue ? " = " + result.Value : " ran"));
    }

    static void LogRng(string call)
    {
        if (deciding.Count > 0)
        {
            Main.logger_instance?.Msg(Tag + "  " + deciding.Peek() + " rng: " + call);
        }
    }

    static string Num(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>The echo's in-game name, its scene and its timeline, as far as each is available.</summary>
    static string EchoName(RandomEncounterTypes.EncounterPlacementData placementData)
    {
        RandomEncounterTypes.SceneData sceneData = placementData.SceneData;
        string sceneDisplay = null;
        string sceneName = Scenes.SceneName;
        string echoDisplay = null;
        string timeline = null;
        try
        {
            if (sceneData != null)
            {
                sceneDisplay = sceneData.SceneDetails?.LocalizedName;
                sceneName = sceneData.Scene.name;
            }
            MonolithRun run = placementData.MonolithData?.ActiveRun;
            EchoWebIsland island = placementData.MonolithData?.ActiveIsland;
            if (run != null)
            {
                timeline = run.timeline?.displayName;
                if (island != null)
                {
                    echoDisplay = island.GetEchoDisplayName(run.timelineID, run.difficultyIndex);
                }
            }
        }
        catch (Exception ex)
        {
            echoDisplay = "(error " + ex.GetType().Name + ")";
        }
        return "'"
            + (string.IsNullOrWhiteSpace(echoDisplay) ? sceneDisplay : echoDisplay)
            + "', scene "
            + sceneName
            + " ('"
            + sceneDisplay
            + "'), timeline '"
            + timeline
            + "'";
    }

    [HarmonyPatch(typeof(RandomEncounterPlacer), "TryPlaceRandomEncounters")]
    public class RandomEncounterPlacer_TryPlaceRandomEncounters
    {
        [HarmonyPrefix]
        static void Prefix(RandomEncounterTypes.EncounterPlacementData __1)
        {
            // A spawner that threw last zone would leave its label behind; start each zone clean.
            deciding.Clear();
            inEcho = __1?.MonolithData != null && __1.MonolithData.HasMonolithData;
            if (!inEcho)
            {
                return;
            }
            echoNumber++;
            spawnedThisEcho.Clear();
            Main.logger_instance?.Msg(Tag + "BEGIN ECHO #" + echoNumber + " " + EchoName(__1));
        }

        [HarmonyPostfix]
        static void Postfix(RandomEncounterTypes.EncounterPlacementData __1, bool __result) =>
            LogZone(__1, __result);
    }

    [HarmonyPatch(typeof(TheWeaver), "UpdateWeaverTreeNode")]
    public class TheWeaver_UpdateWeaverTreeNode
    {
        [HarmonyPostfix]
        static void Postfix(WeaverTreeNodeEffect __0, int __1) =>
            Main.logger_instance?.Msg(
                Tag + "Weaver node " + __0 + " (" + (int)__0 + ") points " + __1
            );
    }

    [HarmonyPatch(typeof(RngElement), "Roll", new System.Type[] { typeof(float) })]
    public class RngElement_Roll
    {
        [HarmonyPostfix]
        static void Postfix(float __0, bool __result) =>
            LogRng("Roll(chance " + Num(__0) + ") = " + __result);
    }

    [HarmonyPatch(typeof(RngElement), "StochasticRound", new System.Type[] { typeof(float) })]
    public class RngElement_StochasticRound
    {
        [HarmonyPostfix]
        static void Postfix(float __0, int __result) =>
            LogRng("StochasticRound(" + Num(__0) + ") = " + __result);
    }

    [HarmonyPatch(typeof(RngElement), "NextFloat", new System.Type[] { })]
    public class RngElement_NextFloat
    {
        [HarmonyPostfix]
        static void Postfix(float __result) => LogRng("NextFloat() = " + Num(__result));
    }

    [HarmonyPatch(
        typeof(RngElement),
        "NextFloat",
        new System.Type[] { typeof(float), typeof(float) }
    )]
    public class RngElement_NextFloatRange
    {
        [HarmonyPostfix]
        static void Postfix(float __0, float __1, float __result) =>
            LogRng("NextFloat(" + Num(__0) + ", " + Num(__1) + ") = " + Num(__result));
    }

    // Where champions come from. The random champion spawner rolled 0% in every echo, yet
    // champions appeared, so these log the other paths. None of them takes a ref argument.

    // Combat events (enemies alerted): can spawn champions, loot lizards and rune wardens.
    [HarmonyPatch(typeof(RandomEncounterPlacer), "OnCallToArms")]
    public class RandomEncounterPlacer_OnCallToArms
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Call to Arms", "event");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    // The actor a call-to-arms (or similar) event spawned next to the player.
    [HarmonyPatch(typeof(RandomEncounterPlacer), "SpawnEncounterActorNearLocation")]
    public class RandomEncounterPlacer_SpawnEncounterActorNearLocation
    {
        [HarmonyPostfix]
        static void Postfix(ActorData __0) =>
            Main.logger_instance?.Msg(
                Tag
                    + "Spawned near location: "
                    + (__0.IsNullOrDestroyed() ? "?" : __0.actorName)
                    + " in "
                    + Scenes.SceneName
            );
    }

    // Champion spawner placed at zone load.
    [HarmonyPatch(typeof(ChampionEncounter), "PlaceRandomChampionSpawner")]
    public class ChampionEncounter_PlaceRandomChampionSpawner
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Champion placement", "zone load");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    // Champion spawn points built into maps and objectives.
    [HarmonyPatch(typeof(SpawnRandomChampionInteraction), "SpawnChampionHere")]
    public class SpawnRandomChampionInteraction_SpawnChampionHere
    {
        [HarmonyPostfix]
        static void Postfix(SpawnRandomChampionInteraction __instance, ActorData __0) =>
            Main.logger_instance?.Msg(
                Tag
                    + "Map champion spawn point ("
                    + __instance.championType
                    + "): "
                    + (__0.IsNullOrDestroyed() ? "?" : __0.actorName)
                    + " in "
                    + Scenes.SceneName
            );
    }

    // One CanSpawn and one ExecuteSpawn patch per spawner, so the patch-target tests check each.

    [HarmonyPatch(typeof(RandomRegularRunePrisonSpawner), "CanSpawn")]
    public class RegularRunePrison_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Rune Prison", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomRegularRunePrisonSpawner), "ExecuteSpawn")]
    public class RegularRunePrison_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Rune Prison", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomGuaranteedRunePrisonSpawner), "CanSpawn")]
    public class GuaranteedRunePrison_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Guaranteed Rune Prison", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomGuaranteedRunePrisonSpawner), "ExecuteSpawn")]
    public class GuaranteedRunePrison_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Guaranteed Rune Prison", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomNemesisSpawner), "CanSpawn")]
    public class Nemesis_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Nemesis", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomNemesisSpawner), "ExecuteSpawn")]
    public class Nemesis_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Nemesis", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomOneShotCacheSpawner), "CanSpawn")]
    public class OneShotCache_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Lost Cache", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomOneShotCacheSpawner), "ExecuteSpawn")]
    public class OneShotCache_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Lost Cache", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomFavourCacheSpawner), "CanSpawn")]
    public class FavourCache_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Favour Cache", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomFavourCacheSpawner), "ExecuteSpawn")]
    public class FavourCache_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Favour Cache", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomObserverWindowSpawner), "CanSpawn")]
    public class ObserverWindow_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Omen window", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomObserverWindowSpawner), "ExecuteSpawn")]
    public class ObserverWindow_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Omen window", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomRoamingOmenSpawner), "CanSpawn")]
    public class RoamingOmen_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Roaming Omen", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomRoamingOmenSpawner), "ExecuteSpawn")]
    public class RoamingOmen_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Roaming Omen", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomRageOfMorditasSpawner), "CanSpawn")]
    public class RageOfMorditas_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Rage of Morditas (Blood Ritual)", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomRageOfMorditasSpawner), "ExecuteSpawn")]
    public class RageOfMorditas_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Rage of Morditas (Blood Ritual)", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomTimeBeastSpawner), "CanSpawn")]
    public class TimeBeast_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Time Beast (Rift Beast)", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomTimeBeastSpawner), "ExecuteSpawn")]
    public class TimeBeast_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Time Beast (Rift Beast)", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomChampionSpawner), "CanSpawn")]
    public class Champion_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Champion", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomChampionSpawner), "ExecuteSpawn")]
    public class Champion_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Champion", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }

    [HarmonyPatch(typeof(RandomShrineSpawner), "CanSpawn")]
    public class Shrine_CanSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Shrine", "CanSpawn");

        [HarmonyPostfix]
        static void Postfix(bool __result) => Exit(__result);
    }

    [HarmonyPatch(typeof(RandomShrineSpawner), "ExecuteSpawn")]
    public class Shrine_ExecuteSpawn
    {
        [HarmonyPrefix]
        static void Prefix() => Enter("Shrine", "ExecuteSpawn");

        [HarmonyPostfix]
        static void Postfix() => Exit();
    }
}
