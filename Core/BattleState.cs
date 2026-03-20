namespace AutoPlayMod.Core;

/// <summary>
/// Snapshot of the entire combat state at a decision point.
/// This is the "world view" passed to strategy scripts and LLM agents.
/// </summary>
public class BattleState
{
    public PlayerState Player { get; set; } = new();
    public List<EnemyState> Enemies { get; set; } = [];
    public List<CardState> Hand { get; set; } = [];
    public List<PotionState> Potions { get; set; } = [];
    public List<RelicState> Relics { get; set; } = [];
    public int DrawPileCount { get; set; }
    public int DiscardPileCount { get; set; }
    public int ExhaustPileCount { get; set; }
    public int Round { get; set; }
    public int Turn { get; set; }
}

public class PlayerState
{
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public int Energy { get; set; }
    public int MaxEnergy { get; set; }
    public List<PowerState> Powers { get; set; } = [];
}

public class EnemyState
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public string IntentType { get; set; } = "Unknown";
    public int IntentDamage { get; set; }
    public int IntentHits { get; set; } = 1;
    public bool IsAlive { get; set; } = true;
    public List<PowerState> Powers { get; set; } = [];
}

public class CardState
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";  // Card effect text (e.g. "Deal 6 damage. Apply 2 Vulnerable.")
    public int Cost { get; set; }
    public string Type { get; set; } = "";        // Attack, Skill, Power, Status, Curse
    public string TargetType { get; set; } = "";   // None, AnyEnemy, AllEnemies, Self, etc.
    public bool CanPlay { get; set; }
    public bool Upgraded { get; set; }
}

public class PotionState
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";  // Potion effect text
    public string TargetType { get; set; } = "";
}

public class RelicState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Counter { get; set; }
}

public class PowerState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Amount { get; set; }
}
