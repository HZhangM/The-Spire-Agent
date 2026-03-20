using System.Text.Json.Serialization;

namespace AutoPlayMod.Memory;

/// <summary>
/// A single entity memory (card, enemy, event, relic).
/// One file per entity, indexed by sanitized name.
/// </summary>
public class MemoryEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("observations")]
    public List<string> Observations { get; set; } = [];

    [JsonPropertyName("synergies")]
    public List<string> Synergies { get; set; } = [];

    [JsonPropertyName("anti_synergies")]
    public List<string> AntiSynergies { get; set; } = [];

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("encounter_count")]
    public int EncounterCount { get; set; }

    [JsonPropertyName("last_updated")]
    public string LastUpdated { get; set; } = "";

    /// <summary>Format for injection into prompts.</summary>
    public string ToInjectionString()
    {
        var parts = new List<string> { $"{Name} (rating:{Rating}, seen:{EncounterCount}x)" };
        foreach (var obs in Observations)
            parts.Add($"  - {obs}");
        if (Synergies.Count > 0)
            parts.Add($"  Synergies: {string.Join(", ", Synergies)}");
        if (AntiSynergies.Count > 0)
            parts.Add($"  Anti-synergies: {string.Join(", ", AntiSynergies)}");
        return string.Join("\n", parts);
    }

    /// <summary>Relevance score for injection priority.</summary>
    public double InjectionScore()
    {
        double score = Rating * 2.0;
        score += Math.Min(EncounterCount, 10) * 0.5;
        // Recency boost: entries updated today get +2
        if (DateTime.TryParse(LastUpdated, out var dt) && (DateTime.Now - dt).TotalDays < 1)
            score += 2.0;
        // Penalize very long entries
        int totalLength = Observations.Sum(o => o.Length);
        if (totalLength > 300) score -= 1.0;
        return score;
    }
}

/// <summary>
/// Archetype memory — a deck build pattern.
/// </summary>
public class ArchetypeEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("core_cards")]
    public List<string> CoreCards { get; set; } = [];

    [JsonPropertyName("support_cards")]
    public List<string> SupportCards { get; set; } = [];

    [JsonPropertyName("key_relics")]
    public List<string> KeyRelics { get; set; } = [];

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; set; } = [];

    [JsonPropertyName("weaknesses")]
    public List<string> Weaknesses { get; set; } = [];

    [JsonPropertyName("observations")]
    public List<string> Observations { get; set; } = [];

    [JsonPropertyName("win_rate_note")]
    public string WinRateNote { get; set; } = "";

    [JsonPropertyName("last_updated")]
    public string LastUpdated { get; set; } = "";

    public string ToInjectionString()
    {
        var parts = new List<string>
        {
            $"Archetype: {Name} — {Description}",
            $"  Core: {string.Join(", ", CoreCards)}",
            $"  Support: {string.Join(", ", SupportCards)}",
        };
        if (Strengths.Count > 0)
            parts.Add($"  Strengths: {string.Join("; ", Strengths)}");
        if (Weaknesses.Count > 0)
            parts.Add($"  Weaknesses: {string.Join("; ", Weaknesses)}");
        foreach (var obs in Observations)
            parts.Add($"  - {obs}");
        return string.Join("\n", parts);
    }
}

/// <summary>
/// Episodic run memory — one complete run from start to end.
/// </summary>
public class RunEntry
{
    [JsonPropertyName("run_id")]
    public int RunId { get; set; }

    [JsonPropertyName("character")]
    public string Character { get; set; } = "";

    [JsonPropertyName("archetype")]
    public string Archetype { get; set; } = "";

    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("final_floor")]
    public int FinalFloor { get; set; }

    [JsonPropertyName("key_decisions")]
    public List<string> KeyDecisions { get; set; } = [];

    [JsonPropertyName("cause_of_end")]
    public string CauseOfEnd { get; set; } = "";

    [JsonPropertyName("lessons")]
    public List<string> Lessons { get; set; } = [];

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";
}

/// <summary>
/// Strategy memory — stored in strategy.json per character and in general/.
/// Uses the same MemoryEntry format with observations.
/// </summary>
public class StrategyEntry
{
    [JsonPropertyName("observations")]
    public List<string> Observations { get; set; } = [];

    [JsonPropertyName("last_updated")]
    public string LastUpdated { get; set; } = "";
}
