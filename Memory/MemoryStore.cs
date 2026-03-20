using System.Text.Json;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Memory;

/// <summary>
/// File-based memory store. Each entity gets its own JSON file.
/// Scoped per character with a shared general/ directory.
/// Thread-safe for concurrent reads; writes use simple locking.
/// </summary>
public class MemoryStore
{
    private readonly string _basePath;
    private readonly string _characterId;
    private readonly object _writeLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public MemoryStore(string basePath, string characterId)
    {
        _basePath = basePath;
        _characterId = SanitizeName(characterId);
        EnsureDirectories();
    }

    public void SetCharacter(string characterId)
    {
        // Allow changing character mid-session (e.g. new run)
        var field = typeof(MemoryStore).GetField("_characterId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(this, SanitizeName(characterId));
        EnsureDirectories();
    }

    #region Read Operations

    /// <summary>Read a single entity memory by exact name.</summary>
    public MemoryEntry? Read(string category, string name)
    {
        var path = GetEntityPath(category, name);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MemoryEntry>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Failed to read {category}/{name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Read an archetype memory by name.</summary>
    public ArchetypeEntry? ReadArchetype(string name)
    {
        var path = GetEntityPath("archetype", name);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ArchetypeEntry>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Failed to read archetype/{name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Read both character-specific and general strategy.</summary>
    public (StrategyEntry? character, StrategyEntry? general) ReadStrategy()
    {
        var charPath = Path.Combine(_basePath, _characterId, "strategy.json");
        var generalPath = Path.Combine(_basePath, "general", "strategy.json");

        StrategyEntry? charStrategy = null, genStrategy = null;
        if (File.Exists(charPath))
        {
            try { charStrategy = JsonSerializer.Deserialize<StrategyEntry>(File.ReadAllText(charPath), JsonOpts); }
            catch { }
        }
        if (File.Exists(generalPath))
        {
            try { genStrategy = JsonSerializer.Deserialize<StrategyEntry>(File.ReadAllText(generalPath), JsonOpts); }
            catch { }
        }
        return (charStrategy, genStrategy);
    }

    /// <summary>Search all entities in a category by keyword (name or observation content).</summary>
    public List<MemoryEntry> Search(string category, string keyword)
    {
        var results = new List<MemoryEntry>();
        var dir = GetCategoryDir(category);
        if (!Directory.Exists(dir)) return results;

        var kw = keyword.ToLowerInvariant();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<MemoryEntry>(json, JsonOpts);
                if (entry == null) continue;

                bool matches = entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                    matches = entry.Observations.Any(o => o.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (!matches)
                    matches = entry.Synergies.Any(s => s.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                if (matches) results.Add(entry);
            }
            catch { }
        }
        return results;
    }

    /// <summary>Get all entries in a category.</summary>
    public List<MemoryEntry> GetAllInCategory(string category)
    {
        var results = new List<MemoryEntry>();
        var dir = GetCategoryDir(category);
        if (!Directory.Exists(dir)) return results;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<MemoryEntry>(File.ReadAllText(file), JsonOpts);
                if (entry != null) results.Add(entry);
            }
            catch { }
        }
        return results;
    }

    /// <summary>Get all archetype entries.</summary>
    public List<ArchetypeEntry> GetAllArchetypes()
    {
        var results = new List<ArchetypeEntry>();
        var dir = GetCategoryDir("archetype");
        if (!Directory.Exists(dir)) return results;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<ArchetypeEntry>(File.ReadAllText(file), JsonOpts);
                if (entry != null) results.Add(entry);
            }
            catch { }
        }
        return results;
    }

    /// <summary>Get recent run entries.</summary>
    public List<RunEntry> GetRecentRuns(int count = 5)
    {
        var results = new List<RunEntry>();
        var dir = GetCategoryDir("run");
        if (!Directory.Exists(dir)) return results;

        var files = Directory.GetFiles(dir, "*.json")
            .OrderByDescending(f => f)
            .Take(count);

        foreach (var file in files)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<RunEntry>(File.ReadAllText(file), JsonOpts);
                if (entry != null) results.Add(entry);
            }
            catch { }
        }
        return results;
    }

    /// <summary>
    /// Get scored, truncated memory text for injection into prompts.
    /// Returns the highest-scoring entries that fit within the token budget.
    /// Approximate: 1 token ≈ 4 chars.
    /// </summary>
    public string GetForInjection(string category, IEnumerable<string> names, int tokenBudget = 500)
    {
        var entries = new List<MemoryEntry>();
        foreach (var name in names)
        {
            var entry = Read(category, name);
            if (entry != null) entries.Add(entry);
        }

        if (entries.Count == 0) return "";

        // Sort by injection score descending
        entries.Sort((a, b) => b.InjectionScore().CompareTo(a.InjectionScore()));

        var sb = new System.Text.StringBuilder();
        int charBudget = tokenBudget * 4; // approximate

        foreach (var entry in entries)
        {
            var text = entry.ToInjectionString();
            if (sb.Length + text.Length > charBudget) break;
            sb.AppendLine(text);
        }

        return sb.ToString().TrimEnd();
    }

    #endregion

    #region Write Operations

    /// <summary>Write or update an entity memory. Creates file if new, appends observation if existing.</summary>
    public void Write(string category, string name, string observation, int? rating = null, List<string>? synergies = null)
    {
        lock (_writeLock)
        {
            var existing = Read(category, name);
            if (existing != null)
            {
                existing.Observations.Add(observation);
                if (rating.HasValue) existing.Rating = rating.Value;
                if (synergies != null)
                {
                    foreach (var s in synergies)
                        if (!existing.Synergies.Contains(s))
                            existing.Synergies.Add(s);
                }
                existing.EncounterCount++;
                existing.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd");
                SaveEntity(category, name, existing);
            }
            else
            {
                var entry = new MemoryEntry
                {
                    Name = name,
                    Observations = [observation],
                    Rating = rating ?? 3,
                    Synergies = synergies ?? [],
                    EncounterCount = 1,
                    LastUpdated = DateTime.Now.ToString("yyyy-MM-dd")
                };
                SaveEntity(category, name, entry);
            }
        }
    }

    /// <summary>Append an observation to an existing entry (no-op if entry doesn't exist).</summary>
    public void Append(string category, string name, string observation)
    {
        lock (_writeLock)
        {
            var existing = Read(category, name);
            if (existing == null) return;
            existing.Observations.Add(observation);
            existing.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            SaveEntity(category, name, existing);
        }
    }

    /// <summary>Update rating of an existing entry.</summary>
    public void UpdateRating(string category, string name, int rating)
    {
        lock (_writeLock)
        {
            var existing = Read(category, name);
            if (existing == null) return;
            existing.Rating = Math.Clamp(rating, 1, 5);
            existing.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            SaveEntity(category, name, existing);
        }
    }

    /// <summary>Replace observations list (used by merge).</summary>
    public void ReplaceObservations(string category, string name, List<string> observations,
        List<string>? synergies = null, List<string>? antiSynergies = null, int? rating = null)
    {
        lock (_writeLock)
        {
            var existing = Read(category, name);
            if (existing == null) return;
            existing.Observations = observations;
            if (synergies != null) existing.Synergies = synergies;
            if (antiSynergies != null) existing.AntiSynergies = antiSynergies;
            if (rating.HasValue) existing.Rating = rating.Value;
            existing.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            SaveEntity(category, name, existing);
        }
    }

    /// <summary>Save an archetype entry.</summary>
    public void SaveArchetype(ArchetypeEntry archetype)
    {
        lock (_writeLock)
        {
            archetype.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            var path = GetEntityPath("archetype", archetype.Name);
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(archetype, JsonOpts));
        }
    }

    /// <summary>Save a run entry.</summary>
    public void SaveRun(RunEntry run)
    {
        lock (_writeLock)
        {
            var dir = GetCategoryDir("run");
            Directory.CreateDirectory(dir);
            var filename = $"run_{run.RunId:D3}.json";
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, JsonSerializer.Serialize(run, JsonOpts));

            // Prune old runs (keep last 50)
            var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList();
            while (files.Count > 50)
            {
                File.Delete(files[0]);
                files.RemoveAt(0);
            }
        }
    }

    /// <summary>Save strategy (character or general).</summary>
    public void SaveStrategy(string scope, StrategyEntry strategy)
    {
        lock (_writeLock)
        {
            strategy.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            var dir = scope == "general"
                ? Path.Combine(_basePath, "general")
                : Path.Combine(_basePath, _characterId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "strategy.json");
            File.WriteAllText(path, JsonSerializer.Serialize(strategy, JsonOpts));
        }
    }

    /// <summary>Append to strategy observations.</summary>
    public void AppendStrategy(string scope, string observation)
    {
        lock (_writeLock)
        {
            var dir = scope == "general"
                ? Path.Combine(_basePath, "general")
                : Path.Combine(_basePath, _characterId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "strategy.json");

            StrategyEntry strategy;
            if (File.Exists(path))
            {
                try { strategy = JsonSerializer.Deserialize<StrategyEntry>(File.ReadAllText(path), JsonOpts) ?? new(); }
                catch { strategy = new(); }
            }
            else
            {
                strategy = new();
            }

            strategy.Observations.Add(observation);
            // Keep max 20
            while (strategy.Observations.Count > 20)
                strategy.Observations.RemoveAt(0);
            strategy.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            File.WriteAllText(path, JsonSerializer.Serialize(strategy, JsonOpts));
        }
    }

    /// <summary>Check if an entity needs merging (8+ observations).</summary>
    public bool NeedsMerge(string category, string name)
    {
        var entry = Read(category, name);
        return entry != null && entry.Observations.Count >= 8;
    }

    /// <summary>Get next run ID.</summary>
    public int GetNextRunId()
    {
        var dir = GetCategoryDir("run");
        if (!Directory.Exists(dir)) return 1;
        var files = Directory.GetFiles(dir, "run_*.json");
        if (files.Length == 0) return 1;
        var maxId = files.Select(f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            return int.TryParse(name.Replace("run_", ""), out var id) ? id : 0;
        }).Max();
        return maxId + 1;
    }

    #endregion

    #region Path Helpers

    /// <summary>
    /// Categories that are shared across all characters (game knowledge).
    /// </summary>
    private static readonly HashSet<string> SharedCategories = ["card", "enemy", "event", "relic"];

    private void EnsureDirectories()
    {
        // Shared knowledge (not character-specific)
        var sharedDir = Path.Combine(_basePath, "shared");
        Directory.CreateDirectory(Path.Combine(sharedDir, "cards"));
        Directory.CreateDirectory(Path.Combine(sharedDir, "enemies"));
        Directory.CreateDirectory(Path.Combine(sharedDir, "events"));
        Directory.CreateDirectory(Path.Combine(sharedDir, "relics"));

        // Character-specific (strategy, archetypes, runs)
        var charDir = Path.Combine(_basePath, _characterId);
        Directory.CreateDirectory(Path.Combine(charDir, "archetypes"));
        Directory.CreateDirectory(Path.Combine(charDir, "runs"));
        Directory.CreateDirectory(Path.Combine(_basePath, "general"));
    }

    private string GetCategoryDir(string category)
    {
        var subdir = category switch
        {
            "card" => "cards",
            "enemy" => "enemies",
            "event" => "events",
            "relic" => "relics",
            "archetype" => "archetypes",
            "run" => "runs",
            _ => category
        };

        // Shared categories go to shared/, character-specific go to {character}/
        if (SharedCategories.Contains(category))
            return Path.Combine(_basePath, "shared", subdir);

        return Path.Combine(_basePath, _characterId, subdir);
    }

    private string GetEntityPath(string category, string name)
    {
        return Path.Combine(GetCategoryDir(category), SanitizeName(name) + ".json");
    }

    private void SaveEntity(string category, string name, MemoryEntry entry)
    {
        var path = GetEntityPath(category, name);
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(entry, JsonOpts));
    }

    /// <summary>Sanitize a name for use as a filename.</summary>
    public static string SanitizeName(string name)
    {
        // Lowercase, replace spaces/special chars with underscore
        var sanitized = name.ToLowerInvariant().Trim();
        sanitized = Regex.Replace(sanitized, @"[^a-z0-9_]", "_");
        sanitized = Regex.Replace(sanitized, @"_+", "_");
        sanitized = sanitized.Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
    }

    #endregion
}
