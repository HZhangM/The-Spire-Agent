using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;

namespace AutoPlayMod.Agent;

/// <summary>
/// Shared utilities.
/// </summary>
public static class JsonUtils
{
    // Matches energy icon images — replace with "Energy" text
    private static readonly Regex EnergyIconRegex = new(@"\[img\][^\[]*energy[^\[]*\[/img\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Matches other [img]...[/img] blocks (non-energy icons)
    private static readonly Regex ImgRegex = new(@"\[img\][^\[]*\[/img\]", RegexOptions.Compiled);
    // Matches BBCode tags like [gold], [/gold], [blue], [green], etc.
    private static readonly Regex BbCodeTagRegex = new(@"\[/?[a-zA-Z_][^\]]*\]", RegexOptions.Compiled);
    // Matches unresolved SmartFormat variables like {Heal}, {DexterityPower}, {energyPrefix:...}
    private static readonly Regex SmartFormatVarRegex = new(@"\{[a-zA-Z]\w*(?::[^}]*)?\}", RegexOptions.Compiled);

    /// <summary>
    /// Safely get text from a LocString. Tries GetFormattedText() first (resolves variables),
    /// falls back to GetRawText(). Strips BBCode tags and unresolved SmartFormat variables.
    /// Never throws.
    /// </summary>
    /// <summary>
    /// Safe raw text extraction. Uses GetRawText() only (no SmartFormat).
    /// For descriptions that need variable resolution, use SafeFormattedLocText() instead.
    /// </summary>
    public static string SafeLocText(LocString? loc, string fallback = "")
    {
        if (loc == null) return fallback;
        try
        {
            var text = loc.GetRawText();
            if (string.IsNullOrEmpty(text)) return fallback;
            return CleanText(text);
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Safe formatted text extraction. Uses GetFormattedText() which resolves SmartFormat
    /// variables but may log errors for missing variables. Use only when variables have
    /// been pre-injected (e.g. power descriptions with Amount/OwnerName).
    /// </summary>
    public static string SafeFormattedLocText(LocString? loc, string fallback = "")
    {
        if (loc == null) return fallback;
        try
        {
            var text = loc.GetFormattedText();
            if (string.IsNullOrEmpty(text)) return fallback;
            return CleanText(text);
        }
        catch
        {
            // Fall back to raw
            return SafeLocText(loc, fallback);
        }
    }

    /// <summary>
    /// Strip BBCode tags and unresolved SmartFormat variables from text.
    /// </summary>
    public static string CleanText(string text)
    {
        // Replace energy icon images with "Energy" text
        text = EnergyIconRegex.Replace(text, "Energy");
        // Remove other [img]...[/img] blocks
        text = ImgRegex.Replace(text, "");
        // Remove BBCode tags like [gold], [/gold], [blue], etc.
        text = BbCodeTagRegex.Replace(text, "");
        // Replace known SmartFormat variables with meaningful text
        text = Regex.Replace(text, @"\{energyPrefix:[^}]*\}", "Energy");
        // Remove remaining unresolved SmartFormat variables
        text = SmartFormatVarRegex.Replace(text, "?");
        // Collapse multiple spaces
        text = Regex.Replace(text, @"  +", " ");
        return text.Trim();
    }

    /// <summary>
    /// Extract a JSON object or array from an LLM response.
    /// Handles markdown code fences, leading text, etc.
    /// </summary>
    public static string? ExtractJson(string response)
    {
        var text = response.Trim();

        // Strip markdown code fences
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var lastFence = text.LastIndexOf("```");
            if (lastFence >= 0) text = text[..lastFence];
            text = text.Trim();
        }

        // Find JSON object or array
        int start = text.IndexOfAny(['{', '[']);
        if (start < 0) return null;

        char open = text[start], close = open == '{' ? '}' : ']';
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) depth--;
            if (depth == 0) return text[start..(i + 1)];
        }

        return text[start..];
    }

    /// <summary>
    /// Extract a code block with a specific language tag from an LLM response.
    /// E.g., ExtractCodeBlock(response, "lua") extracts content from ```lua...```
    /// </summary>
    public static string? ExtractCodeBlock(string response, string? language = null)
    {
        var text = response.Trim();
        var fence = language != null ? $"```{language}" : "```";

        var codeStart = text.IndexOf(fence, StringComparison.OrdinalIgnoreCase);
        if (codeStart < 0 && language != null)
            codeStart = text.IndexOf("```", StringComparison.Ordinal); // fallback to any fence
        if (codeStart < 0) return null;

        var afterFence = text.IndexOf('\n', codeStart);
        if (afterFence < 0) return null;

        var blockEnd = text.IndexOf("```", afterFence + 1, StringComparison.Ordinal);
        if (blockEnd < 0) return text[(afterFence + 1)..].Trim();

        return text[(afterFence + 1)..blockEnd].Trim();
    }
}
