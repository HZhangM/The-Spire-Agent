using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Core;

/// <summary>
/// Main auto-play controller. Runs a single unified loop that detects the
/// current game phase and dispatches to the appropriate handler.
///
/// Replaces the old dual-loop architecture (separate combat loop + flow controller).
/// </summary>
public class AutoPlayer
{
    public bool IsEnabled { get; private set; }
    public IPlayStrategy? ActiveStrategy { get; private set; }
    public PhaseHandler Handler { get; } = new();
    public RunContext RunContext { get; } = new();
    public RunContextExtractor? ContextExtractor { get; set; }
    public Memory.BackgroundMemoryWriter? BackgroundWriter { get; set; }
    public AgentStatusOverlay StatusOverlay { get; } = new();

    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _isPlayingTurn;

    private const int PollIntervalMs = 200;

    // Last known battle state — captured during combat for use after combat ends
    // (BattleStateCollector returns null once IsInProgress becomes false)
    private BattleState? _lastBattleState;

    // Phase transition detection — only act on changes
    private GamePhase _lastPhase = GamePhase.Idle;
    private bool _lastPhaseHandled;

    // Combat end detection
    private bool _wasCombatActive;
    private bool _combatEndHandled;

    public void SetStrategy(IPlayStrategy strategy)
    {
        ActiveStrategy = strategy;
        Handler.CombatStrategy = strategy;
        Log.Info($"[AutoPlay] Strategy set to: {strategy.Name}");
    }

    public void Toggle()
    {
        IsEnabled = !IsEnabled;
        Log.Info($"[AutoPlay] Auto-play {(IsEnabled ? "ENABLED" : "DISABLED")}");

        if (IsEnabled)
        {
            StatusOverlay.Initialize();
            StatusOverlay.IsActive = ActiveStrategy is Agent.AgentStrategy;
            Start();

            // If toggled on mid-combat during player turn, start playing
            if (CombatManager.Instance.IsInProgress)
            {
                var phase = GamePhaseDetector.Detect();
                if (phase == GamePhase.CombatPlayerTurn)
                    _ = PlayTurnAsync();
            }
        }
        else
        {
            StatusOverlay.IsActive = false;
            Stop();
        }
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
        Log.Info("[AutoPlay] Main loop started");
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
        _cts = null;
        Log.Info("[AutoPlay] Main loop stopped");
    }

    /// <summary>
    /// Called by Harmony patch when player turn starts.
    /// THIS is the combat driver — the game tells us when it's our turn.
    /// </summary>
    public void OnPlayerTurnStart()
    {
        if (!IsEnabled || ActiveStrategy == null) return;
        _combatEndHandled = false;
        _ = PlayTurnAsync();
    }

