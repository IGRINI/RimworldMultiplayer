using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public static partial class SyncFields
    {
        private const string RimHudInspectPaneButtonsTypeName = "RimHUD.Interface.Screen.InspectPaneButtons";

        private static void RegisterRimHudMedicalPatches()
        {
            var inspectPaneButtonsType = MpReflection.GetTypeByName(RimHudInspectPaneButtonsTypeName);
            if (inspectPaneButtonsType == null) return;

            PatchRimHudMedicalButton(inspectPaneButtonsType, "DrawMedical", nameof(RimHudMedicalCareButtonPrefix));
            PatchRimHudMedicalButton(inspectPaneButtonsType, "DrawSelfTend", nameof(RimHudSelfTendButtonPrefix));
        }

        private static void PatchRimHudMedicalButton(Type targetType, string targetMethodName, string prefixMethodName)
        {
            var targetMethod = AccessTools.Method(targetType, targetMethodName, new[] { typeof(Pawn), typeof(Rect) });
            if (targetMethod == null)
            {
                Log.Warning($"MP: RimHUD compatibility patch skipped missing method {targetType.FullName}.{targetMethodName}(Pawn, Rect).");
                return;
            }

            var prefixMethod = AccessTools.Method(typeof(SyncFields), prefixMethodName);
            var finalizerMethod = AccessTools.Method(typeof(SyncFields), nameof(RimHudMedicalButtonFinalizer));

            var prefix = new HarmonyMethod(prefixMethod) { priority = MpPriority.MpFirst };
            var finalizer = new HarmonyMethod(finalizerMethod) { priority = MpPriority.MpLast };

            Multiplayer.harmony.PatchMeasure(targetMethod, prefix: prefix, finalizer: finalizer);
        }

        private static void RimHudMedicalCareButtonPrefix(Pawn pawn, out bool __state)
        {
            __state = false;
            if (pawn?.playerSettings == null) return;

            SyncFieldUtil.FieldWatchPrefix();
            SyncMedCare.Watch(pawn);
            __state = true;
        }

        private static void RimHudSelfTendButtonPrefix(Pawn pawn, out bool __state)
        {
            __state = false;
            if (pawn?.playerSettings == null) return;

            SyncFieldUtil.FieldWatchPrefix();
            SyncSelfTend.Watch(pawn);
            __state = true;
        }

        private static void RimHudMedicalButtonFinalizer(bool __state)
        {
            if (__state)
                SyncFieldUtil.FieldWatchPostfix();
        }
    }
}
