using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

// TODO: TEST: Test that this works with the new world generation

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.RegenerateEverythingNow))]
    public static class MapDrawerRegenPatch
    {
        public static Dictionary<int, MapDrawer> copyFrom = new();

        // These are readonly so they need to be set using reflection
        private static FieldInfo mapDrawerMap = AccessTools.Field(typeof(MapDrawer), nameof(MapDrawer.map));
        private static FieldInfo sectionMap = AccessTools.Field(typeof(Section), nameof(Section.map));

        static bool Prefix(MapDrawer __instance)
        {
            Map map = __instance.map;
            if (!copyFrom.TryGetValue(map.uniqueID, out MapDrawer keepDrawer)) return true;

            map.mapDrawer = keepDrawer;
            mapDrawerMap.SetValue(keepDrawer, map);

            foreach (Section section in keepDrawer.sections)
            {
                sectionMap.SetValue(section, map);

                for (int i = 0; i < section.layers.Count; i++)
                {
                    SectionLayer layer = section.layers[i];

                    if (!ShouldKeep(layer))
                        section.layers[i] = (SectionLayer)Activator.CreateInstance(layer.GetType(), section);
                    else if (layer is SectionLayer_TerrainScatter scatter)
                        scatter.scats.Do(s => s.map = map);
                }
            }

            foreach (Section s in keepDrawer.sections)
                foreach (SectionLayer layer in s.layers)
                    if (!ShouldKeep(layer))
                        layer.Regenerate();

            copyFrom.Remove(map.uniqueID);

            return false;
        }

        static bool ShouldKeep(SectionLayer layer)
        {
            return layer.GetType().Assembly == typeof(Game).Assembly;
        }
    }

    [HarmonyPatch(typeof(WorldGrid), MethodType.Constructor)]
    public static class WorldGridCachePatch
    {
        public static AccessTools.FieldRef<WorldGrid, List<WorldDrawLayerBase>> globalLayers = AccessTools.FieldRefAccess<WorldGrid, List<WorldDrawLayerBase>>(nameof(WorldGrid.globalLayers));
        public static WorldGrid copyFrom;

        static void Postfix(WorldGrid __instance, ref int ___cachedTraversalDistance, ref int ___cachedTraversalDistanceForStart, ref int ___cachedTraversalDistanceForEnd)
        {
            if (copyFrom == null) return;

            try
            {
                WorldGrid grid = __instance;
                WorldGrid source = copyFrom;

                if (grid.surface == null || source.surface == null)
                    return;

                grid.surfaceViewAngle = source.SurfaceViewAngle;
                grid.surfaceViewCenter = source.SurfaceViewCenter;
                grid.surface.verts = source.UnsafeVerts;
                grid.surface.tileIDToNeighbors_offsets = source.UnsafeTileIDToNeighbors_offsets;
                grid.surface.tileIDToNeighbors_values = source.UnsafeTileIDToNeighbors_values;
                grid.surface.tileIDToVerts_offsets = source.UnsafeTileIDToVerts_offsets;
                grid.surface.averageTileSize = source.AverageTileSize;
                grid.surface.tiles.Clear();
                globalLayers(grid) = source.globalLayers;

                ___cachedTraversalDistance = -1;
                ___cachedTraversalDistanceForStart = -1;
                ___cachedTraversalDistanceForEnd = -1;
            }
            finally
            {
                copyFrom = null;
            }
        }
    }

    [HarmonyPatch(typeof(WorldGrid), nameof(WorldGrid.ExposeData))]
    public static class WorldGridExposeDataPatch
    {
        public static WorldGrid copyFrom;

        static bool Prefix(WorldGrid __instance)
        {
            if (copyFrom == null) return true;

            try
            {
                WorldGrid grid = __instance;
                WorldGrid source = copyFrom;

                if (grid.surface == null || source.surface == null)
                    return true;

                // Use Clear/AddRange instead of reflection to preserve collection observers
                // and handle readonly field correctly.
                grid.surface.tiles.Clear();
                grid.surface.tiles.AddRange(source.surface.tiles);

                return false;
            }
            finally
            {
                // ExposeData runs multiple times but WorldGrid only needs LoadSaveMode.LoadingVars
                copyFrom = null;
            }
        }
    }

    //TODO: TEST: Test that this works with the new world generation
    [HarmonyPatch(typeof(WorldGrid), (nameof(WorldGrid.InitializeGlobalLayers)))]
    public static class WorldRendererCachePatch
    {

        public static AccessTools.FieldRef<WorldGrid, List<WorldDrawLayerBase>> globalLayers = AccessTools.FieldRefAccess<WorldGrid, List<WorldDrawLayerBase>>(nameof(WorldGrid.globalLayers));
        public static WorldGrid copyFrom;

        static bool Prefix(WorldGrid __instance)
        {
            if (copyFrom == null) return true;

            globalLayers(__instance) = copyFrom.globalLayers;
            copyFrom = null;

            return false;
        }
    }
}
