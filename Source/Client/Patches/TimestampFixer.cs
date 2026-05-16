using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Patches;

public static class TimestampFixer
{
    private const int MaxFutureCanSleepTick = 1000;
    delegate ref int FieldGetter<in T>(T obj) where T : IExposable;

    private static Dictionary<Type, List<FieldGetter<IExposable>>> timestampFields = new();
    private static AccessTools.FieldRef<Ability, int> abilityCooldownEndTick = AccessTools.FieldRefAccess<Ability, int>("cooldownEndTick");
    private static AccessTools.FieldRef<Ability, int> abilityCooldownDuration = AccessTools.FieldRefAccess<Ability, int>("cooldownDuration");
    private static HashSet<IExposable> processedExposables;

    private static void Add<T>(FieldGetter<T> t) where T : IExposable
    {
        timestampFields.GetOrAddNew(typeof(T)).Add(obj => ref t((T)obj));
    }

    static TimestampFixer()
    {
        Add((Ability ability) => ref abilityCooldownEndTick(ability));
        Add((Ability ability) => ref ability.lastCastTick);
        Add((Pawn_MindState mind) => ref mind.canSleepTick);
        Add((Pawn_MindState mind) => ref mind.canLovinTick);
        Add((Pawn_GuestTracker guest) => ref guest.ticksWhenAllowedToEscapeAgain);
        Add((Pawn_GuestTracker guest) => ref guest.lastPrisonBreakTicks);
    }

    public static int? currentOffset;

    public static void FixPawn(Pawn p, Map oldMap, Map newMap)
    {
        var oldTime = oldMap?.AsyncTime().mapTicks ?? Multiplayer.AsyncWorldTime.worldTicks;
        var newTime = newMap?.AsyncTime().mapTicks ?? Multiplayer.AsyncWorldTime.worldTicks;
        currentOffset = newTime - oldTime;
        processedExposables = new HashSet<IExposable>();

        MpLog.Debug($"Fixing pawn timestamps for {p} moving from {oldMap?.ToString() ?? "World"}:{oldTime} to {newMap?.ToString() ?? "World"}:{newTime}");

        try
        {
            // Auxiliary save which is used to visit the pawn's data
            Scribe.saver.DebugOutputFor(p);
            ProcessPawnAbilities(p);
        }
        finally
        {
            currentOffset = null;
            processedExposables = null;
        }
    }

    public static void ProcessExposable(IExposable exposable)
    {
        if (currentOffset == null) return;
        if (exposable == null) return;
        if (processedExposables != null && !processedExposables.Add(exposable)) return;

        ProcessRegisteredTimestampFields(exposable);
    }

    public static void ClampCanSleepTick(Pawn pawn)
    {
        if (pawn?.mindState == null || !pawn.Spawned || pawn.Map == null) return;

        var currentTick = CurrentTickFor(pawn);
        var maxCanSleepTick = currentTick + MaxFutureCanSleepTick;

        if (pawn.mindState.canSleepTick > maxCanSleepTick)
        {
            MpLog.Debug($"Clamping canSleepTick for {pawn} from {pawn.mindState.canSleepTick} to {maxCanSleepTick}");
            pawn.mindState.canSleepTick = maxCanSleepTick;
        }
    }

    public static void ClampAbilityCooldown(Ability ability)
    {
        if (ability?.pawn == null) return;

        var cooldownDuration = abilityCooldownDuration(ability);
        if (cooldownDuration <= 0) return;

        var currentTick = CurrentTickFor(ability.pawn);
        var cooldownEndTick = abilityCooldownEndTick(ability);

        if (cooldownEndTick - currentTick > cooldownDuration)
        {
            var clampedCooldownEndTick = currentTick + cooldownDuration;
            MpLog.Debug($"Clamping ability cooldown for {ability.def} on {ability.pawn} from {cooldownEndTick} to {clampedCooldownEndTick}");
            abilityCooldownEndTick(ability) = clampedCooldownEndTick;
        }
    }

