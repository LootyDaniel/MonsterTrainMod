using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using TrainworksReloaded.Base;
using TrainworksReloaded.Base.Extensions;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using static SaveManager;

namespace HexTrolls.Plugin.code
{
    public static class GoldTriggerHandler
    {
        public static CharacterTriggerData.Trigger OnGoldChangedTrigger { get; private set; }
        public static bool IsRegistered { get; private set; }

        private static readonly List<CharacterState> _allCharacters = new List<CharacterState>();

        // Call GoldTriggerHandler.Initialize() inside your Plugin.Awake()
        public static void Initialize()
        {
            Railend.ConfigurePostAction(c =>
            {
                var manager = c.GetInstance<IRegister<CharacterTriggerData.Trigger>>();
                string profitId = MyPluginInfo.PLUGIN_GUID.GetId(TemplateConstants.CharacterTriggerEnum, "OnProfit");

                if (manager.TryGetValue(profitId, out var trigger))
                {
                    OnGoldChangedTrigger = trigger;
                    IsRegistered = true;
                }
            });
        }

        public static void OnGoldChanged(GoldChangedSignalData signalData)
        {
            var saveManager = AllGameManagers.Instance?.GetSaveManager();
            var combatManager = AllGameManagers.Instance?.GetCombatManager();
            var roomManager = AllGameManagers.Instance?.GetRoomManager();

            // 1. Skip preview simulations and out-of-combat gold changes
            if (saveManager == null || saveManager.PreviewMode || !IsRegistered)
                return;

            if (combatManager == null || roomManager == null || !combatManager.isActiveAndEnabled)
                return;

            // 2. Collect all friendly units across every floor and queue the trigger
            _allCharacters.Clear();

            for (int i = 0; i < 3; i++)
            {
                roomManager.GetRoom(i).AddCharactersToList(_allCharacters, Team.Type.Monsters, false, true);
            }
            foreach (var unit in _allCharacters)
            {
                if (unit != null && unit.IsAlive && !unit.PreviewMode)
                {
                    combatManager.QueueTrigger(unit, OnGoldChangedTrigger);
                }
            }
        }
    }

    [HarmonyPatch(typeof(SaveManager), "Initialize")]
    public static class Patch_SaveManager_Initialize
    {
        static void Postfix(SaveManager __instance)
        {
            __instance.goldChangedSignal.RemoveListener(GoldTriggerHandler.OnGoldChanged);
            __instance.goldChangedSignal.AddListener(GoldTriggerHandler.OnGoldChanged);
        }
    }
}
