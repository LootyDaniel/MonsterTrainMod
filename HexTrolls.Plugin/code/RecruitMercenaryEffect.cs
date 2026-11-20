using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Recruits a live Mercenary using the original collectible unit's CardData.
    /// Deliberately does not generate new CardData or CharacterData, or replace
    /// the character's intrinsic ability. Logs any mismatch for investigation.
    /// </summary>
    public sealed class RecruitMercenaryEffect : CardEffectBase
    {
        private const string MercenaryStatusId = "hextrolls.plugin_mercenary";
        private const string CardlessStatusId = "cardless";
        private const float CardArtLoadTimeoutSeconds = 8f;

        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => true;

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }

        private static bool IsPlayableMonsterCard(CardData card)
        {
            if (card == null || card.GetCardType() != CardType.Monster || card.IsUnitAbility())
                return false;

            if (card.GetLinkedClass() == null)
                return false;

            string assetName = card.name ?? string.Empty;
            string[] rejected = { "Enemy", "Dummy", "Placeholder", "Generated", "Cardless", "Token" };
            if (rejected.Any(word => assetName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                return false;

            return !assetName.StartsWith("Debug", StringComparison.OrdinalIgnoreCase) &&
                   !assetName.StartsWith("Test", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetCollectibleCardIds()
        {
            // This is intentionally not cached: mod-added pools can finalize
            // after static initialization, and this method runs infrequently.
            var results = new HashSet<string>(StringComparer.Ordinal);
            var register = Railend.GetContainer().GetInstance<IRegister<CardPool>>();
            if (register == null)
                return results;

            var cards = new List<CardData>();
            foreach (var entry in register)
            {
                if (entry.Value == null ||
                    (entry.Key != null && entry.Key.IndexOf("Contract", StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                cards.Clear();
                entry.Value.GetAllChoices(cards);
                foreach (var card in cards)
                {
                    if (IsPlayableMonsterCard(card))
                        results.Add(card.GetID());
                }
            }

            return results;
        }

        private static bool SameCharacterAsset(CharacterData left, CharacterData right)
        {
            if (left == null || right == null)
                return false;

            // The asset name is more precise than the localized display name.
            return string.Equals(left.name, right.name, StringComparison.Ordinal);
        }

        private static int RankCandidate(CardData card, string spawnerCardId,
            HashSet<string> collectibleCardIds)
        {
            int rank = 0;

            if (!string.IsNullOrEmpty(spawnerCardId) &&
                string.Equals(card.GetID(), spawnerCardId, StringComparison.Ordinal))
                rank += 1000;

            if (collectibleCardIds.Contains(card.GetID()))
                rank += 500;

            // Tie breakers only. These are not substitutes for character identity.
            if (!string.IsNullOrWhiteSpace(card.GetOverrideDescriptionKey()))
                rank += 20;

            var triggers = card.GetCardTriggers();
            if (triggers != null && triggers.Count > 0)
                rank += 10;

            var traits = card.GetTraits();
            if (traits != null && traits.Count > 0)
                rank += 5;

            return rank;
        }

        private static CardData FindOriginalUnitCard(CharacterState target,
            AllGameData allGameData, bool logCandidates)
        {
            if (target == null || allGameData == null)
                return null;

            CharacterData source = target.GetSourceCharacterData();
            if (source == null)
                return null;

            var cards = allGameData.GetAllCardData()
                .Where(IsPlayableMonsterCard)
                .ToList();

            // Important: do NOT put name matches and exact CharacterData matches
            // into the same scored pool. That was the old failure mode.
            var exact = cards.Where(card =>
                ReferenceEquals(card.GetSpawnCharacterData(), source)).ToList();

            var sameAsset = cards.Where(card =>
                SameCharacterAsset(card.GetSpawnCharacterData(), source)).ToList();

            // Last-resort compatibility for cloned CharacterData with another
            // asset name. Never prefer it over either of the matches above.
            string displayName = source.GetName();
            var sameDisplayName = cards.Where(card =>
                !string.IsNullOrEmpty(displayName) &&
                string.Equals(card.GetName(), displayName, StringComparison.OrdinalIgnoreCase)).ToList();

            List<CardData> eligible = exact.Count > 0 ? exact :
                sameAsset.Count > 0 ? sameAsset : sameDisplayName;

            string spawnerCardId = target.GetSpawnerCard()?.GetCardDataID();
            var collectibleIds = GetCollectibleCardIds();

            if (logCandidates)
            {
                Debug.Log($"[RecruitContext] Live character: asset='{source.name}', name='{displayName}', " +
                          $"ability={DescribeAbility(source)}, spawnerId='{spawnerCardId ?? "none"}'");
                Debug.Log($"[RecruitContext] Matching tier: " +
                          (exact.Count > 0 ? "exact CharacterData reference" :
                           sameAsset.Count > 0 ? "same CharacterData asset name" : "display-name fallback") +
                          $", candidates={eligible.Count}");

                foreach (var card in eligible.OrderByDescending(c => RankCandidate(c, spawnerCardId, collectibleIds)))
                {
                    CharacterData candidateSource = card.GetSpawnCharacterData();
                    Debug.Log($"[RecruitCandidate] card='{card.name}' id='{card.GetID()}' " +
                              $"character='{candidateSource?.name ?? "null"}' " +
                              $"ability={DescribeAbility(candidateSource)} " +
                              $"pool={collectibleIds.Contains(card.GetID())} " +
                              $"score={RankCandidate(card, spawnerCardId, collectibleIds)}");
                }
            }

            // If name is the only match and it points at multiple different
            // character assets, choosing any one of them would be unsafe.
            if (exact.Count == 0 && sameAsset.Count == 0 &&
                sameDisplayName.Select(card => card.GetSpawnCharacterData()?.name)
                    .Distinct(StringComparer.Ordinal).Count() > 1)
            {
                if (logCandidates)
                    Debug.LogWarning("[Recruit] Ambiguous display-name match. Recruitment skipped.");
                return null;
            }

            return eligible
                .OrderByDescending(card => RankCandidate(card, spawnerCardId, collectibleIds))
                .ThenBy(card => card.name, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static bool CanRecruit(CharacterState target, ICoreGameManagers managers)
        {
            if (target == null || !target.IsAlive ||
                !target.HasStatusEffect(MercenaryStatusId) || !target.HasStatusEffect(CardlessStatusId))
                return false;

            return FindOriginalUnitCard(target, managers?.GetAllGameData(), false) != null;
        }

        /// <summary>
        /// Diagnostic only. An ability belongs to CharacterData, not to the
        /// collectible CardData's ordinary trigger/trait list. We avoid using
        /// unverified getter names as hard compile-time dependencies.
        /// </summary>
        private static string DescribeAbility(CharacterData character)
        {
            if (character == null)
                return "null";

            string[] names = { "GetAbility", "GetAbilityData", "GetUnitAbility", "GetInitialAbility" };
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in names)
            {
                try
                {
                    var method = character.GetType().GetMethod(name, flags, null, Type.EmptyTypes, null);
                    if (method == null)
                        continue;

                    object value = method.Invoke(character, null);
                    if (value is UnityEngine.Object unityValue)
                        return unityValue != null ? unityValue.name : "null";

                    return value?.ToString() ?? "null";
                }
                catch (Exception ex)
                {
                    return "getter-error:" + ex.GetType().Name;
                }
            }

            // If none of these methods exists, this diagnostic is inconclusive.
            return "getter-not-exposed";
        }

        // This checks the actual CardData art prefab, not an unrelated HUD/character sprite.
        // Used by another Monster Train 2 mod to determine when a card is ready to render.
        private static bool IsCardArtReady(CardData card)
        {
            if (card == null)
                return false;

            try
            {
                GameObject prefab = null;
                return card.GetCardArtPrefabVariant(out prefab) && prefab != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RecruitAssets] Art readiness check failed: {ex.Message}");
                return false;
            }
        }

        // Inspect the actual native card state, not a manually copied UI string.
        // The game may derive the visible body text from runtime card metadata.
        private static string RawCardBody(CardState card)
        {
            if (card == null) return "<no-card>";
            try
            {
                var field = typeof(CardState).GetField("cardText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return field?.GetValue(card) as string ?? "<null>";
            }
            catch (Exception ex) { return "<error:" + ex.GetType().Name + ">"; }
        }

        private static void LogNativeText(string stage, CardState card)
        {
            if (card == null)
            {
                Debug.Log($"[RecruitState] {stage}: null card");
                return;
            }

            string field = RawCardBody(card);
            string exposed = "<method unavailable>";
            try
            {
                MethodInfo method = typeof(CardState).GetMethod("GetCardText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (method != null && method.ReturnType == typeof(string))
                    exposed = (string)method.Invoke(card, null) ?? "<null>";
            }
            catch (Exception ex) { exposed = "<error:" + ex.GetType().Name + ">"; }

            Debug.Log($"[RecruitState] {stage}: cardId='{card.GetCardDataID()}', " +
                $"fieldLength={field.Length}, exposedLength={exposed.Length}, " +
                $"field='{field}', exposed='{exposed}'");
        }

        private static void LogNativeTextMethodSignatures()
        {
            // One-time method census: avoid guessing game method signatures,
            // especially on updated Monster Train 2 builds.
            if (_loggedTextMethods) return;
            _loggedTextMethods = true;

            foreach (var type in new[] { typeof(CardState), typeof(CharacterData) })
            {
                foreach (var method in type.GetMethods(BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (method.Name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0 &&
                        method.Name.IndexOf("Description", StringComparison.OrdinalIgnoreCase) < 0 &&
                        method.Name.IndexOf("Refetch", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var ps = method.GetParameters();
                    if (ps.Length > 6) continue;
                    Debug.Log($"[RecruitStateMethods] {type.Name}.{method.Name}(" +
                        string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name)) +
                        ") -> " + method.ReturnType.Name);
                }
            }
        }

        private static bool _loggedTextMethods;

        // Diagnostic only: test whether the original triggers exist in the NEW
        // CardState, or whether card text creation simply fails. No state edits.
        private static bool _loggedDeckMethodSignatures;

        private static void LogDeckMethodSignatures()
        {
            if (_loggedDeckMethodSignatures) return;
            _loggedDeckMethodSignatures = true;
            foreach (var method in typeof(SaveManager).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "AddCardToDeck"))
            {
                string args = string.Join(", ", method.GetParameters().Select(p =>
                    p.ParameterType.Name + " " + p.Name +
                    (p.IsOptional ? " [optional]" : "")));
                Debug.Log("[RecruitProbe] SaveManager.AddCardToDeck(" + args +
                    ") -> " + method.ReturnType.Name);
            }
        }

        private static string ProbeTextMethod(object instance, string name, object[] args)
        {
            if (instance == null) return "<null-instance>";
            try
            {
                MethodInfo method = instance.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == name &&
                        m.GetParameters().Length == args.Length);
                if (method == null) return "<no-method>";
                object result = method.Invoke(instance, args);
                return result == null ? "<null>" : result.ToString();
            }
            catch (TargetInvocationException ex)
            {
                return "<exception:" + (ex.InnerException?.GetType().Name ?? ex.GetType().Name) + ">";
            }
            catch (Exception ex)
            {
                return "<exception:" + ex.GetType().Name + ">";
            }
        }

        private static string TryCharacterNativeText(CharacterData character, CardState card,
            SaveManager saveManager, ICoreGameManagers managers)
        {
            if (character == null || card == null) return "<missing-character-or-card>";
            try
            {
                // Six-argument signature discovered in the user's MT2 runtime log.
                MethodInfo method = typeof(CharacterData).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "GetCharacterCardText" &&
                        m.GetParameters().Length == 6);
                if (method == null) return "<no-method>";

                object relicManager = null;
                if (managers != null)
                {
                    MethodInfo getRelics = managers.GetType().GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "GetRelicManager" &&
                            m.GetParameters().Length == 0);
                    if (getRelics != null)
                        relicManager = getRelics.Invoke(managers, null);
                }

                object[] args = { string.Empty, card, relicManager, saveManager, true, null };
                method.Invoke(character, args);
                return args[0] as string ?? "<null-result>";
            }
            catch (TargetInvocationException ex)
            {
                return "<exception:" + (ex.InnerException?.GetType().Name ?? ex.GetType().Name) + ">";
            }
            catch (Exception ex)
            {
                return "<exception:" + ex.GetType().Name + ">";
            }
        }

        private static void LogStateProbe(string stage, CardState card,
            CharacterData source, SaveManager saveManager, ICoreGameManagers managers)
        {
            if (card == null)
            {
                Debug.Log("[RecruitProbe] " + stage + ": no card state");
                return;
            }
            CharacterData cardCharacter = null;
            try { cardCharacter = card.GetSpawnCharacterData(); }
            catch (Exception ex) { Debug.LogWarning("[RecruitProbe] Character getter: " + ex.Message); }
            Debug.Log($"[RecruitProbe] {stage} " +
                $"cardId={card.GetCardDataID()} " +
                $"stateType={ProbeTextMethod(card, "GetCardType", new object[0])} " +
                $"charAsset='{cardCharacter?.name ?? "null"}' " +
                $"sameChar={ReferenceEquals(cardCharacter, source)} " +
                $"body='{RawCardBody(card)}'");

            // These return the actual native trigger/ability text independently
            // of CardState.cardText (the displayed/cached card body).
            Debug.Log("[RecruitProbe] " + stage + " trigger=" +
                ProbeTextMethod(card, "GenerateTriggerText", new object[] { false }));
            Debug.Log("[RecruitProbe] " + stage + " ability=" +
                ProbeTextMethod(card, "GenerateUnitOrRoomAbilityText", new object[0]));
            Debug.Log("[RecruitProbe] " + stage + " getCardText=" +
                ProbeTextMethod(card, "GetCardText", new object[] { null, saveManager, true }));
            Debug.Log("[RecruitProbe] " + stage + " characterNativeText=" +
                TryCharacterNativeText(source, card, saveManager, managers));
        }

        public override bool TestEffect(CardEffectState cardEffectState,
            CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers)
        {
            if (cardEffectParams?.targets == null || cardEffectParams.targets.Count == 0)
                return true;

            return cardEffectParams.targets.Any(t => CanRecruit(t, coreGameManagers));
        }

        public override IEnumerator ApplyEffect(CardEffectState cardEffectState,
            CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers,
            ISystemManagers sysManagers)
        {
            var saveManager = coreGameManagers?.GetSaveManager();
            if (saveManager == null || saveManager.PreviewMode ||
                cardEffectParams?.targets == null || cardEffectParams.targets.Count == 0)
                yield break;

            CharacterState target = cardEffectParams.targets
                .FirstOrDefault(t => t != null && t.IsAlive &&
                    t.HasStatusEffect(MercenaryStatusId) && t.HasStatusEffect(CardlessStatusId));
            if (target == null)
                yield break;

            CardData originalCard = FindOriginalUnitCard(target,
                coreGameManagers.GetAllGameData(), true);
            if (originalCard == null)
            {
                Debug.LogWarning("[Recruit] No unambiguous collectible card found. Mercenary left in battle.");
                yield break;
            }

            CharacterData sourceCharacter = target.GetSourceCharacterData();
            CharacterData recruitedCharacter = originalCard.GetSpawnCharacterData();
            Debug.Log($"[Recruit] Using '{originalCard.GetName()}' " +
                      $"(cardId='{originalCard.GetID()}', cardAsset='{originalCard.name}')");
            Debug.Log($"[RecruitContext] live='{sourceCharacter?.name}', " +
                      $"recruited='{recruitedCharacter?.name}', " +
                      $"sameReference={ReferenceEquals(sourceCharacter, recruitedCharacter)}, " +
                      $"liveAbility={DescribeAbility(sourceCharacter)}, " +
                      $"recruitedAbility={DescribeAbility(recruitedCharacter)}");

            // The card may belong to a clan outside the run's main/allied clans.
            // Request its addressable card art BEFORE AddCardToDeck builds its UI.
            // LoadCardsForCompendium is an actual public AssetLoadingManager API;
            // don't try to invent LoadCardData / StartLoad signatures.
            bool artReadyBefore = IsCardArtReady(originalCard);
            Debug.Log($"[RecruitAssets] card='{originalCard.name}' " +
                      $"type={originalCard.GetCardType()} " +
                      $"class='{originalCard.GetLinkedClass()?.name}' " +
                      $"artReadyBefore={artReadyBefore}");

            if (!artReadyBefore)
            {
                var loader = AssetLoadingManager.GetInst();
                if (loader == null)
                {
                    Debug.LogWarning("[RecruitAssets] AssetLoadingManager is unavailable; continuing recruitment without art preload.");
                }
                else
                {
                    bool callbackReceived = false;
                    bool requestStarted = false;
                    try
                    {
                        loader.LoadCardsForCompendium(
                            new List<CardData> { originalCard },
                            null,
                            () => { callbackReceived = true; });
                        requestStarted = true;
                        Debug.Log("[RecruitAssets] Requested card art via LoadCardsForCompendium.");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[RecruitAssets] Card art request failed: {ex}");
                    }

                    if (requestStarted)
                    {
                        float startedAt = Time.realtimeSinceStartup;
                        while (!callbackReceived &&
                               Time.realtimeSinceStartup - startedAt < CardArtLoadTimeoutSeconds)
                        {
                            yield return null;
                        }

                        bool artReadyAfter = IsCardArtReady(originalCard);
                        Debug.Log($"[RecruitAssets] callbackReceived={callbackReceived}, " +
                                  $"artReadyAfter={artReadyAfter}, " +
                                  $"timedOut={!callbackReceived}");
                        if (!artReadyAfter)
                        {
                            Debug.LogWarning("[RecruitAssets] Art still unavailable. " +
                                "The card will be recruited, but it may display placeholder art. " +
                                "Check addressable loading errors in LogOutput.log.");
                        }
                    }
                }
            }

            // The contract effect spawns a real CharacterData, but its spawner
            // CardState can contain modifiers that are NOT visible in the unit's
            // applied-upgrades list. Copy both sources before creating the deck card.
            // We never create or alter a CardData or CharacterData.
            CardState sourceCardState = target.GetSpawnerCard();
            var modifiers = new CardStateModifiers();
            var seenUpgrades = new HashSet<CardUpgradeState>();
            int fromSpawner = 0;
            int fromUnit = 0;

            // Preserve modifiers on the source card (including upgrades that
            // were present when the mercenary was initially summoned).
            if (sourceCardState != null)
            {
                var sourceModifiers = sourceCardState.GetCardStateModifiers();
                if (sourceModifiers != null)
                {
                    var sourceUpgrades = sourceModifiers.GetCardUpgrades();
                    if (sourceUpgrades != null)
                    {
                        foreach (var upgrade in sourceUpgrades)
                        {
                            if (upgrade != null && seenUpgrades.Add(upgrade))
                            {
                                modifiers.AddUpgrade(upgrade);
                                fromSpawner++;
                            }
                        }
                    }
                }
            }

            // Preserve battle-granted upgrades such as the Commander's
            // random basic/elite upgrades. These are not shop purchases and
            // must not be removed, rejected, or replaced by the Mercenary lock.
            var appliedUpgrades = target.GetAppliedCardUpgrades();
            if (appliedUpgrades != null)
            {
                foreach (var upgrade in appliedUpgrades)
                {
                    if (upgrade != null && seenUpgrades.Add(upgrade))
                    {
                        modifiers.AddUpgrade(upgrade);
                        fromUnit++;
                    }
                }
            }

            Debug.Log($"[RecruitPreserve] originalCard='{originalCard.name}', " +
                      $"spawnerMatches={sourceCardState != null && sourceCardState.GetCardDataID() == originalCard.GetID()}, " +
                      $"spawnerUpgrades={fromSpawner}, unitOnlyUpgrades={fromUnit}, " +
                      $"totalCopied={seenUpgrades.Count}");

            CardUpgradeData mercenaryLock = cardEffectState?.GetParamCardUpgradeData();
            if (mercenaryLock == null)
            {
                Debug.LogError("[RecruitPreserve] RecruitSpell has no param_upgrade. Recruitment aborted to avoid creating a card without the shop lock.");
                yield break;
            }

            // IMPORTANT: Add the native card with its existing upgrades FIRST.
            // A shop-lock upgrade is a distinct concern and should not be mixed
            // into the initial CardStateModifiers that construct the unit card.
            CardState addedCard = null;
            try
            {
                addedCard = saveManager.AddCardToDeck(
                    originalCard, modifiers,
                    true, 0, false, false,
                    true,   // setupStartingUpgrades
                    false,  // setPurified: NEVER erase native abilities/statuses
                    true);  // notifyDeckCount
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Recruit] AddCardToDeck failed: {ex}");
            }

            // Never remove the battle unit unless a new deck card was created.
            if (addedCard == null)
            {
                Debug.LogWarning("[Recruit] No card added; Mercenary left in battle.");
                yield break;
            }

            LogDeckMethodSignatures();
            LogStateProbe("SPAWNER", sourceCardState, sourceCharacter, saveManager, coreGameManagers);
            LogStateProbe("NEW_BEFORE_REFRESH", addedCard, sourceCharacter, saveManager, coreGameManagers);

            // Keep the original CardData / CharacterData. We have confirmed that
            // RefetchAssets alone does not generate the card's native text.
            // The real CardState.UpdateCardBodyText method takes SaveManager;
            // earlier versions invoked it with the default (null) argument.
            // This test passes the live SaveManager explicitly.
            LogNativeTextMethodSignatures();
            LogNativeText("SPAWNER", sourceCardState);
            LogNativeText("NEW_BEFORE_REFETCH", addedCard);
            try
            {
                addedCard.RefetchAssets(originalCard);
                LogNativeText("NEW_AFTER_REFETCH", addedCard);
                addedCard.RefreshCardBodyTextLocalization();
                LogNativeText("NEW_AFTER_LOCALIZATION_ONLY", addedCard);
                addedCard.UpdateCardBodyText(saveManager);
                LogNativeText("NEW_AFTER_UPDATE_WITH_SAVE_MANAGER", addedCard);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RecruitState] Native refetch/refresh failed: " + ex);
            }

            LogStateProbe("NEW_AFTER_REFRESH", addedCard, sourceCharacter, saveManager, coreGameManagers);

            // Apply the shop marker using the game's NORMAL permanent-upgrade
            // pipeline only after the original card has been built. This keeps
            // original effects and Commander buffs separate from the lock.
            try
            {
                var lockState = new CardUpgradeState();
                lockState.Setup(mercenaryLock);
                addedCard.ApplyPermanentUpgrade(lockState, saveManager, true, null);

                bool lockFound = addedCard.GetCardStateModifiers()?.GetCardUpgrades()?.Any(u =>
                    u != null && u.GetStatusEffectUpgrades() != null &&
                    u.GetStatusEffectUpgrades().Any(status =>
                        status != null && status.statusId == MercenaryStatusId)) == true;
                Debug.Log($"[RecruitPreserve] Applied shop lock AFTER card creation; lockFound={lockFound}");
                if (!lockFound)
                    Debug.LogError("[RecruitPreserve] Shop-lock marker missing on added card. Check the upgrade registration and filter patch.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[RecruitPreserve] Failed to apply shop lock: " + ex);
                // Recruitment already created a deck card; log a hard failure
                // instead of silently claiming the shop restriction works.
            }

            try
            {
                // The vanilla refresh preserves the original card's localized
                // descriptions and then incorporates persistent upgrades.
                addedCard.RefreshCardBodyTextLocalization();
                LogNativeText("AFTER_LOCK_LOCALIZATION_ONLY", addedCard);
                addedCard.UpdateCardBodyText(saveManager);
                LogNativeText("AFTER_LOCK_UPDATE_WITH_SAVE_MANAGER", addedCard);
                LogStateProbe("NEW_AFTER_LOCK", addedCard, sourceCharacter, saveManager, coreGameManagers);
                Debug.Log($"[RecruitAssets] artReadyAfterAdd={IsCardArtReady(originalCard)}");
                Debug.Log($"[Recruit] Added to deck: cardId='{addedCard.GetCardDataID()}'");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Recruit] Card text refresh warning: {ex.Message}");
            }

            // Previously working sacrifice signature; suppresses death triggers.
            yield return target.Sacrifice(null, true, false, null);
        }
    }
}