    public static void ClampAbilityCooldowns(Pawn pawn)
    {
        if (pawn?.abilities == null) return;

        foreach (var ability in pawn.abilities.AllAbilitiesForReading)
            ClampAbilityCooldown(ability);
    }

    private static void ProcessPawnAbilities(Pawn pawn)
    {
        if (pawn?.abilities == null) return;

        foreach (var ability in pawn.abilities.AllAbilitiesForReading)
        {
            ProcessExposable(ability);
        }
    }

    private static void ProcessRegisteredTimestampFields(IExposable exposable)
    {
        for (var type = exposable.GetType(); type != null; type = type.BaseType)
            if (timestampFields.TryGetValue(type, out var fieldGetters))
                foreach (var del in fieldGetters)
                {
                    ref var value = ref del(exposable);
                    if (value > 0)
                        value += currentOffset!.Value;
                }
    }

    private static int CurrentTickFor(Pawn pawn)
    {
        return pawn.MapHeld?.AsyncTime().mapTicks ?? Multiplayer.AsyncWorldTime.worldTicks;
    }
}

[HarmonyPatch(typeof(DebugLoadIDsSavingErrorsChecker), nameof(DebugLoadIDsSavingErrorsChecker.RegisterDeepSaved))]
static class RegisterDeepSaved_ProcessExposable
{
    static void Prefix(object obj)
    {
        if (TimestampFixer.currentOffset != null && obj is IExposable exposable)
            TimestampFixer.ProcessExposable(exposable);
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
static class PawnDespawn_RememberMap
{
    static void Prefix(Pawn __instance)
    {
        if (Multiplayer.Client == null) return;
        __instance.GetComp<MultiplayerPawnComp>().lastMap = __instance.Map.uniqueID;
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.RemovePawn))]
static class WorldPawnsRemovePawn_RememberTick
{
    static void Prefix(Pawn p)
    {
        if (Multiplayer.Client == null) return;
        p.GetComp<MultiplayerPawnComp>().worldPawnRemoveTick = Multiplayer.AsyncWorldTime.worldTicks;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
static class PawnSpawn_FixTimestamp
{
    static void Postfix(Pawn __instance)
    {
        if (Multiplayer.Client == null) return;
        if (__instance.Map == null) return;

        var comp = __instance.GetComp<MultiplayerPawnComp>();

        if (comp.worldPawnRemoveTick == Multiplayer.AsyncWorldTime.worldTicks)
        {
            TimestampFixer.FixPawn(__instance, null, __instance.Map);
            comp.worldPawnRemoveTick = -1;
            comp.lastMap = -1;
            TimestampFixer.ClampCanSleepTick(__instance);
            TimestampFixer.ClampAbilityCooldowns(__instance);
            return;
        }

        if (comp.lastMap != -1 && comp.lastMap != __instance.Map.uniqueID)
        {
            var oldMap = Find.Maps.FirstOrDefault(m => m.uniqueID == comp.lastMap);
            if (oldMap != null)
                TimestampFixer.FixPawn(__instance, oldMap, __instance.Map);

            comp.lastMap = -1;
        }

        TimestampFixer.ClampCanSleepTick(__instance);
        TimestampFixer.ClampAbilityCooldowns(__instance);
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.AddPawn))]
static class WorldPawnsAddPawn_FixTimestamp
{
    static void Prefix(Pawn p)
    {
        if (Multiplayer.Client == null) return;

        var comp = p.GetComp<MultiplayerPawnComp>();
        var lastMap = comp.lastMap;
        if (lastMap != -1)
        {
            TimestampFixer.FixPawn(p, Find.Maps.FirstOrDefault(m => m.uniqueID == lastMap), null);
            comp.lastMap = -1;
        }
    }
}

[HarmonyPatch(typeof(JobGiver_GetRest), nameof(JobGiver_GetRest.GetPriority))]
static class JobGiverGetRest_ClampCanSleepTick
{
    static void Prefix(Pawn pawn)
    {
        if (Multiplayer.Client == null) return;
        TimestampFixer.ClampCanSleepTick(pawn);
    }
}
