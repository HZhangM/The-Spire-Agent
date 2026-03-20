using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Logging;

namespace AutoPlayMod.Agent.Clients;

/// <summary>
/// Google Gemini API client with full multi-turn conversation + tool use.
/// </summary>
public class GeminiClient : ILlmClient
{
    public string ProviderName => "Gemini";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiKey;

    private readonly List<JsonObject> _contents = new();
    private readonly List<JsonObject> _fullLog = new();
    private readonly List<(string callId, string name, string? result)> _pendingToolResults = new();
    public List<(string name, JsonElement input, string id)> ExtraToolUses { get; } = new();

    private int _callIdCounter;

    public GeminiClient(string apiKey, string model = "gemini-2.5-flash")
    {
        _apiKey = apiKey;
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };
    }

    public void StartConversation()
    {
        _contents.Clear();
        _fullLog.Clear();
        _pendingToolResults.Clear();
        ExtraToolUses.Clear();
        _callIdCounter = 0;
        Log.Info("[AutoPlay/Gemini] New conversation started");
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
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
            },
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = userMessage } }
                }
            },
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 4096 }
        };

        var body = await PostAsync($"v1beta/models/{_model}:generateContent?key={_apiKey}", request, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0]
            .GetProperty("text").GetString() ?? "";
    }

    public async Task<JsonElement?> CompleteWithToolAsync(string systemPrompt, string userMessage,
        JsonElement[] tools, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
            },
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = userMessage } }
                }
            },
            ["tools"] = new JsonArray { ConvertToolsToGemini(tools) },
            ["tool_config"] = new JsonObject
            {
                ["function_calling_config"] = new JsonObject { ["mode"] = "ANY" }
            },
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 1024 }
        };

        var body = await PostAsync($"v1beta/models/{_model}:generateContent?key={_apiKey}", request, ct);
        var doc = JsonDocument.Parse(body);
        var parts = doc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts");

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var name = fc.GetProperty("name").GetString()!;
                var args = fc.TryGetProperty("args", out var a) ? a : JsonDocument.Parse("{}").RootElement;
                var wrapper = JsonDocument.Parse($"{{\"tool\":\"{name}\",\"input\":{args.GetRawText()}}}");
                return wrapper.RootElement;
            }
        }
        return null;
    }

    public async Task<(string toolName, JsonElement input, string toolUseId)?> SendMessageAsync(
        string systemPrompt, string userContent, JsonElement[] tools, CancellationToken ct = default)
    {
        // Build user turn — include function responses if pending
        if (_pendingToolResults.Count > 0)
        {
            var parts = new JsonArray();
            foreach (var (callId, name, result) in _pendingToolResults)
            {
                parts.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = name,
                        ["response"] = new JsonObject
                        {
                            ["result"] = result ?? userContent
                        }
                    }
                });
            }
            var userTurn = new JsonObject { ["role"] = "user", ["parts"] = parts };
            _contents.Add(userTurn);
            _fullLog.Add(JsonNode.Parse(userTurn.ToJsonString())!.AsObject());
            _pendingToolResults.Clear();
        }
        else
        {
            var userTurn = new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = userContent } }
            };
            _contents.Add(userTurn);
            _fullLog.Add(JsonNode.Parse(userTurn.ToJsonString())!.AsObject());
        }

        // Build request
        var contentsArray = new JsonArray();
        foreach (var c in _contents)
            contentsArray.Add(JsonNode.Parse(c.ToJsonString())!);

        var request = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
            },
            ["contents"] = contentsArray,
            ["tools"] = new JsonArray { ConvertToolsToGemini(tools) },
            ["tool_config"] = new JsonObject
            {
                ["function_calling_config"] = new JsonObject { ["mode"] = "ANY" }
            },
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 1024 }
        };

        var body = await PostAsync($"v1beta/models/{_model}:generateContent?key={_apiKey}", request, ct);
        var doc = JsonDocument.Parse(body);

        // Log tokens
        if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
        {
            var inTok = usage.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            var outTok = usage.TryGetProperty("candidatesTokenCount", out var cpt) ? cpt.GetInt32() : 0;
            Log.Info($"[AutoPlay/Gemini] Tokens: in={inTok} out={outTok}");
        }

        var content = doc.RootElement.GetProperty("candidates")[0].GetProperty("content");

        // Record model turn
        var modelTurn = new JsonObject
        {
            ["role"] = "model",
            ["parts"] = JsonNode.Parse(content.GetProperty("parts").GetRawText())!
        };
        _contents.Add(modelTurn);
        _fullLog.Add(JsonNode.Parse(modelTurn.ToJsonString())!.AsObject());

        // Parse function calls
        var allCalls = new List<(string name, JsonElement input, string id)>();
        foreach (var part in content.GetProperty("parts").EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var name = fc.GetProperty("name").GetString()!;
                var args = fc.TryGetProperty("args", out var a) ? a : JsonDocument.Parse("{}").RootElement;
                var id = $"gemini_call_{_callIdCounter++}";
                allCalls.Add((name, args, id));
            }
        }

        if (allCalls.Count == 0) return null;

        _pendingToolResults.Clear();
        ExtraToolUses.Clear();
        foreach (var call in allCalls)
            _pendingToolResults.Add((call.id, call.name, null));
        for (int i = 1; i < allCalls.Count; i++)
            ExtraToolUses.Add(allCalls[i]);

        return (allCalls[0].name, allCalls[0].input, allCalls[0].id);
    }

    public void SetPendingToolResult(string toolUseId, string result)
    {
        for (int i = 0; i < _pendingToolResults.Count; i++)
        {
            if (_pendingToolResults[i].callId == toolUseId)
            {
                _pendingToolResults[i] = (toolUseId, _pendingToolResults[i].name, result);
                return;
            }
        }
    }

    public void CleanupAfterInterruption()
    {
        _pendingToolResults.Clear();
        ExtraToolUses.Clear();
        // Remove trailing incomplete turns
        while (_contents.Count > 0)
        {
            var role = _contents[^1]["role"]?.ToString();
            if (role == "user")
            {
                _contents.RemoveAt(_contents.Count - 1);
                continue;
            }
            if (role == "model")
            {
                var json = _contents[^1].ToJsonString();
                if (json.Contains("functionCall"))
                {
                    _contents.RemoveAt(_contents.Count - 1);
                    continue;
                }
            }
            break;
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
            throw new HttpRequestException($"Gemini API error {response.StatusCode}: {body}");
        return body;
    }

    /// <summary>Convert Claude-format tool definitions to Gemini function declarations.</summary>
    private static JsonObject ConvertToolsToGemini(JsonElement[] tools)
    {
        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            var desc = tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            var fn = new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
            };

            if (tool.TryGetProperty("input_schema", out var schema))
            {
                // Gemini uses "parameters" with OpenAPI schema format
                fn["parameters"] = JsonNode.Parse(schema.GetRawText())!;
            }

            declarations.Add(fn);
        }
        return new JsonObject { ["functionDeclarations"] = declarations };
    }

    #endregion
}