    /// <summary>
    /// Play the entire player turn. Driven by the game's TurnStarted event.
    /// </summary>
    private async Task PlayTurnAsync()
    {
        if (_isPlayingTurn) return;
        _isPlayingTurn = true;

        try
        {
            // Link to main CTS so Toggle-off cancels in-progress turns
            using var cts = _cts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
                : new CancellationTokenSource();
            var ct = cts.Token;
            int actionsPlayed = 0;

            // Wait for hand cards to be dealt before acting
            for (int wait = 0; wait < 15; wait++)
            {
                var initState = BattleStateCollector.Collect();
                if (initState != null && initState.Hand.Count > 0) break;
                await Task.Delay(200, ct);
            }

            while (!ct.IsCancellationRequested && actionsPlayed < 50)
            {
                // Check phase — if no longer our turn, stop or handle sub-phase
                var phase = GamePhaseDetector.Detect();
                if (phase == GamePhase.CombatHandSelect || phase == GamePhase.CombatOverlaySelect)
                {
                    // Mid-combat card selection — handle it within combat context
                    await Handler.HandlePhase(phase, ct);
                    await Task.Delay(300, ct);
                    continue;
                }
                if (phase != GamePhase.CombatPlayerTurn)
                    break;

                var state = BattleStateCollector.Collect();
                if (state == null) break;
                _lastBattleState = state; // Capture for post-combat reflection

                CombatAction action;
                try
                {
                    action = await ActiveStrategy.DecideAction(state);
                }
                catch (Exception ex)
                {
                    Log.Error($"[AutoPlay] Strategy error: {ex.Message}");
                    ActionExecutor.Execute(CombatAction.EndTurn());
                    break;
                }

                Log.Info($"[AutoPlay] Action #{actionsPlayed}: {action}");
                bool success = ActionExecutor.Execute(action);

                if (action.Type == CombatActionType.EndTurn) break;

                if (!success)
                {
                    if (ActiveStrategy is Agent.AgentStrategy agentStrat)
                        agentStrat.NotifyActionFailed(action);
                }
                else
                {
                    actionsPlayed++;
                }

                await Task.Delay(300, ct);

                // After killing enemies, new ones may spawn (e.g. slimes splitting).
                // If all enemies appear dead but combat is still in progress, wait for spawn.
                if (CombatManager.Instance.IsInProgress)
                {
                    var checkState = BattleStateCollector.Collect();
                    if (checkState != null && checkState.Enemies.All(e => !e.IsAlive))
                    {
                        Log.Info("[AutoPlay] All enemies dead but combat ongoing — waiting for spawns");
                        for (int waitSpawn = 0; waitSpawn < 20; waitSpawn++)
                        {
                            await Task.Delay(300, ct);
                            checkState = BattleStateCollector.Collect();
                            if (checkState == null) break;
                            if (checkState.Enemies.Any(e => e.IsAlive)) break;
                            if (!CombatManager.Instance.IsInProgress) break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoPlay] Turn error: {ex.Message}");
        }
        finally
        {
            _isPlayingTurn = false;
        }
    }

    /// <summary>
    /// Called by Harmony patch when combat ends (backward compat).
    /// </summary>
    public async void OnCombatEnd(bool victory) => await HandleCombatEnd(victory);

    /// <summary>
    /// The main poll loop. Does NOT drive combat (that's event-driven via TurnStarted).
    /// Only handles non-combat phases, and only on phase TRANSITIONS.
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, ct);

                // Detect combat end (game events are unreliable)
                bool combatActive = CombatManager.Instance.IsInProgress;
                if (_wasCombatActive && !combatActive)
                {
                    _wasCombatActive = false;
                    await HandleCombatEnd(true);
                }
                _wasCombatActive = combatActive;

                // Detect current phase
                GamePhase phase;
                try
                {
                    phase = GamePhaseDetector.Detect();
                }
                catch (Exception ex)
                {
                    Log.Error($"[AutoPlay] Phase detection error: {ex.Message}\n{ex.StackTrace}");
                    await Task.Delay(1000, ct);
                    continue;
                }

                // Combat phases are driven by TurnStarted event, not this loop
                if (GamePhaseDetector.IsCombatPhase(phase))
                {
                    _lastPhase = phase;
                    continue;
                }

                if (phase == GamePhase.Idle)
                {
                    _lastPhase = phase;
                    _lastPhaseHandled = false;
                    continue;
                }

                // Phase transition detection
                bool isNewPhase = phase != _lastPhase;
                if (isNewPhase)
                {
                    Log.Info($"[AutoPlay] Phase transition: {_lastPhase} → {phase}");
                    _lastPhaseHandled = false;
                }

                // Phases that need repeated polling:
                // - RewardsScreen: one reward per poll
                // - EventRoom: buttons may not be loaded yet, multi-step events
                // - RestSite: wait for proceed after choosing
                // - TreasureRoom: open chest then proceed
                // - Shop: browse then leave
                // - GenericOverlay: may need multiple clicks
                // One-shot phases (act once, wait for transition):
                // - MapScreen, CardRewardSelect, CardGridSelect
                bool needsRepeat = phase is GamePhase.RewardsScreen
                    or GamePhase.EventRoom or GamePhase.RestSite
                    or GamePhase.TreasureRoom or GamePhase.Shop
                    or GamePhase.GenericOverlay;

                if (!_lastPhaseHandled || needsRepeat)
                {
                    try
                    {
                        bool handled = await Handler.HandlePhase(phase, ct);
                        if (!needsRepeat)
                            _lastPhaseHandled = handled;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[AutoPlay] HandlePhase({phase}) error: {ex.Message}\n{ex.StackTrace}");
                        await Task.Delay(1000, ct);
                    }
                }

                _lastPhase = phase;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"[AutoPlay] Loop error: {ex.Message}");
                _lastPhaseHandled = false;
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task HandleCombatEnd(bool victory)
    {
        if (_combatEndHandled) return;
        _combatEndHandled = true;

        if (ActiveStrategy == null) return;
        Log.Info($"[AutoPlay] Combat ended: victory={victory}");

        // Use last captured state — BattleStateCollector returns null after combat ends
        var finalState = BattleStateCollector.Collect() ?? _lastBattleState;
        _lastBattleState = null;

        if (finalState != null)
        {
            try
            {
                await ActiveStrategy.OnCombatEnd(finalState, victory, finalState.Player.Hp);
            }
            catch (Exception ex)
            {
                Log.Error($"[AutoPlay] OnCombatEnd error: {ex.Message}");
            }
        }

        // Update run context from combat reflection (background)
        if (ContextExtractor != null)
        {
            var reflection = ActiveStrategy is Agent.AgentStrategy agentStrat2
                ? agentStrat2.Journal?.GetLatestReflection() : null;
            ContextExtractor.AfterCombat(reflection, finalState);
        }

        // Start non-combat session for the post-combat flow
        if (Handler.Advisor is Agent.GameAgentAdvisor agentAdvisor)
        {
            var outcome = victory ? "Victory" : "Defeat";
            var hp = finalState != null ? $"{finalState.Player.Hp}/{finalState.Player.MaxHp}" : "?";
            agentAdvisor.StartNonCombatSession($"Combat ended: {outcome}, HP: {hp}");
        }

        // If defeated → run is over, save run memory
        if (!victory)
        {
            SaveRunMemory("Defeated in combat", finalState);
        }
    }

    /// <summary>
    /// Save a run summary to persistent memory.
    /// Called on defeat or victory (act clear / game win).
    /// </summary>
    private void SaveRunMemory(string result, BattleState? finalState)
    {
        if (BackgroundWriter == null) return;

        try
        {
            var runState = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            var floor = runState?.TotalFloor ?? 0;
            var character = runState?.Players.FirstOrDefault()?.Character?.Id?.Entry ?? "ironclad";
            var archetype = RunContext.Archetype;
            if (string.IsNullOrEmpty(archetype)) archetype = "Unknown";

            var causeOfEnd = result;
            if (finalState != null)
            {
                var enemies = string.Join(", ", finalState.Enemies.Where(e => e.IsAlive).Select(e => e.Name));
                causeOfEnd = $"{result} vs {enemies} (HP: {finalState.Player.Hp}/{finalState.Player.MaxHp})";
            }

            BackgroundWriter.ProcessRunEnd(
                character, archetype, result, floor, causeOfEnd,
                RunContext.KeyDecisions);

            Log.Info($"[AutoPlay] Run memory saved: {result} at floor {floor}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AutoPlay] Failed to save run memory: {ex.Message}");
        }
    }
}
