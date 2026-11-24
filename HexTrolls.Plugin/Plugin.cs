using BepInEx;
using BepInEx.Logging;
using Conductor.Extensions;
using Conductor.TrackedValues;
using Conductor.UI;
using HarmonyLib;
using HexTrolls.Plugin.code;
using System.Linq;
using TrainworksReloaded.Base;
using TrainworksReloaded.Base.Extensions;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Extensions;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;
using static CharacterState;
using static CharacterTriggerData;
using static SaveManager;

namespace HexTrolls.Plugin
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        //var EphemeralCardsPurged;

        internal static new ManualLogSource Logger = new(MyPluginInfo.PLUGIN_GUID);
        internal static int ApplyPatches(Harmony harmony, IEnumerable<Type> types)
        {
            int failures = 0;
            foreach (var type in types)
            {
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception error)
                {
                    ++failures;
                }
            }
            return failures;
        }
        // Plugin startup logic. This function is automatically called when your plugin initializes
        public void Awake()
        {
            Logger = base.Logger;

            gameObject.AddComponent<HexTrolls.Plugin.code.TrainersRosterProtocolBehaviour>();

            var builder = Railhead.GetBuilder();
            builder.Configure(
                MyPluginInfo.PLUGIN_GUID,
                c =>
                {
                    // Be sure to include any new json files if you add more.
                    c.AddMergedJsonFile(

                        //shit
                        //"json/plugin.json",
                        "json/global.json",

                        //ClanSubTypes
                        "json/clan/ClanSubtypes.json",

                        //ClanBanners
                        "json/nodes/BountyClanBanner.json",
                        "json/nodes/TrainersClanBanner.json",

                        //ClanCardFrames
                        "json/clan/BountyClanCardFrame.json",

                        //mechanics
                        //"json/clan/OrcClanCustomMechanic.json",
                        "json/clan/OrcClanCustomMechanic2.json",
                        "json/clan/TrainersDeckDeploy.json",
                        "json/clan/TrainersRosterConductorHUD.json",

                        //Clans
                        "json/clan/BountyClan.json",
                        "json/clan/TrainersClan.json",

                        //Keywords
                        "json/clan/LunarmancerKeywords.json",


                        //status_effects
                        "json/status_effects/Mark.json",
                        "json/status_effects/Shockwave.json",
                        "json/status_effects/Mercenary.json",
                        "json/status_effects/TrainersLevel.json",
                        "json/status_effects/TrainersEXP.json",
                        "json/status_effects/TrainersEvolve.json",
                        "json/status_effects/TrainersRosterSlot.json",

                        //triggers
                        "json/triggers/MarkedTrigger.json",
                        "json/triggers/ProfitTrigger.json",

                        //spells
                        "json/spells/DuplicateGearSpell.json",
                        "json/spells/HealingPotion.json",
                        "json/spells/QuickGrab.json",
                        "json/spells/CallForViolence.json",
                        "json/spells/SpikeOfProscription.json",
                        "json/spells/RecruitSpell.json",

                        //equipment
                        "json/equipment/HuntersBow.json",
                        "json/equipment/TrappingGear.json",

                        //rooms

                        //units
                        "json/units/JuniorAgent.json",
                        "json/units/SupportAgent.json",
                        "json/units/GuildMage.json",
                        "json/units/GuildBruiser.json",
                        "json/units/BlastMagus.json",
                        "json/units/BluntTrap.json",
                        "json/units/StunTrap.json",
                        "json/units/AttackTrap.json",
                        "json/units/TrainersPocketMon39.json",

                        //upgrades
                        "json/upgrades/Tremorstone.json",
                        "json/upgrades/ColossusSlab.json",

                        //relics
                        "json/relics/OverflowClause.json",
                        "json/relics/LibraryOfScrolls.json",
                        "json/relics/TrainersRosterProtocol.json",
                        "json/relics/TrainersWildPocketMonInvasion.json",

                        //Champions
                        "json/champions/GuildMasterChampion.json",
                        "json/champions/MerchantChampion.json",
                        "json/champions/RedChampion.json",
                        "json/champions/BlueChampion.json"

                    );
                }
            );
            Railend.ConfigurePostAction(
                c =>
                {
                    var manager = c.GetInstance<IRegister<CharacterTriggerData.Trigger>>();
                    string markId = MyPluginInfo.PLUGIN_GUID.GetId(TemplateConstants.CharacterTriggerEnum, "OnMarked");
                    if (manager.TryGetValue(markId, out var markedTrigger))
                    {
                        markedTrigger.SetToTriggerOnStatusEffectAdded(OnStatusEffectAddedToUnit);
                    }
                    else
                    {
                        Plugin.Logger.LogError($"Failed to find CharacterTriggerData.Trigger for ID: {markId}");
                    }

                    var cardRegister = c.GetInstance<IRegister<CardData>>();
                    var spriteRegister = c.GetInstance<IRegister<Sprite>>();
                    MercenaryHudController.InitializeData(cardRegister, spriteRegister);
                }
            );
            //MarkedTriggerHandler.Initialize();
            GoldTriggerHandler.Initialize();

            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

            int failures = ApplyPatches(new Harmony(MyPluginInfo.PLUGIN_GUID), typeof(Plugin).Assembly.GetTypes());
            if (failures == 0) Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
            else Logger.LogError($"FreeCompany initialized with {failures} failed Harmony patch classes. "
                + "Some features are unavailable; inspect the errors above before playing.");

            // Uncomment if you need Harmony Patch support.
            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

        }

        static bool OnStatusEffectAddedToUnit(TriggerOnStatusAddedParams data, out QueueTriggerParams? triggerQueueData)
        {
            triggerQueueData = null;

            var saveManager = AllGameManagers.Instance?.GetSaveManager();
            var combatManager = AllGameManagers.Instance?.GetCombatManager();

            if (saveManager == null || saveManager.PreviewMode || combatManager == null)
                return false;

            if (data.Character == null || data.Character.PreviewMode || data.StatusEffectStack.Count <= 0)
                return false;

            if (data.StatusId != "hextrolls.plugin_mark")
                return false;

            var room = data.Character.GetCurrentRoom();
            if (room == null)
                return false;

            var triggerManager = Railend.GetContainer().GetInstance<IRegister<CharacterTriggerData.Trigger>>();
            string triggerId = MyPluginInfo.PLUGIN_GUID.GetId(TemplateConstants.CharacterTriggerEnum, "OnMarked");
            if (!triggerManager.TryGetValue(triggerId, out var onMarkedTrigger))
                return false;

            var friendlyUnits = new List<CharacterState>();
            room.AddCharactersToList(friendlyUnits, Team.Type.Monsters);

            foreach (var unit in friendlyUnits)
            {
                if (unit != null && unit.IsAlive) 
                {
                    combatManager.QueueTrigger(unit, onMarkedTrigger);
                }
            }

            return false;
        }
    }
}
