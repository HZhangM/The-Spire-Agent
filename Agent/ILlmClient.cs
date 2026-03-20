using System.Text.Json;

namespace AutoPlayMod.Agent;

/// <summary>
/// Unified LLM client interface. All providers implement this fully.
/// Supports text completion, single-turn tool use, and multi-turn conversation.
/// </summary>
public interface ILlmClient
{
    string ProviderName { get; }

    // ── Text Completion ──

    /// <summary>Send a prompt and get a text response.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    // ── Single-Turn Tool Use ──

    /// <summary>
    /// Send a message with tools and get back a tool call (no conversation history).
    /// Returns the tool result element, or null if no tool was called.
    /// </summary>
    Task<JsonElement?> CompleteWithToolAsync(string systemPrompt, string userMessage,
        JsonElement[] tools, CancellationToken ct = default);

    // ── Multi-Turn Conversation ──

    /// <summary>Start a new conversation, clearing all history.</summary>
    void StartConversation();

    /// <summary>
    /// Send a message in the multi-turn conversation.
    /// If there are pending tool results, they are sent automatically.
    /// Returns (toolName, toolInput, toolUseId) or null.
    /// </summary>
    Task<(string toolName, JsonElement input, string toolUseId)?> SendMessageAsync(
        string systemPrompt, string userContent, JsonElement[] tools, CancellationToken ct = default);

    /// <summary>Provide the result for a pending tool_use (for parallel tool calls).</summary>
    void SetPendingToolResult(string toolUseId, string result);

    /// <summary>Extra tool_use blocks from the last response (parallel calls).</summary>
    List<(string name, JsonElement input, string id)> ExtraToolUses { get; }

    /// <summary>Get the full conversation log as JSON string.</summary>
    string GetConversationLog();

    /// <summary>Clean up conversation state after a timeout/cancellation.</summary>
    void CleanupAfterInterruption();
}
