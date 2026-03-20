using System.Text;

namespace AutoPlayMod.Core;

/// <summary>
/// In-memory context for the current run. Persists across combat/non-combat sessions
/// within one run, auto-injected into every new session's first message.
/// Reset when a new run starts. NOT saved to disk (that's MemoryStore's job).
///
/// Updated automatically after each session by RunContextExtractor.
/// </summary>
public class RunContext
{
    /// <summary>Current deck archetype, e.g. "Strength Build", "Block/Barricade".</summary>
    public string Archetype { get; set; } = "";

    /// <summary>What the deck is missing, e.g. "AOE damage", "card draw".</summary>
    public List<string> DeckGaps { get; set; } = [];

    /// <summary>Current strategic goals, e.g. "Find Whirlwind before Act 2 boss".</summary>
    public List<string> CurrentGoals { get; set; } = [];

    /// <summary>Key decisions made this run with reasoning.</summary>
    public List<string> KeyDecisions { get; set; } = [];

    /// <summary>Current floor number.</summary>
    public int Floor { get; set; }

    /// <summary>Current act.</summary>
    public int Act { get; set; }

    /// <summary>Number of combats completed this run.</summary>
    public int CombatsCompleted { get; set; }

    /// <summary>Brief one-line strategy note, updated after each session.</summary>
    public string StrategyNote { get; set; } = "";

    private const int MaxKeyDecisions = 15;
    private const int MaxGoals = 5;
    private const int MaxGaps = 5;

    public void AddDecision(string decision)
    {
        KeyDecisions.Add(decision);
        while (KeyDecisions.Count > MaxKeyDecisions)
            KeyDecisions.RemoveAt(0);
    }

    public void SetGoals(List<string> goals)
    {
        CurrentGoals = goals.Take(MaxGoals).ToList();
    }

    public void SetGaps(List<string> gaps)
    {
        DeckGaps = gaps.Take(MaxGaps).ToList();
    }

    public void Reset()
    {
        Archetype = "";
        DeckGaps.Clear();
        CurrentGoals.Clear();
        KeyDecisions.Clear();
        Floor = 0;
        Act = 0;
        CombatsCompleted = 0;
        StrategyNote = "";
    }

    /// <summary>
    /// Format for injection into session prompts.
    /// Returns empty string if no context yet.
    /// </summary>
    public string ToInjectionString()
    {
        if (string.IsNullOrEmpty(Archetype) && KeyDecisions.Count == 0 && CurrentGoals.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("=== CURRENT RUN CONTEXT ===");
        sb.AppendLine($"Act {Act}, Floor {Floor} | Combats: {CombatsCompleted}");

        if (!string.IsNullOrEmpty(Archetype))
            sb.AppendLine($"Archetype: {Archetype}");

        if (!string.IsNullOrEmpty(StrategyNote))
            sb.AppendLine($"Strategy: {StrategyNote}");

        if (DeckGaps.Count > 0)
            sb.AppendLine($"Deck gaps: {string.Join(", ", DeckGaps)}");

        if (CurrentGoals.Count > 0)
        {
            sb.AppendLine("Goals:");
            foreach (var g in CurrentGoals) sb.AppendLine($"  - {g}");
        }

        if (KeyDecisions.Count > 0)
        {
            sb.AppendLine($"Recent decisions ({KeyDecisions.Count}):");
            // Show last 5 decisions to keep injection compact
            foreach (var d in KeyDecisions.TakeLast(5)) sb.AppendLine($"  - {d}");
        }

        sb.Append("=== END RUN CONTEXT ===");
        return sb.ToString();
    }
}
