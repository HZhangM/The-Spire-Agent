using AutoPlayMod.Agent;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPlayMod.Core;

/// <summary>
/// Handles non-combat game flow: map navigation, rewards, events, shop, rest sites.
/// Runs as a polling loop when auto-play is enabled.
/// </summary>
public class GameFlowController
{
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    private const int PollIntervalMs = 200;
    private const int ActionDelayMs = 500;

    public bool IsRunning => _isRunning;
    public INonCombatAdvisor Advisor { get; set; } = new DefaultAdvisor();

    /// <summary>
    /// Reference to the unified game agent. Used to route mid-combat overlay decisions
    /// (e.g. Headbutt card select) through the combat conversation thread instead of
    /// the non-combat advisor, which would corrupt the combat history.
    /// </summary>
    public Agent.UnifiedGameAgent? CombatAgent { get; set; }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
        Log.Info("[AutoPlay/Flow] Game flow controller started");
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
        _cts = null;
        Log.Info("[AutoPlay/Flow] Game flow controller stopped");
    }

    /// <summary>Track combat state to detect combat end (events are unreliable).</summary>
    private bool _wasCombatActive;

    /// <summary>
    /// Callback for when combat ends, detected by polling.
    /// Set by AutoPlayer to trigger reflection + non-combat session.
    /// </summary>
    public Func<bool, Task>? OnCombatEndDetected { get; set; }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, ct);

                // Detect combat end by polling IsInProgress (events are unreliable)
                bool combatActive = CombatManager.Instance.IsInProgress;
                if (_wasCombatActive && !combatActive)
                {
                    Log.Info("[AutoPlay/Flow] Combat end detected (IsInProgress → false)");
                    _wasCombatActive = false;
                    try
                    {
                        if (OnCombatEndDetected != null)
                            await OnCombatEndDetected(true); // assume victory for now
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[AutoPlay/Flow] OnCombatEndDetected error: {ex.Message}");
                    }
                }
                _wasCombatActive = combatActive;

                // Check for hand card selection mode (Armaments upgrade, discard, etc.)
                // In v0.99.1+ this is NOT an overlay - it's a mode on NPlayerHand itself.
                if (await TryHandleHandSelection(ct))
                    continue;

                // Check for overlay screens (rewards, card selection, events, etc.)
                if (await TryHandleOverlayScreen(ct))
                    continue;

                // If we get here, no overlay is blocking — reset proceed trigger
                _proceedTriggered = false;

                // Check map screen LAST — only navigate when nothing else is happening.
                // The player (or AI) may open the map during combat/events just to look.
                if (await TryHandleMapScreen(ct))
                    continue;

                // Skip room-level handling if combat is active
                if (combatActive)
                    continue;

                // Check for room-level interactions
                await TryHandleRoom(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"[AutoPlay/Flow] Error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Handle NPlayerHand in card selection mode (Armaments upgrade, Gambler's Brew discard, etc.).
    /// In v0.99.1+ this replaced the old NSimpleCardSelectScreen overlay.
    /// </summary>
    private int _handSelectLogThrottle;

    private async Task<bool> TryHandleHandSelection(CancellationToken ct)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection) return false;

        if (++_handSelectLogThrottle % 5 == 1)
        {
            Log.Info($"[AutoPlay/Flow] Hand selection mode: {hand.CurrentMode}");
        }

        // Determine purpose from mode
        var purpose = hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect
            ? CardSelectionPurpose.UpgradeInHand
            : CardSelectionPurpose.Other;

        // Find selectable card holders in the hand
        var validHolders = new List<NHandCardHolder>();
        var cardNames = new List<string>();

        foreach (var holder in hand.ActiveHolders)
        {
            if (!GodotObject.IsInstanceValid(holder)) continue;
            if (!holder.Visible) continue; // Hidden cards are filtered out
            if (holder.CardNode == null) continue;

            validHolders.Add(holder);
            cardNames.Add(FormatCardForAdvisor(holder.CardNode.Model));
        }

        if (validHolders.Count == 0) return false;

        // Ask advisor which card to pick.
        // During combat, route through combat agent to stay in context.
        int chosenIndex = 0;
        try
        {
            var selCtx = new CardSelectionContext
            {
                Purpose = purpose,
                CanSkip = false,
                Description = $"Hand selection ({hand.CurrentMode})",
            };
            var summary = CollectGameSummary();
            chosenIndex = await ChooseCardWithRouting(selCtx, cardNames, summary);
            if (chosenIndex < 0 || chosenIndex >= validHolders.Count)
                chosenIndex = 0;
        }
        catch
        {
            chosenIndex = 0;
        }

        var chosen = validHolders[chosenIndex];
        Log.Info($"[AutoPlay/Flow] Hand select ({purpose}): choosing '{cardNames[chosenIndex]}' (index {chosenIndex}/{validHolders.Count})");

        // Click the card holder to select it
        chosen.EmitSignal(NCardHolder.SignalName.Pressed, chosen);
        await Task.Delay(300, ct);

        // Now find and click the confirm button on the hand
        var confirmBtn = hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
        if (confirmBtn != null && GodotObject.IsInstanceValid(confirmBtn) && confirmBtn.IsEnabled)
        {
            Log.Info("[AutoPlay/Flow] Hand select: confirming");
            await UiHelper.Click(confirmBtn, 100);
            await Task.Delay(ActionDelayMs, ct);
        }
        else
        {
            // Wait for confirm to become enabled
            for (int retry = 0; retry < 10; retry++)
            {
                await Task.Delay(300, ct);
                confirmBtn = hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
                if (confirmBtn != null && GodotObject.IsInstanceValid(confirmBtn) && confirmBtn.IsEnabled)
                {
                    Log.Info("[AutoPlay/Flow] Hand select: confirming (delayed)");
                    await UiHelper.Click(confirmBtn, 100);
                    await Task.Delay(ActionDelayMs, ct);
                    break;
                }
            }
        }

        return true;
    }

    private int _logThrottle;

    private async Task<bool> TryHandleOverlayScreen(CancellationToken ct)
    {
        var stack = NOverlayStack.Instance;
        if (stack == null || stack.ScreenCount == 0) return false;

        var screen = stack.Peek();
        if (screen == null) return false;

        // Log what screen we're seeing (throttled to avoid spam)
        if (++_logThrottle % 10 == 1)
        {
            Log.Info($"[AutoPlay/Flow] Overlay detected: {screen.GetType().Name} (count={stack.ScreenCount})");
        }

        switch (screen)
        {
            case NRewardsScreen rewardsScreen:
                await HandleRewardsScreen(rewardsScreen, ct);
                return true;

            case NCardRewardSelectionScreen cardScreen:
                await HandleCardRewardScreen(cardScreen, ct);
                return true;

            // All deck-based card selection screens (remove, upgrade, transform, etc.)
            case NCardGridSelectionScreen gridScreen:
                await HandleCardGridSelectionScreen(gridScreen, ct);
                return true;

            default:
                // Generic: try card selection first, then proceed/skip
                await HandleGenericScreen(screen as Node, ct);
                return true;
        }
    }

    private async Task TryHandleRoom(CancellationToken ct)
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null) return;

        var room = runState.CurrentRoom;
        if (room == null) return;

        switch (room.RoomType)
        {
            case RoomType.Event:
                await HandleEventRoom(ct);
                break;
            case RoomType.Shop:
                await HandleShopRoom(ct);
                break;
            case RoomType.RestSite:
                await HandleRestSiteRoom(ct);
                break;
            case RoomType.Treasure:
                await HandleTreasureRoom(ct);
                break;
        }
    }

    #region Screen Handlers

    /// <summary>
    /// Check if map screen is open and handle it.
    /// NMapScreen is NOT an overlay screen - it uses a static singleton.
    /// </summary>
    private int _mapLogThrottle;

    private async Task<bool> TryHandleMapScreen(CancellationToken ct)
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null) return false;

        bool isOpen = mapScreen.IsOpen;
        bool isTravelEnabled = mapScreen.IsTravelEnabled;
        bool isTraveling = mapScreen.IsTraveling;

        // Log map state periodically for debugging
        if (++_mapLogThrottle % 25 == 1)
        {
            Log.Info($"[AutoPlay/Flow] Map check: IsOpen={isOpen}, IsTravelEnabled={isTravelEnabled}, IsTraveling={isTraveling}");
        }

        if (!isOpen) return false;
        if (isTraveling) return false; // Already traveling, wait for it to finish

        // Collect all NMapPoints and check their states
        var allPoints = UiHelper.FindAll<NMapPoint>(mapScreen);
        var travelablePoints = new List<NMapPoint>();
        foreach (var point in allPoints)
        {
            if (point.State == MapPointState.Travelable)
            {
                travelablePoints.Add(point);
            }
        }

        if (travelablePoints.Count == 0)
        {
            if (_mapLogThrottle % 25 == 1)
            {
                Log.Info($"[AutoPlay/Flow] Map: no travelable points (total points={allPoints.Count})");
            }
            return false;
        }

        Log.Info($"[AutoPlay/Flow] Map: {travelablePoints.Count} travelable points, IsTravelEnabled={isTravelEnabled}");

        int chosenIndex = 0;

        if (travelablePoints.Count == 1)
        {
            Log.Info("[AutoPlay/Flow] Map: only 1 node, skipping advisor");
        }
        else
        {
            // Multiple choices — ask advisor
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
                if (chosenIndex < 0 || chosenIndex >= travelablePoints.Count)
                    chosenIndex = 0;
            }
            catch (Exception ex)
            {
                Log.Warn($"[AutoPlay/Flow] Map advisor failed: {ex.Message}, picking first node");
            }
        }

        var target = travelablePoints[chosenIndex];
        Log.Info($"[AutoPlay/Flow] Map: selecting [{target.Point.coord.col},{target.Point.coord.row}] type={target.Point.PointType} (choice {chosenIndex}/{travelablePoints.Count})");

        try
        {
            // Simulate a player click on the map point using ForceClick.
            // ForceClick() calls OnRelease() → checks IsTravelable → OnMapPointSelectedLocally()
            // which goes through the proper game action queue (Vote → Move → Travel).
            if (isTravelEnabled)
            {
                Log.Info("[AutoPlay/Flow] Map: using ForceClick on NMapPoint");
                target.ForceClick();
            }
            else
            {
                // IsTravelEnabled is false — ForceClick would be blocked by IsTravelable check.
                // Bypass by calling OnMapPointSelectedLocally directly.
                Log.Info("[AutoPlay/Flow] Map: IsTravelEnabled=false, calling OnMapPointSelectedLocally directly");
                mapScreen.OnMapPointSelectedLocally(target);
            }
            await Task.Delay(ActionDelayMs * 4, ct); // Wait for vote processing + travel animation
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Flow] Map selection failed: {ex.Message}\n{ex.StackTrace}");
        }

        return true;
    }

    private async Task HandleRewardsScreen(NRewardsScreen rewardsScreen, CancellationToken ct)
    {
        // Click ONE reward button at a time.
        // Clicking a reward (e.g. card reward) may open a sub-screen overlay.
        // The proceed button is disabled while overlays are on top.
        // So we must: click one reward → return → let overlay handler deal with sub-screen
        // → next poll handles the next reward → when all collected, click proceed.

        var screenNode = (Node)rewardsScreen;

        // Find uncollected reward buttons (NRewardButton instances in the rewards container)
        var rewardButtons = UiHelper.FindAll<NRewardButton>(screenNode);
        // Also check for linked reward sets (e.g. enchantment choices)
        var linkedRewards = UiHelper.FindAll<NLinkedRewardSet>(screenNode);

        if (rewardButtons.Count > 0)
        {
            // Click just the first available reward button
            var firstReward = rewardButtons[0];
            if (GodotObject.IsInstanceValid(firstReward))
            {
                Log.Info($"[AutoPlay/Flow] Rewards: clicking reward button ({rewardButtons.Count} remaining)");
                await UiHelper.Click(firstReward, 100);
                await Task.Delay(ActionDelayMs, ct);
            }
            return; // Let next poll handle any overlay or the next reward
        }

        if (linkedRewards.Count > 0)
        {
            var firstLinked = linkedRewards[0];
            if (GodotObject.IsInstanceValid(firstLinked))
            {
                // Click the first clickable child in the linked reward set
                var clickables = UiHelper.FindAll<NClickableControl>(firstLinked);
                foreach (var clickable in clickables)
                {
                    if (!GodotObject.IsInstanceValid(clickable)) continue;
                    if (clickable is NButton nb && !nb.IsEnabled) continue;
                    Log.Info($"[AutoPlay/Flow] Rewards: clicking linked reward");
                    await UiHelper.Click(clickable, 100);
                    await Task.Delay(ActionDelayMs, ct);
                    return;
                }
            }
        }

        // No more reward buttons — need to dismiss the rewards screen.
        // ForceClick/CallDeferred approaches failed to trigger the proceed handler.
        // Directly remove the overlay from the stack, which triggers AfterOverlayClosed()
        // (proper cleanup + _closedCompletionSource.SetResult()), then call
        // ProceedFromTerminalRewardsScreen on the main thread to handle room transition.
        if (!_proceedTriggered)
        {
            _proceedTriggered = true;
            Log.Info("[AutoPlay/Flow] Rewards: removing overlay + calling ProceedFromTerminalRewardsScreen");

            // Remove overlay on main thread — triggers AfterOverlayClosed cleanup
            screenNode.CallDeferred("queue_free");
            NOverlayStack.Instance.Remove(rewardsScreen);

            // Trigger room transition on main thread
            TaskHelper.RunSafely(RunManager.Instance.ProceedFromTerminalRewardsScreen());
            await Task.Delay(ActionDelayMs * 4, ct);
            return;
        }

        // Already triggered, wait for transition
        await Task.Delay(ActionDelayMs, ct);
    }

    private bool _proceedTriggered;

    private async Task HandleCardRewardScreen(NCardRewardSelectionScreen cardScreen, CancellationToken ct)
    {
        var screenNode = (Node)cardScreen;

        bool selected = await TrySelectCardHolder(screenNode, "Card reward",
            CardSelectionPurpose.Reward, canSkip: true);
        if (!selected)
        {
            Log.Info("[AutoPlay/Flow] Card reward: skipping");
            await TryClickProceedButton(screenNode, ct);
        }

        await Task.Delay(ActionDelayMs, ct);
    }

    /// <summary>
    /// Handle any NCardGridSelectionScreen (deck card select, upgrade, transform, etc.)
    /// These all share the pattern: click a card holder, then click confirm.
    /// </summary>
    private async Task HandleCardGridSelectionScreen(NCardGridSelectionScreen gridScreen, CancellationToken ct)
    {
        var screenNode = (Node)gridScreen;
        var screenName = gridScreen.GetType().Name;

        // Detect purpose from screen type
        var purpose = gridScreen switch
        {
            NDeckUpgradeSelectScreen => CardSelectionPurpose.UpgradeFromDeck,
            NDeckTransformSelectScreen => CardSelectionPurpose.Transform,
            NDeckCardSelectScreen => CardSelectionPurpose.Remove,
            NSimpleCardSelectScreen => CardSelectionPurpose.UpgradeInHand, // mid-combat (Armaments etc.)
            _ => CardSelectionPurpose.Other,
        };

        // Step 1: Check if a confirm button is already enabled (card already selected from a previous poll)
        if (await TryClickConfirm(screenNode, screenName, ct))
            return;

        // Step 2: Select a card from the internal _grid ONLY.
        // Use reflection to access the protected _grid field, which contains
        // only the selectable cards (not display-only cards like the Armaments preview).
        Node searchRoot = screenNode;
        try
        {
            var gridField = typeof(NCardGridSelectionScreen).GetField("_grid",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (gridField?.GetValue(gridScreen) is Node grid)
            {
                searchRoot = grid;
                Log.Info($"[AutoPlay/Flow] {screenName}: using _grid (children: {grid.GetChildCount(false)})");
            }
        }
        catch { }

        bool clicked = await TrySelectCardHolder(searchRoot, screenName, purpose);

        if (!clicked)
        {
            // Nothing to select - try proceed to skip
            await TryClickProceedButton(screenNode, ct);
            return;
        }

        // Step 3: Wait for preview/confirm to appear, then click confirm
        // The confirm button enables after a card is selected + animation plays
        for (int retry = 0; retry < 10; retry++)
        {
            await Task.Delay(300, ct);
            if (await TryClickConfirm(screenNode, screenName, ct))
                return;
        }

        Log.Warn($"[AutoPlay/Flow] {screenName}: confirm button never became enabled");
        await TryClickProceedButton(screenNode, ct);
    }

    /// <summary>
    /// Find card holders and select one. Uses the Advisor to choose which card
    /// when in agent mode (for smart Armaments upgrades, card removal, etc.).
    /// Falls back to first card if advisor is unavailable.
    /// </summary>
    private async Task<bool> TrySelectCardHolder(Node root, string context,
        CardSelectionPurpose purpose = CardSelectionPurpose.Other, bool canSkip = false)
    {
        // Collect all valid card holders
        var validHolders = new List<NCardHolder>();
        var cardNames = new List<string>();

        var holders = UiHelper.FindAll<NCardHolder>(root);
        foreach (var holder in holders)
        {
            if (!GodotObject.IsInstanceValid(holder)) continue;
            if (holder.CardNode == null) continue;

            validHolders.Add(holder);
            cardNames.Add(FormatCardForAdvisor(holder.CardModel));
        }

        if (validHolders.Count == 0)
        {
            // Fallback: search inside NCardGrid
            var grids = UiHelper.FindAll<NCardGrid>(root);
            foreach (var grid in grids)
            {
                if (!GodotObject.IsInstanceValid(grid)) continue;
                var gridHolders = UiHelper.FindAll<NCardHolder>(grid);
                foreach (var h in gridHolders)
                {
                    if (!GodotObject.IsInstanceValid(h)) continue;
                    if (h.CardNode == null) continue;
                    validHolders.Add(h);
                    cardNames.Add(FormatCardForAdvisor(h.CardModel));
                }
            }
        }

        if (validHolders.Count == 0) return false;

        // Ask advisor which card to pick, with context about WHY we're choosing.
        // During combat, route through the combat agent's conversation thread
        // so mid-combat card selections (Headbutt, etc.) stay in context.
        int chosenIndex = 0;
        try
        {
            var selCtx = new CardSelectionContext
            {
                Purpose = purpose,
                CanSkip = canSkip,
                Description = context,
            };
            var summary = CollectGameSummary();
            chosenIndex = await ChooseCardWithRouting(selCtx, cardNames, summary);

            // -1 means skip (only valid when canSkip is true)
            if (chosenIndex == -1 && canSkip)
            {
                Log.Info($"[AutoPlay/Flow] {context}: advisor chose to skip");
                return false; // Caller should handle skip (click proceed/skip button)
            }

            if (chosenIndex < 0 || chosenIndex >= validHolders.Count)
                chosenIndex = 0;
        }
        catch
        {
            chosenIndex = 0;
        }

        var chosen = validHolders[chosenIndex];
        Log.Info($"[AutoPlay/Flow] {context} ({purpose}): selecting '{cardNames[chosenIndex]}' (index {chosenIndex}/{validHolders.Count})");
        chosen.EmitSignal(NCardHolder.SignalName.Pressed, chosen);
        return true;
    }

    /// <summary>
    /// Try to find and click an enabled NConfirmButton anywhere in the node tree.
    /// Returns true if a button was found and clicked.
    /// </summary>
    private async Task<bool> TryClickConfirm(Node root, string context, CancellationToken ct)
    {
        // Search entire scene tree from root - confirm buttons may be in nested preview containers
        var confirmButtons = UiHelper.FindAll<NConfirmButton>(root);
        foreach (var btn in confirmButtons)
        {
            if (!GodotObject.IsInstanceValid(btn)) continue;
            if (!btn.IsEnabled) continue;

            Log.Info($"[AutoPlay/Flow] {context}: confirming");
            await UiHelper.Click(btn, 100);
            await Task.Delay(ActionDelayMs, ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Generic handler for unknown screens.
    /// Tries: card selection → confirm → proceed → any button.
    /// </summary>
    private async Task HandleGenericScreen(Node? screenNode, CancellationToken ct)
    {
        if (screenNode == null) return;

        var screenName = screenNode.GetType().Name;

        // First: check if there's already a confirm button enabled
        if (await TryClickConfirm(screenNode, $"Generic {screenName}", ct))
            return;

        // Check if this screen has card holders
        bool hasCards = await TrySelectCardHolder(screenNode, $"Generic {screenName}", CardSelectionPurpose.Other);
        if (hasCards)
        {
            // Wait for confirm to become enabled
            for (int retry = 0; retry < 10; retry++)
            {
                await Task.Delay(300, ct);
                if (await TryClickConfirm(screenNode, $"Generic {screenName}", ct))
                    return;
            }
        }

        // Try proceed/skip
        await TryClickProceedButton(screenNode, ct);
    }

    #endregion

    #region Room Handlers

    private async Task HandleEventRoom(CancellationToken ct)
    {
        var game = NGame.Instance;
        if (game == null) return;

        var eventRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
        if (eventRoom == null) return;

        var optionButtons = UiHelper.FindAll<NEventOptionButton>(eventRoom);
        if (optionButtons.Count == 0) return;

        // Collect enabled options with their text
        var validButtons = new List<NEventOptionButton>();
        var optionTexts = new List<string>();
        foreach (var button in optionButtons)
        {
            if (!GodotObject.IsInstanceValid(button)) continue;
            if (!button.IsEnabled) continue;
            validButtons.Add(button);

            // Extract option text from the button's label
            string text = "Option";
            try
            {
                var label = ((Node)button).GetNodeOrNull<Godot.RichTextLabel>("%Text");
                if (label != null) text = label.Text ?? "Option";
            }
            catch { }
            optionTexts.Add(text);
        }

        if (validButtons.Count == 0) return;

        // Try to get event description for context
        string eventDesc = "Event";
        try
        {
            var layout = ((Node)eventRoom).GetNodeOrNull("EventLayout");
            if (layout != null)
            {
                var descLabel = layout.GetNodeOrNull<Godot.RichTextLabel>("%EventDescription");
                if (descLabel != null) eventDesc = descLabel.Text ?? "Event";
            }
        }
        catch { }

        // Ask advisor which option to pick
        int chosenIndex = 0;
        try
        {
            var summary = CollectGameSummary();
            chosenIndex = await Advisor.ChooseEventOption(eventDesc, optionTexts, summary);
            if (chosenIndex < 0 || chosenIndex >= validButtons.Count)
                chosenIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Flow] Event advisor failed: {ex.Message}, picking first option");
        }

        Log.Info($"[AutoPlay/Flow] Event: choosing option {chosenIndex}/{validButtons.Count}: {optionTexts[chosenIndex]}");
        await UiHelper.Click(validButtons[chosenIndex], 100);
        await Task.Delay(ActionDelayMs * 3, ct);

        // After choosing, event may show new text/options or a proceed button.
        // Poll for proceed or new option buttons.
        await WaitForProceedOrNewOptions<NEventOptionButton>(eventRoom, "Event", ct);
    }

    private async Task HandleShopRoom(CancellationToken ct)
    {
        // TODO: implement shop agent (evaluate cards/relics/potions to buy, card removal)
        // For now: wait a moment then leave
        var game = NGame.Instance;
        if (game == null) return;

        var shopRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/MerchantRoom");
        if (shopRoom == null) return;

        Log.Info("[AutoPlay/Flow] Shop: no purchase logic yet, leaving");
        await Task.Delay(ActionDelayMs * 2, ct);

        // Find leave/proceed button
        var proceedButtons = UiHelper.FindAll<NProceedButton>(shopRoom);
        foreach (var btn in proceedButtons)
        {
            if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
            {
                await UiHelper.Click(btn, 100);
                await Task.Delay(ActionDelayMs, ct);
                return;
            }
        }

        await Task.Delay(ActionDelayMs, ct);
    }

    private async Task HandleRestSiteRoom(CancellationToken ct)
    {
        var game = NGame.Instance;
        if (game == null) return;

        var restRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/RestSiteRoom");
        if (restRoom == null) return;

        // Find NRestSiteButton specifically (not generic clickables)
        var restButtons = UiHelper.FindAll<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton>(restRoom);
        var validButtons = new List<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton>();
        var optionTexts = new List<string>();

        foreach (var btn in restButtons)
        {
            if (!GodotObject.IsInstanceValid(btn)) continue;
            if (!btn.IsEnabled) continue;
            validButtons.Add(btn);

            string text = "Option";
            try
            {
                var label = ((Node)btn).GetNodeOrNull<Godot.Label>("%Label");
                if (label != null)
                    text = label.Text ?? "Option";
            }
            catch { }
            optionTexts.Add(text);
        }

        if (validButtons.Count == 0) return;

        // Ask advisor which rest option to pick
        int chosenIndex = 0;
        try
        {
            var summary = CollectGameSummary();
            chosenIndex = await Advisor.ChooseRestSiteOption(optionTexts, summary);
            if (chosenIndex < 0 || chosenIndex >= validButtons.Count)
                chosenIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Flow] Rest site advisor failed: {ex.Message}, picking first option");
        }

        Log.Info($"[AutoPlay/Flow] Rest site: choosing option {chosenIndex}/{validButtons.Count}: {optionTexts[chosenIndex]}");
        await UiHelper.Click(validButtons[chosenIndex], 100);
        await Task.Delay(ActionDelayMs * 3, ct);

        // Wait for proceed or overlay (e.g. upgrade card selection)
        await WaitForProceedOrNewOptions<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton>(restRoom, "Rest site", ct);
    }

    private async Task HandleTreasureRoom(CancellationToken ct)
    {
        // Click the chest, then proceed
        var game = NGame.Instance;
        if (game == null) return;

        var treasureRoom = game.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/TreasureRoom");
        if (treasureRoom == null) return;

        var clickables = UiHelper.FindAll<NClickableControl>(treasureRoom);
        foreach (var btn in clickables)
        {
            if (!GodotObject.IsInstanceValid(btn)) continue;
            if (btn is NProceedButton) continue; // Don't click proceed yet, open chest first
            if (btn is NButton nb && !nb.IsEnabled) continue;

            Log.Info("[AutoPlay/Flow] Treasure: clicking chest");
            await UiHelper.Click(btn, 100);
            await Task.Delay(ActionDelayMs * 3, ct);
            break;
        }

        // Wait for proceed button after opening chest
        await WaitForProceedOrNewOptions<NClickableControl>(treasureRoom, "Treasure", ct);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// After clicking an option in a room (event/rest/treasure), poll for either:
    /// 1. A proceed button to appear → click it
    /// 2. New option buttons to appear → return so next poll handles them
    /// 3. An overlay to appear → return so overlay handler takes over
    /// </summary>
    private async Task WaitForProceedOrNewOptions<TButton>(Node room, string context, CancellationToken ct) where TButton : Node
    {
        for (int retry = 0; retry < 20; retry++)
        {
            await Task.Delay(ActionDelayMs, ct);

            // If an overlay appeared (rewards, card selection, etc.), let overlay handler deal with it
            if (NOverlayStack.Instance != null && NOverlayStack.Instance.ScreenCount > 0)
            {
                Log.Info($"[AutoPlay/Flow] {context}: overlay appeared, deferring");
                return;
            }

            // Check for proceed button
            var proceedButtons = UiHelper.FindAll<NProceedButton>(room);
            foreach (var btn in proceedButtons)
            {
                if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
                {
                    Log.Info($"[AutoPlay/Flow] {context}: clicking proceed");
                    await UiHelper.Click(btn, 100);
                    await Task.Delay(ActionDelayMs * 2, ct);
                    return;
                }
            }

            // Check if new action buttons appeared (e.g. event multi-step)
            var newButtons = UiHelper.FindAll<TButton>(room);
            var enabledCount = newButtons.Count(b => GodotObject.IsInstanceValid(b) && (b is not NButton nb || nb.IsEnabled));
            if (enabledCount > 0)
            {
                Log.Info($"[AutoPlay/Flow] {context}: new options appeared, deferring to next poll");
                return;
            }
        }
        Log.Warn($"[AutoPlay/Flow] {context}: no proceed button found after waiting");
    }

    /// <summary>
    /// Route card selection to the right agent based on whether combat is active.
    /// During combat: use the combat agent's conversation thread (so mid-combat
    /// card selections like Headbutt stay in the combat context).
    /// Outside combat: use the non-combat advisor as normal.
    /// </summary>
    private async Task<int> ChooseCardWithRouting(CardSelectionContext context, List<string> cardNames, GameSummary summary)
    {
        bool inCombat = CombatManager.Instance.IsInProgress;

        if (inCombat && CombatAgent != null)
        {
            // Build a prompt for the combat agent
            var cardsDesc = string.Join("\n", cardNames.Select((c, i) => $"  [{i}] {c}"));
            var purposeDesc = context.Purpose switch
            {
                CardSelectionPurpose.UpgradeInHand => "Choose a card in your hand to UPGRADE.",
                CardSelectionPurpose.Remove => "Choose a card to REMOVE from your deck.",
                _ => context.Description.Length > 0 ? context.Description : "Choose a card.",
            };
            var prompt = $"[MID-COMBAT CARD SELECTION]\n{purposeDesc}\n\nOptions:\n{cardsDesc}";

            Log.Info("[AutoPlay/Flow] Routing mid-combat card selection through combat agent");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await CombatAgent.RunAgentLoop(
                prompt, Agent.ToolDefinitions.CardSelectionTools, Agent.Prompts.CombatSystem, cts.Token);

            if (result.HasValue)
            {
                var (toolName, input) = result.Value;
                if (toolName == "skip_card_reward") return -1;
                if (input.TryGetProperty("card_index", out var idx))
                {
                    var choice = idx.GetInt32();
                    if (choice >= 0 && choice < cardNames.Count) return choice;
                }
            }
            return 0;
        }

        // Outside combat: use normal advisor
        return await Advisor.ChooseCard(context, cardNames, summary);
    }

    private async Task TryClickProceedButton(Node? screen, CancellationToken ct)
    {
        if (screen == null) return;

        var proceedButtons = UiHelper.FindAll<NProceedButton>(screen);
        foreach (var btn in proceedButtons)
        {
            if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
            {
                Log.Info($"[AutoPlay/Flow] Clicking proceed on {screen.GetType().Name}");
                await UiHelper.Click(btn, 100);
                await Task.Delay(ActionDelayMs, ct);
                return;
            }
        }

        // Fallback: try any enabled button
        var anyButtons = UiHelper.FindAll<NButton>(screen);
        foreach (var btn in anyButtons)
        {
            if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
            {
                Log.Info($"[AutoPlay/Flow] Clicking button on {screen.GetType().Name}");
                await UiHelper.Click(btn, 100);
                await Task.Delay(ActionDelayMs, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Collect a GameSummary for the advisor from current RunState.
    /// </summary>
    private GameSummary CollectGameSummary()
    {
        var summary = new GameSummary();
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return summary;

            var player = runState.Players.FirstOrDefault();
            if (player == null) return summary;

            summary.Hp = player.Creature.CurrentHp;
            summary.MaxHp = player.Creature.MaxHp;
            summary.Gold = player.Gold;
            summary.Floor = runState.TotalFloor;
            summary.Act = runState.CurrentActIndex + 1;
            summary.Relics = player.Relics.Select(r =>
            {
                var name = JsonUtils.SafeLocText(r.Title, r.GetType().Name);
                string desc = "";
                try { desc = JsonUtils.SafeLocText(r.Description); }
                catch { }
                return string.IsNullOrEmpty(desc) ? name : $"{name} ({desc})";
            }).ToList();
            summary.Potions = player.Potions.Select(p =>
            {
                var name = JsonUtils.SafeLocText(p.Title, p.GetType().Name);
                string desc = "";
                try { desc = JsonUtils.SafeLocText(p.Description); }
                catch { }
                return string.IsNullOrEmpty(desc) ? name : $"{name} ({desc})";
            }).ToList();
            summary.PotionSlots = player.Potions.Count();
            summary.PotionSlotsMax = player.MaxPotionCount;

            // Get deck cards
            var pcs = player.PlayerCombatState;
            if (pcs != null)
            {
                summary.DeckCards = pcs.AllCards.Select(c =>
                {
                    var name = c.Title ?? c.GetType().Name;
                    var up = c.IsUpgraded ? "+" : "";
                    var cost = c.EnergyCost?.GetAmountToSpend() ?? 0;
                    return $"{name}{up} ({cost}⚡ {c.Type})";
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay/Flow] Failed to collect game summary: {ex.Message}");
        }
        return summary;
    }

    #endregion

    /// <summary>
    /// Format a card model into a rich description for the advisor.
    /// </summary>
    private static string FormatCardForAdvisor(CardModel? card)
    {
        if (card == null) return "Unknown card";

        var name = card.Title ?? card.GetType().Name;
        var upgraded = card.IsUpgraded ? "+" : "";
        var cost = card.EnergyCost?.GetAmountToSpend() ?? 0;
        var type = card.Type.ToString();
        var rarity = card.Rarity.ToString();

        string desc = "";
        try { desc = card.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.None) ?? ""; }
        catch { }

        return $"{name}{upgraded} ({cost}⚡ {type}, {rarity}) — {desc}";
    }
}
