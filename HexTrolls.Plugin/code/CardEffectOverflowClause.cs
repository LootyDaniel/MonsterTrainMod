using System;
using System.Collections;
using System.Collections.Generic;
using TrainworksReloaded.Base;
using TrainworksReloaded.Base.Extensions;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Artifact's native RelicEffectAddEffectOnUnitSpawn calls this after allied spawns.
    /// Count UNIT INSTANCES, not their size/capacity. At exactly seven, attempt to
    /// bump the frontmost Trap down a floor, then move it to the new room's front.
    /// If either the trap or destination is missing, do nothing; never reject a summon.
    /// </summary>
    public sealed class CardEffectOverflowClause : CardEffectBase
    {
        private const string MoveEffectCardId = "OverflowClauseInternalMoveEffects";
        private static readonly string[] KnownTrapAssets =
        {
            "ConcussiveTrapCharacter", "StunTrapCharacter", "AttackTrapCharacter"
        };

        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => false;

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }

        public override IEnumerator ApplyEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers,
            ISystemManagers systemManagers)
        {
            var saveManager = coreGameManagers?.GetSaveManager();
            var roomManager = coreGameManagers?.GetRoomManager();
            if (saveManager == null || saveManager.PreviewMode ||
                roomManager == null || cardEffectParams == null)
                yield break;

            int roomIndex = cardEffectParams.selectedRoom;
            var room = roomManager.GetRoom(roomIndex);
            if (room == null)
            {
                Debug.LogWarning($"[OverflowClause] Source room {roomIndex} not found.");
                yield break;
            }

            // Zero is the bottom floor, so it has nowhere to go downward.
            // Room Index 3 is the Pyre room; native relocation handles eligibility.
            var allies = new List<CharacterState>();
            room.AddCharactersToList(allies, Team.Type.Monsters);
            int liveCount = 0;
            foreach (var ally in allies)
            {
                if (ally != null && ally.IsAlive && !ally.IsDestroyed)
                    ++liveCount;
            }

            if (liveCount != 7)
                yield break;

            if (roomIndex <= 0)
            {
                Debug.Log("[OverflowClause] Seventh ally on bottom floor. No lower room; leaving all seven.");
                yield break;
            }

            // AddCharactersToList enumerates room positions. Select first eligible Trap
            // from its ordered monster list (the foremost eligible Trap, not necessarily
            // the absolute front unit). All three Bounty traps declare the @Trap subtype.
            CharacterState trap = null;
            foreach (var ally in allies)
            {
                if (ally != null && ally.IsAlive && !ally.IsDestroyed && IsTrap(ally))
                {
                    trap = ally;
                    break;
                }
            }
            if (trap == null)
            {
                Debug.Log($"[OverflowClause] Seventh ally on floor {roomIndex}: no Trap; leaving all seven.");
                yield break;
            }

            var below = roomManager.GetRoom(roomIndex - 1);
            if (below == null)
            {
                Debug.Log($"[OverflowClause] No room below floor {roomIndex}; leaving all seven.");
                yield break;
            }

            var register = Railend.GetContainer().GetInstance<IRegister<CardData>>();
            string helperId = MyPluginInfo.PLUGIN_GUID.GetId(
                TemplateConstants.Card, MoveEffectCardId);
            if (register == null || !register.TryGetValue(helperId, out CardData helper) || helper == null)
            {
                Debug.LogWarning($"[OverflowClause] Helper card missing: {helperId}; leaving all seven.");
                yield break;
            }

            var effectData = helper.GetEffects();
            if (effectData == null || effectData.Count != 2 ||
                effectData[0] == null || effectData[1] == null)
            {
                Debug.LogWarning("[OverflowClause] Expected Bump and FloorRearrange helper effects; leaving all seven.");
                yield break;
            }

            // Only the chosen Trap should receive the native effects.
            var moveParams = new CardEffectParams
            {
                selectedRoom = roomIndex,
                targets = new List<CharacterState> { trap }
            };
            var bumpState = new CardEffectState();
            bumpState.Setup(effectData[0], null);
            var bump = bumpState.GetCardEffect();

            if (bump == null || !bump.TestEffect(bumpState, moveParams, coreGameManagers))
            {
                Debug.Log($"[OverflowClause] Native descend disallowed for '{trap.GetName()}'; leaving all seven.");
                yield break;
            }

            Debug.Log($"[OverflowClause] Seven allies on floor {roomIndex}. " +
                      $"Attempting to descend frontmost Trap '{trap.GetName()}'.");
            yield return bump.ApplyEffect(bumpState, moveParams, coreGameManagers, systemManagers);

            // The native effect may reject an actual move for capacity, immobility,
            // combat state or another game rule. Never forcibly relocate the unit.
            if (trap.IsDestroyed || !trap.IsAlive || trap.GetCurrentRoom() != below)
            {
                Debug.Log("[OverflowClause] Native move did not complete; seven units are allowed to remain.");
                yield break;
            }

            // Moving down may put the Trap in another position; move it to the front
            // only after verifying the native descend succeeded.
            moveParams.selectedRoom = roomIndex - 1;
            var frontState = new CardEffectState();
            frontState.Setup(effectData[1], null);
            var front = frontState.GetCardEffect();
            if (front != null && front.TestEffect(frontState, moveParams, coreGameManagers))
            {
                yield return front.ApplyEffect(frontState, moveParams, coreGameManagers, systemManagers);
            }
            else
            {
                Debug.LogWarning("[OverflowClause] Trap descended, but native front placement was not allowed.");
            }

            Debug.Log($"[OverflowClause] '{trap.GetName()}' moved to floor {roomIndex - 1}.");
        }

        private static bool IsTrap(CharacterState unit)
        {
            var data = unit.GetSourceCharacterData();
            if (data == null)
                return false;

            // Work with the existing, registered Trap subtype rather than with a
            // temporary marker/status. Check actual SubtypeData assets first.
            var subtypes = data.GetSubtypes();
            if (subtypes != null)
            {
                foreach (var subtype in subtypes)
                {
                    if (subtype == null) continue;
                    var id = subtype.LocalizedName ?? string.Empty;
                    if (id.Equals("Trap", StringComparison.OrdinalIgnoreCase) ||
                        id.EndsWith("_Trap", StringComparison.OrdinalIgnoreCase) ||
                        id.EndsWith("-Trap", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Asset-name safety net for the three currently implemented Bounty traps.
            // This also logs correctly if a mod loader changes the subtype asset ID.
            foreach (string asset in KnownTrapAssets)
                if (string.Equals(data.name, asset, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }
}
