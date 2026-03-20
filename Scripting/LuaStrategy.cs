using AutoPlayMod.Core;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Scripting;

/// <summary>
/// IPlayStrategy backed by a Lua script.
/// Supports hot-reload: call ReloadScript() to swap in new Lua code.
/// </summary>
public class LuaStrategy : IPlayStrategy
{
    public string Name => "LuaScript";

    private readonly LuaEngine _engine;
    private readonly string _scriptPath;

    /// <summary>
    /// Create with a script file path. The file is loaded immediately.
    /// </summary>
    public LuaStrategy(string scriptPath)
    {
        _scriptPath = scriptPath;
        _engine = new LuaEngine();
        _engine.LoadScriptFile(scriptPath);
    }

    /// <summary>
    /// Create with inline Lua source.
    /// </summary>
    public LuaStrategy(string source, bool isInline)
    {
        _scriptPath = "";
        _engine = new LuaEngine();
        _engine.LoadScript(source);
    }

    public Task<CombatAction> DecideAction(BattleState state)
    {
        var action = _engine.DecideAction(state);
        return Task.FromResult(action);
    }

    public Task OnCombatEnd(BattleState finalState, bool victory, int remainingHp)
    {
        _engine.NotifyCombatEnd(finalState, victory, remainingHp);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hot-reload: replace the running script with new Lua source code.
    /// Used by AgenticScriptStrategy after the agent generates an improved script.
    /// </summary>
    public void ReloadScript(string newLuaSource)
    {
        _engine.LoadScript(newLuaSource);
        Log.Info("[AutoPlay/Lua] Script hot-reloaded");
    }

    /// <summary>
    /// Reload from the original file path.
    /// </summary>
    public void ReloadFromFile()
    {
        if (!string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath))
        {
            _engine.LoadScriptFile(_scriptPath);
        }
    }

    /// <summary>
    /// Get the current script source (for sending to agent for improvement).
    /// </summary>
    public string GetCurrentSource() => _engine.CurrentScriptSource;
}
