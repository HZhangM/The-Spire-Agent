using MegaCrit.Sts2.Core.Localization;

namespace AutoPlayMod.Agent;

/// <summary>
/// Shared utilities.
/// </summary>
public static class JsonUtils
{
    /// <summary>
    /// Safely get text from a LocString. GetRawText() can throw LocException if the key
    /// doesn't exist in the localization table. This method never throws.
    /// </summary>
    public static string SafeLocText(LocString? loc, string fallback = "")
    {
        if (loc == null) return fallback;
        try { return loc.GetRawText() ?? fallback; }
        catch { return fallback; }
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
