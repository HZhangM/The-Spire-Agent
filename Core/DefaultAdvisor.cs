namespace AutoPlayMod.Core;

/// <summary>
/// Simple rule-based advisor for non-combat decisions.
/// Used in script mode and as fallback.
/// </summary>
public class DefaultAdvisor : INonCombatAdvisor
{
    public Task<int> ChooseMapNode(List<MapNodeInfo> availableNodes, GameSummary summary)
    {
        // Prefer: RestSite if low HP > Event > Monster > Elite > Shop > Treasure
        bool lowHp = summary.Hp < summary.MaxHp * 0.4;

        if (lowHp)
        {
            var rest = availableNodes.FindIndex(n => n.Type == "RestSite");
            if (rest >= 0) return Task.FromResult(rest);
        }

        // Prefer events for variety, then monsters for rewards
        var eventNode = availableNodes.FindIndex(n => n.Type == "Event");
        if (eventNode >= 0) return Task.FromResult(eventNode);

        var monster = availableNodes.FindIndex(n => n.Type == "Monster");
        if (monster >= 0) return Task.FromResult(monster);

        // Default: first node
        return Task.FromResult(0);
    }

    public Task<int> ChooseEventOption(string eventDescription, List<string> options, GameSummary summary)
    {
        // Always pick the first option
        return Task.FromResult(0);
    }

    public Task<int> ChooseRestSiteOption(List<string> options, GameSummary summary)
    {
        // Rest (heal) if below 60% HP, otherwise smith (upgrade)
        bool shouldHeal = summary.Hp < summary.MaxHp * 0.6;

        if (shouldHeal)
        {
            var restIdx = options.FindIndex(o =>
                o.Contains("Rest", StringComparison.OrdinalIgnoreCase) ||
                o.Contains("Heal", StringComparison.OrdinalIgnoreCase));
            if (restIdx >= 0) return Task.FromResult(restIdx);
        }

        // Look for smith/upgrade
        var smithIdx = options.FindIndex(o =>
            o.Contains("Smith", StringComparison.OrdinalIgnoreCase) ||
            o.Contains("Upgrade", StringComparison.OrdinalIgnoreCase));
        if (smithIdx >= 0) return Task.FromResult(smithIdx);

        return Task.FromResult(0);
    }

    public Task<int> ChooseCard(CardSelectionContext context, List<string> cardNames, GameSummary summary)
    {
        return context.Purpose switch
        {
            // Remove: pick Strike first, then Defend
            CardSelectionPurpose.Remove => Task.FromResult(
                cardNames.FindIndex(c => c.Contains("Strike", StringComparison.OrdinalIgnoreCase)) is >= 0 and var si ? si
                : cardNames.FindIndex(c => c.Contains("Defend", StringComparison.OrdinalIgnoreCase)) is >= 0 and var di ? di
                : 0),

            // Upgrade: pick the first non-Strike, non-Defend card (upgrade scaling cards)
            CardSelectionPurpose.UpgradeInHand or CardSelectionPurpose.UpgradeFromDeck => Task.FromResult(
                cardNames.FindIndex(c =>
                    !c.Contains("Strike", StringComparison.OrdinalIgnoreCase) &&
                    !c.Contains("Defend", StringComparison.OrdinalIgnoreCase)) is >= 0 and var idx ? idx : 0),

            // Reward: always pick first card
            _ => Task.FromResult(0),
        };
    }
}
