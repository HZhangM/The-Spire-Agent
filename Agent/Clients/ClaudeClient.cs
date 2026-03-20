using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Agent.Clients;

/// <summary>
/// Claude API client with multi-turn conversation support.
/// Maintains message history within a combat for contextual decisions.
/// </summary>
public class ClaudeClient : ILlmClient
{
    public string ProviderName => "Claude";

    private readonly HttpClient _http;
    private readonly string _model;

    /// <summary>
    /// Conversation history — persists across calls within one combat.
    /// Cleared on StartConversation().
    /// </summary>
    private readonly List<JsonObject> _messages = new();

    /// <summary>
    /// Full conversation log for saving after combat.
    /// </summary>
    private readonly List<JsonObject> _fullLog = new();

    /// <summary>
    /// Pending tool results from the last assistant response.
    /// When Claude returns multiple tool_use blocks, we store results for query tools
    /// immediately and defer the action tool result until the caller provides it.
    /// All are sent together in the next user message.
    /// </summary>
    private readonly List<(string toolUseId, string? result)> _pendingToolResults = new();

    public ClaudeClient(string apiKey, string model = "claude-sonnet-4-20250514")
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com/"),
            DefaultRequestHeaders =
            {
                { "x-api-key", apiKey },
                { "anthropic-version", "2023-06-01" }
            }
        };
    }

    /// <summary>
    /// Start a new conversation thread (call at combat start).
    /// </summary>
    public void StartConversation()
    {
        _messages.Clear();
        _fullLog.Clear();
        _pendingToolResults.Clear();
        Log.Info("[AutoPlay/Claude] New conversation started");
    }

    /// <summary>
    /// Clean up conversation state after a timeout/cancellation.
    /// Ensures history is in a valid state for the next call:
    /// - No trailing user message without assistant response
    /// - No orphaned pending tool results
    /// - Messages alternate user/assistant correctly
    /// </summary>
    public void CleanupAfterInterruption()
    {
        _pendingToolResults.Clear();
        ExtraToolUses.Clear();

        if (_messages.Count == 0) return;

        // Remove trailing user message (was sent but never got a response)
        if (_messages[^1]["role"]?.ToString() == "user")
        {
            _messages.RemoveAt(_messages.Count - 1);
            Log.Info("[AutoPlay/Claude] Cleanup: removed trailing user message");
        }

        // If last message is assistant with tool_use, remove it too
        // (we never sent the tool_result, so this exchange is incomplete)
        if (_messages.Count > 0 && _messages[^1]["role"]?.ToString() == "assistant")
        {
            var json = _messages[^1].ToJsonString();
            if (json.Contains("\"tool_use\""))
            {
                _messages.RemoveAt(_messages.Count - 1);
                Log.Info("[AutoPlay/Claude] Cleanup: removed trailing assistant with tool_use");

                // Also remove the user message before it (the request that triggered this tool_use)
                if (_messages.Count > 0 && _messages[^1]["role"]?.ToString() == "user")
                {
                    _messages.RemoveAt(_messages.Count - 1);
                    Log.Info("[AutoPlay/Claude] Cleanup: removed preceding user message");
                }
            }
        }
    }

    /// <summary>
    /// Get the full conversation log for saving.
    /// </summary>
    public string GetConversationLog()
    {
        var logArray = new JsonArray();
        foreach (var msg in _fullLog)
            logArray.Add(JsonNode.Parse(msg.ToJsonString())!);
        return logArray.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var request = new
        {
            model = _model,
            max_tokens = 4096,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        var json = JsonSerializer.Serialize(request, JsonCtx.Default);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("v1/messages", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Claude API error {response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var textBlock = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();

        return textBlock ?? "";
    }

    /// <summary>
    /// Send a message in the multi-turn conversation and get a tool call back.
    /// If there's a pending tool_use_id, sends the content as a tool_result.
    /// Otherwise sends as a regular user message.
    /// Returns: (toolName, toolInput, toolUseId)
    /// </summary>
    public async Task<(string toolName, JsonElement input, string toolUseId)?> SendMessageAsync(
        string systemPrompt, string userContent, JsonElement[] tools, CancellationToken ct = default)
    {
        // Build the user message
        JsonObject userMsg;
        if (_pendingToolResults.Count > 0)
        {
            // Must send tool_result for ALL pending tool_uses from the last response.
            // The caller's userContent is the result for the action tool (the one with null result).
            var contentArray2 = new JsonArray();
            foreach (var (toolUseId, result) in _pendingToolResults)
            {
                contentArray2.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = result ?? userContent // null = action tool, use caller's content
                });
            }
            userMsg = new JsonObject
            {
                ["role"] = "user",
                ["content"] = contentArray2
            };
            _pendingToolResults.Clear();
        }
        else
        {
            // Regular user message
            userMsg = new JsonObject
            {
                ["role"] = "user",
                ["content"] = userContent
            };
        }

        _messages.Add(userMsg);
        _fullLog.Add(JsonNode.Parse(userMsg.ToJsonString())!.AsObject());

        // Sanitize message history before sending
        SanitizeMessages();

        // Build the API request
        var requestObj = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                }
            },
        };

        // Add tools with cache_control on the last tool
        var toolsArray = new JsonArray();
        for (int i = 0; i < tools.Length; i++)
        {
            var toolNode = JsonNode.Parse(tools[i].GetRawText())!;
            if (i == tools.Length - 1)
                toolNode.AsObject()["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            toolsArray.Add(toolNode);
        }
        requestObj["tools"] = toolsArray;

        // Add full message history
        var messagesArray = new JsonArray();
        foreach (var msg in _messages)
            messagesArray.Add(JsonNode.Parse(msg.ToJsonString())!);
        requestObj["messages"] = messagesArray;

        // Force tool use
        requestObj["tool_choice"] = new JsonObject { ["type"] = "any" };

        var json = requestObj.ToJsonString(JsonCtx.Default);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("v1/messages", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Claude API error {response.StatusCode}: {body}");

        // Don't use 'using' — we need JsonElements to survive past this method.
        // The JsonDocument will be GC'd eventually. This is intentional.
        var doc = JsonDocument.Parse(body);

        // Log cache performance
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
            var cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
            var inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            var outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            Log.Info($"[AutoPlay/Claude] Tokens: in={inputTokens} out={outputTokens} cache_read={cacheRead} cache_create={cacheCreate}");
        }

        // Record assistant response in history
        var contentArray = doc.RootElement.GetProperty("content");
        var assistantMsg = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = JsonNode.Parse(contentArray.GetRawText())!
        };
        _messages.Add(assistantMsg);
        _fullLog.Add(JsonNode.Parse(assistantMsg.ToJsonString())!.AsObject());

        // Collect ALL tool_use blocks from the response
        var toolUses = new List<(string name, JsonElement input, string id)>();
        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "tool_use")
            {
                toolUses.Add((
                    block.GetProperty("name").GetString()!,
                    block.GetProperty("input"),
                    block.GetProperty("id").GetString()!
                ));
            }
        }

        if (toolUses.Count == 0) return null;

        // Store ALL tool_use ids as pending results.
        // The first is returned to the caller. Extras are stored in ExtraToolUses
        // so RunAgentLoop can handle query tools and provide results.
        _pendingToolResults.Clear();
        ExtraToolUses.Clear();
        foreach (var tu in toolUses)
        {
            _pendingToolResults.Add((tu.id, null)); // null = result not yet provided
        }
        for (int i = 1; i < toolUses.Count; i++)
        {
            ExtraToolUses.Add(toolUses[i]);
        }

        return (toolUses[0].name, toolUses[0].input, toolUses[0].id);
    }

    /// <summary>
    /// Provide a result for a specific pending tool_use (used for query tools
    /// that are handled internally before the next API call).
    /// </summary>
    public void SetPendingToolResult(string toolUseId, string result)
    {
        for (int i = 0; i < _pendingToolResults.Count; i++)
        {
            if (_pendingToolResults[i].toolUseId == toolUseId)
            {
                _pendingToolResults[i] = (toolUseId, result);
                return;
            }
        }
    }

    /// <summary>
    /// Get all pending tool_use blocks (name, input, id) beyond the first one.
    /// The first is returned by SendMessageAsync; extras need handling by the caller.
    /// </summary>
    public List<(string name, JsonElement input, string id)> ExtraToolUses { get; } = new();

    /// <summary>
    /// Legacy single-turn method — still used by non-combat advisors.
    /// </summary>
    public async Task<JsonElement?> CompleteWithToolAsync(string systemPrompt, string userMessage,
        JsonElement[] tools, CancellationToken ct = default)
    {
        // Build request (no history)
        var requestObj = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                }
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = userMessage
                }
            },
        };

        var toolsArray = new JsonArray();
        for (int i = 0; i < tools.Length; i++)
        {
            var toolNode = JsonNode.Parse(tools[i].GetRawText())!;
            if (i == tools.Length - 1)
                toolNode.AsObject()["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            toolsArray.Add(toolNode);
        }
        requestObj["tools"] = toolsArray;
        requestObj["tool_choice"] = new JsonObject { ["type"] = "any" };

        var json = requestObj.ToJsonString(JsonCtx.Default);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("v1/messages", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Claude API error {response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);

        var contentArray2 = doc.RootElement.GetProperty("content");
        foreach (var block in contentArray2.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "tool_use")
            {
                var toolName = block.GetProperty("name").GetString();
                var input = block.GetProperty("input");
                var wrapper = JsonDocument.Parse($"{{\"tool\":\"{toolName}\",\"input\":{input.GetRawText()}}}");
                return wrapper.RootElement;
            }
        }

        return null;
    }

    /// <summary>
    /// Sanitize message history to ensure every tool_use has a matching tool_result.
    /// Uses proper JSON parsing to extract IDs reliably.
    /// </summary>
    private void SanitizeMessages()
    {
        var toolUseIds = new HashSet<string>();
        var toolResultIds = new HashSet<string>();

        foreach (var msg in _messages)
        {
            var role = msg["role"]?.ToString();
            var content = msg["content"];
            if (content == null) continue;

            // Parse content as JSON to extract blocks
            try
            {
                using var doc = JsonDocument.Parse(content.ToJsonString());
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in root.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var typeEl)) continue;
                        var type = typeEl.GetString();

                        if (type == "tool_use" && role == "assistant")
                        {
                            if (block.TryGetProperty("id", out var idEl))
                                toolUseIds.Add(idEl.GetString()!);
                        }
                        else if (type == "tool_result" && role == "user")
                        {
                            if (block.TryGetProperty("tool_use_id", out var idEl))
                                toolResultIds.Add(idEl.GetString()!);
                        }
                    }
                }
            }
            catch
            {
                // content might be a plain string, not an array — skip
            }
        }

        // Find orphaned tool_use ids (no matching tool_result)
        var orphanedIds = new HashSet<string>(toolUseIds);
        orphanedIds.ExceptWith(toolResultIds);

        if (orphanedIds.Count == 0) return;

        Log.Warn($"[AutoPlay/Claude] Sanitizing {orphanedIds.Count} orphaned tool_use(s): {string.Join(", ", orphanedIds)}");

        // Remove messages containing orphaned IDs + their following message
        var indicesToRemove = new HashSet<int>();
        for (int i = 0; i < _messages.Count; i++)
        {
            var msgJson = _messages[i].ToJsonString();
            foreach (var id in orphanedIds)
            {
                if (msgJson.Contains(id))
                {
                    indicesToRemove.Add(i);
                    if (i + 1 < _messages.Count)
                        indicesToRemove.Add(i + 1);
                    break;
                }
            }
        }

        foreach (var idx in indicesToRemove.OrderByDescending(x => x))
        {
            Log.Info($"[AutoPlay/Claude] Removing message at index {idx} (role={_messages[idx]["role"]})");
            _messages.RemoveAt(idx);
        }

        _pendingToolResults.Clear();

        // Ensure conversation doesn't end with assistant message
        while (_messages.Count > 0 && _messages[^1]["role"]?.ToString() == "assistant")
        {
            _messages.RemoveAt(_messages.Count - 1);
            Log.Info("[AutoPlay/Claude] Removed trailing assistant message");
        }
    }
}
