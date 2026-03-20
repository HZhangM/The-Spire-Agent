using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPlayMod.Core;

/// <summary>
/// Handles each game phase: collects state, executes agent's action.
/// Returns true if an action was taken, false if nothing to do.
/// </summary>
public class PhaseHandler
{
    public INonCombatAdvisor Advisor { get; set; } = new DefaultAdvisor();
    public Agent.UnifiedGameAgent? CombatAgent { get; set; }
    public IPlayStrategy? CombatStrategy { get; set; }

    private const int ActionDelayMs = 500;
    private bool _proceedTriggered;
    private int _lastRewardCount = -1;
    private int _rewardStuckCount;
    private readonly HashSet<int> _skippedRewardIndices = new();

    /// <summary>
    /// Handle the current phase. Returns true if something was done.
    /// </summary>
    public async Task<bool> HandlePhase(GamePhase phase, CancellationToken ct)
    {
        _proceedTriggered = phase != GamePhase.RewardsScreen ? false : _proceedTriggered;

        return phase switch
        {
            GamePhase.CombatPlayerTurn => await HandleCombatTurn(ct),
            GamePhase.CombatHandSelect => await HandleCombatHandSelect(ct),
            GamePhase.CombatOverlaySelect => await HandleCombatOverlaySelect(ct),
            GamePhase.RewardsScreen => await HandleRewards(ct),
            GamePhase.CardRewardSelect => await HandleCardReward(ct),
            GamePhase.CardGridSelect => await HandleCardGridSelect(ct),
            GamePhase.MapScreen => await HandleMap(ct),
            GamePhase.EventRoom => await HandleEvent(ct),
            GamePhase.RestSite => await HandleRestSite(ct),
            GamePhase.Shop => await HandleShop(ct),
            GamePhase.TreasureRoom => await HandleTreasure(ct),
            GamePhase.GenericOverlay => await HandleGenericOverlay(ct),
            _ => false,
        };
    }

    #region Combat Phases

    private Task<bool> HandleCombatTurn(CancellationToken ct)
    {
        // Combat is driven by TurnStarted event in AutoPlayer, not by the poll loop.
        // This should not be called, but return false to be safe.
        return Task.FromResult(false);
    }

