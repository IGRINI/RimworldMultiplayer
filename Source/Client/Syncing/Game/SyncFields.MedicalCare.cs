using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    public static partial class SyncFields
    {
        private static readonly Dictionary<MethodBase, MedicalCareDropdownActionFields> medicalCareDropdownActions = new();

        private static void RegisterMedicalCarePatches()
        {
            try
            {
                RegisterMedicalCareDropdownAction(
                    "vanilla medical care",
                    MpMethodUtil.GetLambda(
                        typeof(MedicalCareUtility),
                        nameof(MedicalCareUtility.MedicalCareSelectButton_GenerateMenu),
                        lambdaOrdinal: 0
                    ),
                    "mc",
                    required: true
                );

                RegisterOptionalMedicalCareDropdownAction(
                    "Artificial Beings Framework medical care",
                    "ArtificialBeings.MedicalCareUtility_Patch+MedicalCareUtility_MedicalCareSelectButton_Patch+<>c__DisplayClass2_1",
                    "<MedicalCareSelectButton_GenerateMenu>b__0",
                    "category"
                );
            }
            catch (Exception e)
            {
                Log.Error($"MP: failed to patch medical care dropdown syncing: {e}");
                Multiplayer.loadingErrors = true;
            }
        }

        private static void RegisterOptionalMedicalCareDropdownAction(string patchName, string typeName, string methodName, string categoryFieldName)
        {
            var actionType = MpReflection.GetTypeByName(typeName);
            if (actionType == null) return;

            var actionMethod = AccessTools.Method(actionType, methodName);
            if (actionMethod == null)
            {
                Log.Warning($"MP: skipped {patchName} patch because {typeName}.{methodName} was not found.");
                return;
            }

            RegisterMedicalCareDropdownAction(patchName, actionMethod, categoryFieldName, required: false);
        }

        private static void RegisterMedicalCareDropdownAction(string patchName, MethodBase actionMethod, string categoryFieldName, bool required)
        {
            var categoryField = AccessTools.Field(actionMethod.DeclaringType, categoryFieldName);
            var localsField = AccessTools.Field(actionMethod.DeclaringType, "CS$<>8__locals1");
            var pawnField = AccessTools.Field(localsField?.FieldType, "p");

            if (categoryField == null || localsField == null || pawnField == null)
            {
                var message = $"MP: failed to patch {patchName}: captured fields were not found on {actionMethod.DeclaringType?.FullName}.";
                if (required) throw new MissingFieldException(message);
                Log.Warning(message);
                return;
            }

            medicalCareDropdownActions[actionMethod] = new MedicalCareDropdownActionFields(categoryField, localsField, pawnField);

            Multiplayer.harmony.PatchMeasure(
                actionMethod,
                prefix: new HarmonyMethod(typeof(SyncFields), nameof(MedicalCareDropdownActionPrefix)) { priority = MpPriority.MpFirst }
            );
        }

        private static bool MedicalCareDropdownActionPrefix(object __instance, MethodBase __originalMethod)
        {
            if (!Multiplayer.ShouldSync) return true;
            if (!TryGetMedicalCareDropdownActionData(__instance, __originalMethod, out var pawn, out var medicalCare)) return true;

            ((SyncField)SyncMedCare).DoSyncCatch(pawn, medicalCare);
            return false;
        }

        private static bool TryGetMedicalCareDropdownActionData(object instance, MethodBase actionMethod, out Pawn pawn, out MedicalCareCategory medicalCare)
        {
            pawn = null;
            medicalCare = default;

            if (instance == null) return false;
            if (!medicalCareDropdownActions.TryGetValue(actionMethod, out var fields)) return false;

            var locals = fields.LocalsField.GetValue(instance);
            pawn = fields.PawnField.GetValue(locals) as Pawn;
            if (pawn?.playerSettings == null) return false;

            medicalCare = (MedicalCareCategory)fields.CategoryField.GetValue(instance);
            return true;
        }

        private readonly struct MedicalCareDropdownActionFields(FieldInfo categoryField, FieldInfo localsField, FieldInfo pawnField)
        {
            public readonly FieldInfo CategoryField = categoryField;
            public readonly FieldInfo LocalsField = localsField;
            public readonly FieldInfo PawnField = pawnField;
        }
    }
}
