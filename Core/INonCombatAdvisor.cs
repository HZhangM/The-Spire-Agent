namespace AutoPlayMod.Core;

/// <summary>
/// Interface for non-combat decision making.
/// In script mode, returns simple defaults.
/// In agent mode, queries the LLM for decisions.
/// </summary>
public interface INonCombatAdvisor
{
    /// <summary>Choose which map node to travel to. Returns index into available children.</summary>
    Task<int> ChooseMapNode(List<MapNodeInfo> availableNodes, GameSummary summary);

    /// <summary>Choose which event option to pick. Returns option index (0-based).</summary>
    Task<int> ChooseEventOption(string eventDescription, List<string> options, GameSummary summary);

    /// <summary>Choose rest site action. Returns index of available options.</summary>
    Task<int> ChooseRestSiteOption(List<string> options, GameSummary summary);

    /// <summary>
    /// Choose a card from a list. Context-dependent: upgrade, remove, reward, transform, etc.
    /// Returns index (0-based), or -1 to skip (only valid for rewards).
    /// </summary>
    Task<int> ChooseCard(CardSelectionContext context, List<string> cardNames, GameSummary summary);
}

/// <summary>
/// Describes WHY the player is selecting a card, so the advisor can make appropriate decisions.
/// </summary>
public enum CardSelectionPurpose
{
    /// <summary>Post-combat: choose a new card to add to deck. Can skip.</summary>
    Reward,
    /// <summary>Choose a card in hand to upgrade (e.g. Armaments).</summary>
    UpgradeInHand,
    /// <summary>Choose a card in deck to upgrade (e.g. rest site Smith).</summary>
    UpgradeFromDeck,
    /// <summary>Choose a card to remove from deck.</summary>
    Remove,
    /// <summary>Choose a card to transform into a random card.</summary>
    Transform,
    /// <summary>Choose a card for some other effect.</summary>
    Other,
}

public class CardSelectionContext
{
    public CardSelectionPurpose Purpose { get; set; } = CardSelectionPurpose.Other;
    /// <summary>Human-readable description of the screen, e.g. "Choose a card to upgrade"</summary>
    public string Description { get; set; } = "";
    /// <summary>Whether skipping is allowed (e.g. card rewards can be skipped)</summary>
    public bool CanSkip { get; set; }
}

public class MapNodeInfo
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public int Row { get; set; }
    public int Col { get; set; }
}

public class GameSummary
{
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }
    public int Floor { get; set; }
    public int Act { get; set; }
    public List<string> Relics { get; set; } = [];
    public List<string> DeckCards { get; set; } = [];
    public List<string> Potions { get; set; } = [];
    public int PotionSlots { get; set; }
    public int PotionSlotsMax { get; set; }
}
