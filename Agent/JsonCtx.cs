using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPlayMod.Agent;

/// <summary>
/// Shared JSON serialization options.
/// </summary>
public static class JsonCtx
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