    private async Task<bool> HandleCombatHandSelect(CancellationToken ct)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection) return false;

        // Collect selectable cards
        var validHolders = new List<NHandCardHolder>();
        var cardNames = new List<string>();
        foreach (var holder in hand.ActiveHolders)
        {
            if (!GodotObject.IsInstanceValid(holder) || !holder.Visible || holder.CardNode == null) continue;
            validHolders.Add(holder);
            cardNames.Add(FormatCard(holder.CardNode.Model));
        }
        if (validHolders.Count == 0) return false;

        var purpose = hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect
            ? CardSelectionPurpose.UpgradeInHand : CardSelectionPurpose.Other;

        int chosenIndex = await ChooseCardRouted(purpose, $"Hand selection ({hand.CurrentMode})", cardNames, false);

        var chosen = validHolders[Math.Clamp(chosenIndex, 0, validHolders.Count - 1)];
        Log.Info($"[AutoPlay] Hand select: '{cardNames[Math.Clamp(chosenIndex, 0, cardNames.Count - 1)]}'");
        chosen.EmitSignal(NCardHolder.SignalName.Pressed, chosen);
        await Task.Delay(300, ct);

        // Click confirm
        await WaitAndClickConfirm((Node)hand, "%SelectModeConfirmButton", ct);
        return true;
    }

    private async Task<bool> HandleCombatOverlaySelect(CancellationToken ct)
    {
        var stack = NOverlayStack.Instance;
        if (stack == null || stack.ScreenCount == 0) return false;
        var screen = stack.Peek() as Node;
        if (screen == null) return false;

        return await HandleCardGridSelectInternal(screen, "Combat card select", ct);
    }

    #endregion

    #region Non-Combat Phases

    private async Task<bool> HandleRewards(CancellationToken ct)
    {
        var stack = NOverlayStack.Instance;
        if (stack == null || stack.ScreenCount == 0) return false;
        var rewardsScreen = stack.Peek() as NRewardsScreen;
        if (rewardsScreen == null) return false;
        var screenNode = (Node)rewardsScreen;

        // Click one reward button at a time, skipping ones that can't be collected
        var rewardButtons = UiHelper.FindAll<NRewardButton>(screenNode);
        if (rewardButtons.Count > 0)
        {
            // Detect stuck on a specific button: same count after clicking
            if (rewardButtons.Count == _lastRewardCount)
            {
                _rewardStuckCount++;
            }
            else
            {
                _rewardStuckCount = 0;
                _skippedRewardIndices.Clear();
            }
            _lastRewardCount = rewardButtons.Count;

            // If stuck on current button (3 retries), mark it as skipped, try next
            if (_rewardStuckCount >= 3)
            {
                if (!_skippedRewardIndices.Contains(0))
                {
                    Log.Info("[AutoPlay] Rewards: reward can't be collected (e.g. potions full), skipping");
                    _skippedRewardIndices.Add(_skippedRewardIndices.Count);
                }
                _rewardStuckCount = 0;
            }

            // Find the first non-skipped reward
            for (int i = _skippedRewardIndices.Count; i < rewardButtons.Count; i++)
            {
                var btn = rewardButtons[i];
                if (GodotObject.IsInstanceValid(btn))
                {
                    Log.Info($"[AutoPlay] Rewards: clicking reward {i} ({rewardButtons.Count} total, {_skippedRewardIndices.Count} skipped)");
                    await UiHelper.Click(btn, 100);
                    await Task.Delay(ActionDelayMs, ct);
                    return true;
                }
            }

            // All rewards skipped — fall through to proceed
            Log.Info("[AutoPlay] Rewards: all remaining rewards uncollectable, proceeding");
        }

        _lastRewardCount = -1;
        _rewardStuckCount = 0;
        _skippedRewardIndices.Clear();

        var linkedRewards = UiHelper.FindAll<NLinkedRewardSet>(screenNode);
        if (linkedRewards.Count > 0)
        {
            var clickables = UiHelper.FindAll<NClickableControl>(linkedRewards[0]);
            foreach (var c in clickables)
            {
                if (!GodotObject.IsInstanceValid(c)) continue;
                if (c is NButton nb && !nb.IsEnabled) continue;
                Log.Info("[AutoPlay] Rewards: clicking linked reward");
                await UiHelper.Click(c, 100);
                await Task.Delay(ActionDelayMs, ct);
                return true;
            }
        }

        // No more rewards (or all uncollectable) — proceed
        if (!_proceedTriggered)
        {
            _proceedTriggered = true;
            Log.Info("[AutoPlay] Rewards: removing overlay + proceeding");
            NOverlayStack.Instance.Remove(rewardsScreen);
            TaskHelper.RunSafely(RunManager.Instance.ProceedFromTerminalRewardsScreen());
            await Task.Delay(ActionDelayMs * 4, ct);
        }
        return true;
    }

    private async Task<bool> HandleCardReward(CancellationToken ct)
    {
        var stack = NOverlayStack.Instance;
        if (stack == null || stack.ScreenCount == 0) return false;
        var screen = stack.Peek() as Node;
        if (screen == null) return false;

        bool selected = await SelectCardFromScreen(screen, "Card reward", CardSelectionPurpose.Reward, canSkip: true);
        if (!selected)
        {
            Log.Info("[AutoPlay] Card reward: skipping");
            await ClickProceed(screen, ct);
        }
        await Task.Delay(ActionDelayMs, ct);
        return true;
    }

    private async Task<bool> HandleCardGridSelect(CancellationToken ct)
    {
        var stack = NOverlayStack.Instance;
        if (stack == null || stack.ScreenCount == 0) return false;
        var screen = stack.Peek() as Node;
        if (screen == null) return false;

        return await HandleCardGridSelectInternal(screen, screen.GetType().Name, ct);
    }

    private async Task<bool> HandleMap(CancellationToken ct)
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null || !mapScreen.IsOpen || mapScreen.IsTraveling) return false;

        var travelablePoints = new List<NMapPoint>();
        foreach (var point in UiHelper.FindAll<NMapPoint>(mapScreen))
            if (point.State == MapPointState.Travelable)
                travelablePoints.Add(point);

        if (travelablePoints.Count == 0) return false;

        int chosenIndex = 0;
        if (travelablePoints.Count > 1)
        {
            var nodeInfos = travelablePoints.Select((p, i) => new MapNodeInfo
            {
                Index = i,
                Type = p.Point.PointType.ToString(),
                Row = p.Point.coord.row,
                Col = p.Point.coord.col,
            }).ToList();

            try
            {
                var summary = CollectGameSummary();
                chosenIndex = await Advisor.ChooseMapNode(nodeInfos, summary);
                if (chosenIndex < 0 || chosenIndex >= travelablePoints.Count) chosenIndex = 0;
            }
            catch (Exception ex)
            {
                Log.Warn($"[AutoPlay] Map advisor failed: {ex.Message}");
            }
        }
        else
        {
            Log.Info("[AutoPlay] Map: only 1 node, auto-selecting");
        }

        var target = travelablePoints[chosenIndex];
        Log.Info($"[AutoPlay] Map: selecting [{target.Point.coord.col},{target.Point.coord.row}] type={target.Point.PointType}");

        if (mapScreen.IsTravelEnabled)
            target.ForceClick();
        else
            mapScreen.OnMapPointSelectedLocally(target);

        await Task.Delay(ActionDelayMs * 4, ct);
        return true;
    }

    private async Task<bool> HandleEvent(CancellationToken ct)
    {
        var game = NGame.Instance;
        if (game == null) return false;
        var eventRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
        if (eventRoom == null) return false;

        var optionButtons = UiHelper.FindAll<NEventOptionButton>(eventRoom);
        var validButtons = new List<NEventOptionButton>();
        var optionTexts = new List<string>();
        foreach (var btn in optionButtons)
        {
            if (!GodotObject.IsInstanceValid(btn) || !btn.IsEnabled) continue;
            validButtons.Add(btn);
            string text = "Option";
            try
            {
                var label = ((Node)btn).GetNodeOrNull<Godot.RichTextLabel>("%Text");
                if (label != null) text = label.Text ?? "Option";
            }
            catch { }
            optionTexts.Add(text);
        }
        if (validButtons.Count == 0)
        {
            // No options yet — try proceed, or return false to retry next poll
            var proceeds = UiHelper.FindAll<NProceedButton>(eventRoom);
            foreach (var btn in proceeds)
            {
                if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
                {
                    Log.Info("[AutoPlay] Event: clicking proceed");
                    await UiHelper.Click(btn, 100);
                    await Task.Delay(ActionDelayMs, ct);
                    return true;
                }
            }
            // Buttons not loaded yet — return false, will retry on next poll
            return false;
        }

        // Get event ID from game state for proper naming
        string eventId = "unknown_event";
        string eventDesc = "Event";
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.EventRoom er)
                eventId = er.CanonicalEvent?.Id?.Entry ?? "unknown_event";

            var layout = ((Node)eventRoom).GetNodeOrNull("EventLayout");
            var descLabel = layout?.GetNodeOrNull<Godot.RichTextLabel>("%EventDescription");
            if (descLabel != null) eventDesc = descLabel.Text ?? "Event";
        }
        catch { }

        int chosenIndex = 0;
        try
        {
            var summary = CollectGameSummary();
            chosenIndex = await Advisor.ChooseEventOption($"[{eventId}] {eventDesc}", optionTexts, summary);
            if (chosenIndex < 0 || chosenIndex >= validButtons.Count) chosenIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay] Event advisor failed: {ex.Message}");
        }

        Log.Info($"[AutoPlay] Event [{eventId}]: option {chosenIndex}: {optionTexts[chosenIndex]}");
        await UiHelper.Click(validButtons[chosenIndex], 100);
        await Task.Delay(ActionDelayMs * 3, ct);
        await WaitForProceed(eventRoom, "Event", ct);
        return true;
    }

    private async Task<bool> HandleRestSite(CancellationToken ct)
    {
        var game = NGame.Instance;
        if (game == null) return false;
        var restRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/RestSiteRoom");
        if (restRoom == null) return false;

        var restButtons = UiHelper.FindAll<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton>(restRoom);
        var validButtons = new List<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton>();
        var optionTexts = new List<string>();
        foreach (var btn in restButtons)
        {
            if (!GodotObject.IsInstanceValid(btn) || !btn.IsEnabled) continue;
            validButtons.Add(btn);
            string text = "Option";
            try
            {
                var label = ((Node)btn).GetNodeOrNull<Godot.Label>("%Label");
                if (label != null) text = label.Text ?? "Option";
            }
            catch { }
            optionTexts.Add(text);
        }
        if (validButtons.Count == 0)
        {
            // No rest buttons — try proceed, or return false to retry
            var proceeds = UiHelper.FindAll<NProceedButton>(restRoom);
            foreach (var btn in proceeds)
            {
                if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
                {
                    Log.Info("[AutoPlay] Rest site: clicking proceed");
                    await UiHelper.Click(btn, 100);
                    await Task.Delay(ActionDelayMs, ct);
                    return true;
                }
            }
            return false;
        }

        int chosenIndex = 0;
        try
        {
            var summary = CollectGameSummary();
            chosenIndex = await Advisor.ChooseRestSiteOption(optionTexts, summary);
            if (chosenIndex < 0 || chosenIndex >= validButtons.Count) chosenIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay] Rest advisor failed: {ex.Message}");
        }

        Log.Info($"[AutoPlay] Rest: option {chosenIndex}: {optionTexts[chosenIndex]}");
        await UiHelper.Click(validButtons[chosenIndex], 100);
        await Task.Delay(ActionDelayMs * 3, ct);
        await WaitForProceed(restRoom, "Rest site", ct);
        return true;
    }

    private async Task<bool> HandleShop(CancellationToken ct)
    {
        var game = NGame.Instance;
        if (game == null) return false;
        var shopRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/MerchantRoom");
        if (shopRoom == null) return false;

        // TODO: implement shop agent (buy cards/relics/potions, remove cards)
        Log.Info("[AutoPlay] Shop: no purchase logic yet, leaving");
        await Task.Delay(ActionDelayMs * 2, ct);
        await ClickProceed(shopRoom, ct);
        return true;
    }

    private async Task<bool> HandleTreasure(CancellationToken ct)
    {
        var game = NGame.Instance;
        if (game == null) return false;
        var treasureRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/TreasureRoom");
        if (treasureRoom == null) return false;

        var clickables = UiHelper.FindAll<NClickableControl>(treasureRoom);
        foreach (var btn in clickables)
        {
            if (!GodotObject.IsInstanceValid(btn)) continue;
            if (btn is NProceedButton) continue;
            if (btn is NButton nb && !nb.IsEnabled) continue;
            Log.Info("[AutoPlay] Treasure: opening chest");
            await UiHelper.Click(btn, 100);
            await Task.Delay(ActionDelayMs * 3, ct);
            break;
        }

        await WaitForProceed(treasureRoom, "Treasure", ct);
        return true;
    }

    private async Task<bool> HandleGenericOverlay(CancellationToken ct)
    {
        var stack = NOverlayStack.Instance;
        if (stack == null || stack.ScreenCount == 0) return false;
        var screen = stack.Peek() as Node;
        if (screen == null) return false;

        // Try confirm → card select → proceed
        if (await TryClickConfirmButton(screen, ct)) return true;
        await ClickProceed(screen, ct);
        return true;
    }

    #endregion

    #region Shared Helpers

    private async Task<bool> HandleCardGridSelectInternal(Node screen, string context, CancellationToken ct)
    {
        var gridScreen = screen as NCardGridSelectionScreen;
        var purpose = gridScreen switch
        {
            NDeckUpgradeSelectScreen => CardSelectionPurpose.UpgradeFromDeck,
            NDeckTransformSelectScreen => CardSelectionPurpose.Transform,
            NDeckCardSelectScreen => CardSelectionPurpose.Remove,
            NSimpleCardSelectScreen => CardSelectionPurpose.Other,
            _ => CardSelectionPurpose.Other,
        };

        // Check if confirm is already available
        if (await TryClickConfirmButton(screen, ct)) return true;

        // Find the internal _grid for the correct card holders
        Node searchRoot = screen;
        if (gridScreen != null)
        {
            try
            {
                var gridField = typeof(NCardGridSelectionScreen).GetField("_grid",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (gridField?.GetValue(gridScreen) is Node grid) searchRoot = grid;
            }
            catch { }
        }

        bool clicked = await SelectCardFromScreen(searchRoot, context, purpose);
        if (!clicked)
        {
            await ClickProceed(screen, ct);
            return true;
        }

        // Wait for confirm
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(300, ct);
            if (await TryClickConfirmButton(screen, ct)) return true;
        }

        await ClickProceed(screen, ct);
        return true;
    }

    private async Task<bool> SelectCardFromScreen(Node root, string context,
        CardSelectionPurpose purpose, bool canSkip = false)
    {
        var validHolders = new List<NCardHolder>();
        var cardNames = new List<string>();

        foreach (var holder in UiHelper.FindAll<NCardHolder>(root))
        {
            if (!GodotObject.IsInstanceValid(holder) || holder.CardNode == null) continue;
            validHolders.Add(holder);
            cardNames.Add(FormatCard(holder.CardModel));
        }

        // Fallback: search NCardGrid children
        if (validHolders.Count == 0)
        {
            foreach (var grid in UiHelper.FindAll<NCardGrid>(root))
            {
                foreach (var h in UiHelper.FindAll<NCardHolder>(grid))
                {
                    if (!GodotObject.IsInstanceValid(h) || h.CardNode == null) continue;
                    validHolders.Add(h);
                    cardNames.Add(FormatCard(h.CardModel));
                }
            }
        }

        if (validHolders.Count == 0) return false;

        int chosenIndex = await ChooseCardRouted(purpose, context, cardNames, canSkip);

        if (chosenIndex == -1 && canSkip)
        {
            Log.Info($"[AutoPlay] {context}: skipping");
            return false;
        }

        chosenIndex = Math.Clamp(chosenIndex, 0, validHolders.Count - 1);
        Log.Info($"[AutoPlay] {context}: selecting '{cardNames[chosenIndex]}'");
        validHolders[chosenIndex].EmitSignal(NCardHolder.SignalName.Pressed, validHolders[chosenIndex]);
        return true;
    }

    /// <summary>
    /// Route card selection: during combat → combat agent, outside → advisor.
    /// </summary>
    private async Task<int> ChooseCardRouted(CardSelectionPurpose purpose, string context,
        List<string> cardNames, bool canSkip)
    {
        bool inCombat = CombatManager.Instance.IsInProgress;
        if (inCombat && CombatAgent != null)
        {
            var cardsDesc = string.Join("\n", cardNames.Select((c, i) => $"  [{i}] {c}"));
            var purposeDesc = purpose switch
            {
                CardSelectionPurpose.UpgradeInHand => "Choose a card in your hand to UPGRADE.",
                CardSelectionPurpose.Remove => "Choose a card to REMOVE.",
                _ => context,
            };
            var prompt = $"[MID-COMBAT CARD SELECTION]\n{purposeDesc}\n\nOptions:\n{cardsDesc}";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await CombatAgent.RunAgentLoop(
                prompt, Agent.ToolDefinitions.CardSelectionTools, Agent.Prompts.CombatSystem, cts.Token);

            if (result.HasValue)
            {
                if (result.Value.toolName == "skip_card_reward") return -1;
                if (result.Value.input.TryGetProperty("card_index", out var idx))
                {
                    var choice = idx.GetInt32();
                    if (choice >= 0 && choice < cardNames.Count) return choice;
                }
            }
            return 0;
        }

        try
        {
            var selCtx = new CardSelectionContext { Purpose = purpose, CanSkip = canSkip, Description = context };
            var summary = CollectGameSummary();
            return await Advisor.ChooseCard(selCtx, cardNames, summary);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<bool> TryClickConfirmButton(Node root, CancellationToken ct)
    {
        foreach (var btn in UiHelper.FindAll<NConfirmButton>(root))
        {
            if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
            {
                Log.Info($"[AutoPlay] Clicking confirm");
                await UiHelper.Click(btn, 100);
                await Task.Delay(ActionDelayMs, ct);
                return true;
            }
        }
        return false;
    }

    private async Task WaitAndClickConfirm(Node root, string buttonPath, CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(300, ct);
            var btn = root.GetNodeOrNull<NConfirmButton>(buttonPath);
            if (btn != null && GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
            {
                await UiHelper.Click(btn, 100);
                await Task.Delay(ActionDelayMs, ct);
                return;
            }
        }
    }

    private async Task ClickProceed(Node root, CancellationToken ct)
    {
        foreach (var btn in UiHelper.FindAll<NProceedButton>(root))
        {
            if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
            {
                Log.Info("[AutoPlay] Clicking proceed");
                await UiHelper.Click(btn, 100);
                await Task.Delay(ActionDelayMs, ct);
                return;
            }
        }
        // Fallback: any enabled button
        foreach (var btn in UiHelper.FindAll<NButton>(root))
        {
            if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
            {
                await UiHelper.Click(btn, 100);
                await Task.Delay(ActionDelayMs, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Wait for proceed button or new interactive elements.
    /// </summary>
    private async Task WaitForProceed(Node room, string context, CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(ActionDelayMs, ct);

            // Overlay appeared — defer
            if (NOverlayStack.Instance?.ScreenCount > 0) return;

            // Proceed button
            foreach (var btn in UiHelper.FindAll<NProceedButton>(room))
            {
                if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
                {
                    Log.Info($"[AutoPlay] {context}: clicking proceed");
                    await UiHelper.Click(btn, 100);
                    await Task.Delay(ActionDelayMs, ct);
                    return;
                }
            }
        }
    }

    private GameSummary CollectGameSummary()
    {
        var summary = new GameSummary();
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState?.Players.FirstOrDefault();
            if (player == null) return summary;

            summary.Hp = player.Creature.CurrentHp;
            summary.MaxHp = player.Creature.MaxHp;
            summary.Gold = player.Gold;
            summary.Floor = runState!.TotalFloor;
            summary.Act = runState.CurrentActIndex + 1;
            summary.Relics = player.Relics.Select(r => r.Title?.ToString() ?? r.GetType().Name).ToList();
            summary.Potions = player.Potions.Select(p => p.Title?.ToString() ?? p.GetType().Name).ToList();

            var pcs = player.PlayerCombatState;
            if (pcs != null)
            {
                summary.DeckCards = pcs.AllCards.Select(c =>
                {
                    var name = c.Title?.ToString() ?? c.GetType().Name;
                    var up = c.IsUpgraded ? "+" : "";
                    var cost = c.EnergyCost?.GetAmountToSpend() ?? 0;
                    return $"{name}{up} ({cost} {c.Type})";
                }).ToList();
            }
        }
        catch { }
        return summary;
    }

    private static string FormatCard(CardModel? card)
    {
        if (card == null) return "Unknown";
        var name = card.Title?.ToString() ?? card.GetType().Name;
        var upgraded = card.IsUpgraded ? "+" : "";
        var cost = card.EnergyCost?.GetAmountToSpend() ?? 0;
        var type = card.Type.ToString();
        var rarity = card.Rarity.ToString();
        string desc = "";
        try { desc = card.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.None) ?? ""; }
        catch { }
        return $"{name}{upgraded} ({cost} {type}, {rarity}) — {desc}";
    }

    #endregion
}
