using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Agent.Clients;

/// <summary>
/// OpenAI-compatible API client with full multi-turn conversation + tool use.
/// Works with OpenAI, Azure OpenAI, and any OpenAI-compatible endpoint.
/// </summary>
public class GptClient : ILlmClient
{
    public string ProviderName => "GPT";

    private readonly HttpClient _http;
    private readonly string _model;

    private readonly List<JsonObject> _messages = new();
    private readonly List<JsonObject> _fullLog = new();
    private readonly List<(string toolCallId, string? result)> _pendingToolResults = new();
    public List<(string name, JsonElement input, string id)> ExtraToolUses { get; } = new();

    public GptClient(string apiKey, string model = "gpt-4o", string? baseUrl = null)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl ?? "https://api.openai.com/"),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public void StartConversation()
    {
        _messages.Clear();
        _fullLog.Clear();
        _pendingToolResults.Clear();
        ExtraToolUses.Clear();
        Log.Info("[AutoPlay/GPT] New conversation started");
    }

    public string GetConversationLog()
    {
        var arr = new JsonArray();
        foreach (var msg in _fullLog)
            arr.Add(JsonNode.Parse(msg.ToJsonString())!);
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 4096,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userMessage }
            }
        };

        var body = await PostAsync("v1/chat/completions", request, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public async Task<JsonElement?> CompleteWithToolAsync(string systemPrompt, string userMessage,
        JsonElement[] tools, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userMessage }
            },
            ["tools"] = ConvertToolsToOpenAI(tools),
            ["tool_choice"] = "required"
        };

        var body = await PostAsync("v1/chat/completions", request, ct);
        var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
        {
            var tc = toolCalls[0];
            var fn = tc.GetProperty("function");
            var toolName = fn.GetProperty("name").GetString()!;
            var input = JsonDocument.Parse(fn.GetProperty("arguments").GetString()!).RootElement;
            var wrapper = JsonDocument.Parse($"{{\"tool\":\"{toolName}\",\"input\":{input.GetRawText()}}}");
            return wrapper.RootElement;
        }
        return null;
    }

    public async Task<(string toolName, JsonElement input, string toolUseId)?> SendMessageAsync(
        string systemPrompt, string userContent, JsonElement[] tools, CancellationToken ct = default)
    {
        // Build user message — include tool results if pending
        if (_pendingToolResults.Count > 0)
        {
            foreach (var (toolCallId, result) in _pendingToolResults)
            {
                var toolMsg = new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolCallId,
                    ["content"] = result ?? userContent
                };
                _messages.Add(toolMsg);
                _fullLog.Add(JsonNode.Parse(toolMsg.ToJsonString())!.AsObject());
            }
            _pendingToolResults.Clear();
        }
        else
        {
            var userMsg = new JsonObject { ["role"] = "user", ["content"] = userContent };
            _messages.Add(userMsg);
            _fullLog.Add(JsonNode.Parse(userMsg.ToJsonString())!.AsObject());
        }

        // Build request
        var messagesArray = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };
        foreach (var msg in _messages)
            messagesArray.Add(JsonNode.Parse(msg.ToJsonString())!);

        var request = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["messages"] = messagesArray,
            ["tools"] = ConvertToolsToOpenAI(tools),
            ["tool_choice"] = "required"
        };

        var body = await PostAsync("v1/chat/completions", request, ct);
        var doc = JsonDocument.Parse(body);

        // Log tokens
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var inTok = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            var outTok = usage.TryGetProperty("completion_tokens", out var cpt) ? cpt.GetInt32() : 0;
            Log.Info($"[AutoPlay/GPT] Tokens: in={inTok} out={outTok}");
        }

        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

        // Record assistant message
        var assistantMsg = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = JsonNode.Parse(message.GetRawText())!
        };
        _messages.Add(assistantMsg);
        _fullLog.Add(JsonNode.Parse(assistantMsg.ToJsonString())!.AsObject());

        // Parse tool calls
        if (!message.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.GetArrayLength() == 0)
            return null;

        var allCalls = new List<(string name, JsonElement input, string id)>();
        foreach (var tc in toolCalls.EnumerateArray())
        {
            var fn = tc.GetProperty("function");
            var name = fn.GetProperty("name").GetString()!;
            var args = fn.GetProperty("arguments").GetString()!;
            var input = JsonDocument.Parse(args).RootElement;
            var id = tc.GetProperty("id").GetString()!;
            allCalls.Add((name, input, id));
        }

        _pendingToolResults.Clear();
        ExtraToolUses.Clear();
        foreach (var call in allCalls)
            _pendingToolResults.Add((call.id, null));
        for (int i = 1; i < allCalls.Count; i++)
            ExtraToolUses.Add(allCalls[i]);

        return (allCalls[0].name, allCalls[0].input, allCalls[0].id);
    }

    public void SetPendingToolResult(string toolUseId, string result)
    {
        for (int i = 0; i < _pendingToolResults.Count; i++)
        {
            if (_pendingToolResults[i].toolCallId == toolUseId)
            {
                _pendingToolResults[i] = (toolUseId, result);
                return;
            }
        }
    }

    public void CleanupAfterInterruption()
    {
        _pendingToolResults.Clear();
        ExtraToolUses.Clear();
        // Remove trailing incomplete messages
        while (_messages.Count > 0)
        {
            var role = _messages[^1]["role"]?.ToString();
            if (role == "tool" || role == "user")
                _messages.RemoveAt(_messages.Count - 1);
            else if (role == "assistant")
            {
                var json = _messages[^1].ToJsonString();
                if (json.Contains("tool_calls"))
                {
                    _messages.RemoveAt(_messages.Count - 1);
                    continue;
                }
                break;
            }
            else break;
        }
    }

    #region Helpers

    private async Task<string> PostAsync(string url, JsonObject request, CancellationToken ct)
    {
        var json = request.ToJsonString();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GPT API error {response.StatusCode}: {body}");
        return body;
    }

    /// <summary>Convert Claude-format tool definitions to OpenAI function calling format.</summary>
    private static JsonArray ConvertToolsToOpenAI(JsonElement[] tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            var desc = tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var schema = tool.TryGetProperty("input_schema", out var s) ? s.GetRawText() : "{}";

            var fn = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["parameters"] = JsonNode.Parse(schema)!
                }
            };
            result.Add(fn);
        }
        return result;
    }

    #endregion
}
