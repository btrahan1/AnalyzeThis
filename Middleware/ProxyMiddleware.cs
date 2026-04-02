using System.Diagnostics;
using System.Text.Json;
using AnalyzeThis.Models;
using AnalyzeThis.Services;
using System.Text.Json.Nodes;

namespace AnalyzeThis.Middleware;

public class ProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrafficService _trafficService;
    private readonly SettingsService _settingsService;
    public ProxyMiddleware(RequestDelegate next, IHttpClientFactory httpClientFactory, TrafficService trafficService, SettingsService settingsService)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _trafficService = trafficService;
        _settingsService = settingsService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only proxy requests for api/*
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var entry = new TrafficEntry
        {
            Method = context.Request.Method,
            Path = context.Request.Path,
            ParsedContent = "Thinking..." // Initial state
        };
        _trafficService.AddEntry(entry); // LOG IMMEDIATELY

        var sw = Stopwatch.StartNew();
        string targetUrl = "http://localhost:11434/api/generate"; // Default Diagnostic Placeholder
        try
        {
            // 1. Capture and prepare request
            context.Request.EnableBuffering();
            var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            
            // --- DEEP MERGE POLICY & ROUTING ---
            var settings = _settingsService.Current;
            var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(body);
            
            string targetBaseUrl = "http://localhost:11434"; // Default
            string? apiKey = null;
            AiModel? registryModel = null;

            if (jsonNode != null)
            {
                var requestedModel = jsonNode["model"]?.ToString();
                var originalPath = context.Request.Path.ToString();
                
                // 1. Resolve 'Master Override' or 'Match'
                if (!string.IsNullOrEmpty(_settingsService.SelectedModelId))
                {
                    registryModel = _settingsService.ModelRegistry.FirstOrDefault(m => m.Id == _settingsService.SelectedModelId);
                }

                if (registryModel == null)
                {
                    registryModel = _settingsService.ModelRegistry.FirstOrDefault(m => m.ModelName == requestedModel || m.DisplayName == requestedModel);
                }

                if (registryModel != null)
                {
                    targetBaseUrl = registryModel.BaseUrl;
                    apiKey = registryModel.ApiKey;
                    jsonNode["model"] = registryModel.ModelName; 
                    entry.Model = registryModel.DisplayName; 
                }
                else
                {
                    targetBaseUrl = "http://localhost:11434";
                    entry.Model = $"{jsonNode["model"]?.ToString() ?? "Unknown"} (Fallback)";
                }

                // --- UNIVERSAL DIALECT TRANSLATION ---
                if (registryModel != null && !registryModel.IsLocal)
                {
                    // Path Translation (Ollama -> OpenAI)
                    var cloudPath = "/chat/completions"; 
                    if (targetBaseUrl.Contains("openai") || targetBaseUrl.Contains("google") || targetBaseUrl.Contains("xai"))
                    {
                         targetUrl = targetBaseUrl.TrimEnd('/') + cloudPath;
                    }
                    else 
                    {
                         targetUrl = targetBaseUrl.TrimEnd('/') + originalPath + context.Request.QueryString;
                    }

                    // Payload Translation (Ollama -> OpenAI Chat)
                    var messages = new System.Text.Json.Nodes.JsonArray();
                    
                    // 2a. Handle Client-Side SYSTEM Instruction (Ollama 'system' field)
                    var systemInstruction = jsonNode["system"]?.ToString();
                    if (!string.IsNullOrEmpty(systemInstruction))
                    {
                        messages.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = systemInstruction });
                        ((System.Text.Json.Nodes.JsonObject)jsonNode).Remove("system");
                    }

                    // 2b. Handle User PROMPT
                    var prompt = jsonNode["prompt"]?.ToString();
                    if (!string.IsNullOrEmpty(prompt) && jsonNode["messages"] == null)
                    {
                        messages.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = prompt });
                        jsonNode["messages"] = messages;
                        ((System.Text.Json.Nodes.JsonObject)jsonNode).Remove("prompt");
                    }
                    else if (jsonNode["messages"] is System.Text.Json.Nodes.JsonArray existingMessages)
                    {
                         // If we added a system message, we need to prepend it to existing messages
                         if (messages.Count > 0)
                         {
                             // Insert system role at the start
                             existingMessages.Insert(0, messages[0]?.DeepClone());
                         }
                    }
                    else if (messages.Count > 0)
                    {
                         jsonNode["messages"] = messages;
                    }

                    // Map Options -> Root Properties
                    var options = jsonNode["options"] as System.Text.Json.Nodes.JsonObject;
                    if (options != null)
                    {
                        jsonNode["temperature"] = options["temperature"]?.DeepClone() ?? settings.Temperature;
                        jsonNode["top_k"] = options["top_k"]?.DeepClone() ?? settings.TopK;
                        jsonNode["top_p"] = options["top_p"]?.DeepClone() ?? 0.95; // Sensible cloud default
                    }
                    else
                    {
                        jsonNode["temperature"] = settings.Temperature;
                        jsonNode["top_k"] = settings.TopK;
                        jsonNode["top_p"] = 0.95;
                    }

                    // Strategic Cleanup: Remove all Ollama-specific fields
                    var root = (System.Text.Json.Nodes.JsonObject)jsonNode;
                    root.Remove("options");
                    root.Remove("format");
                    root.Remove("stream"); 

                    if (settings.EnableJsonMode)
                    {
                        jsonNode["response_format"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "json_object" };
                        
                        // Force JSON Instruction into system prompt
                        if (jsonNode["messages"] is System.Text.Json.Nodes.JsonArray msgs)
                        {
                             var jsonDirective = "IMPORTANT: Your response MUST be a valid JSON object.";
                             var alreadyHasDirective = msgs.Any(m => m?["content"]?.ToString()?.Contains("JSON", StringComparison.OrdinalIgnoreCase) == true);
                             if (!alreadyHasDirective)
                             {
                                 msgs.Insert(0, new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = jsonDirective });
                             }
                        }
                    }
                }
                else 
                {
                    targetUrl = targetBaseUrl.TrimEnd('/') + originalPath + context.Request.QueryString;
                    
                    var options = jsonNode["options"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                    jsonNode["options"] = options;
                    options["temperature"] ??= settings.Temperature;
                    options["top_k"] ??= settings.TopK;
                    if (settings.EnableJsonMode) jsonNode["format"] = "json";
                }

                body = jsonNode.ToJsonString();
                var promptVal = jsonNode["prompt"]?.ToString();
                var msgVal = jsonNode["messages"] is System.Text.Json.Nodes.JsonArray arr ? arr[0]?["content"]?.ToString() : null;
                entry.PromptText = promptVal ?? msgVal ?? "";
                
                // --- ACCURATE DIAGNOSTIC LOGGING ---
                // Extract Temp/Top-K for display regardless of dialect (Ollama or Cloud)
                var optionsObj = jsonNode["options"] as System.Text.Json.Nodes.JsonObject;
                
                if (jsonNode["temperature"] != null) entry.Temperature = jsonNode["temperature"].GetValue<double>();
                else if (optionsObj?["temperature"] != null) entry.Temperature = optionsObj["temperature"]?.GetValue<double>();
                
                if (jsonNode["top_k"] != null) entry.TopK = jsonNode["top_k"].GetValue<int>();
                else if (optionsObj?["top_k"] != null) entry.TopK = optionsObj["top_k"]?.GetValue<int>();
                
                entry.JsonMode = settings.EnableJsonMode;
            }
            
            entry.RequestPayload = body;
            _trafficService.NotifyUpdate();

            // 2. Forward to Target
            var client = _httpClientFactory.CreateClient();
            var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);
            if (!string.IsNullOrEmpty(apiKey))
            {
                proxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            if (!string.IsNullOrEmpty(body))
            {
                proxyRequest.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var proxyResponse = await client.SendAsync(proxyRequest);
            entry.StatusCode = (int)proxyResponse.StatusCode;

            // 3. Capture & Cross-Translate Response
            var responseContent = await proxyResponse.Content.ReadAsStringAsync();
            entry.ResponsePayload = responseContent;

            sw.Stop();
            entry.LatencyMs = sw.ElapsedMilliseconds;

            string finalParsedResponse = "";
            try
            {
                using var respDoc = JsonDocument.Parse(responseContent);
                if (respDoc.RootElement.TryGetProperty("response", out var respProp))
                {
                    finalParsedResponse = respProp.GetString() ?? "";
                }
                else if (respDoc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var msg))
                    {
                        finalParsedResponse = msg.GetProperty("content").GetString() ?? "";
                    }
                }
                
                entry.ResponseText = finalParsedResponse;
                entry.ParsedContent = finalParsedResponse;

                // --- RESPONSE TRANSLATION (OpenAI -> Ollama) ---
                // If the client expects 'response' (Ollama style) but we got OpenAI style,
                // we wrap it so the local chat app doesn't break.
                if (registryModel != null && !registryModel.IsLocal)
                {
                    var responseWrapper = new { response = finalParsedResponse, model = entry.Model, done = true };
                    responseContent = JsonSerializer.Serialize(responseWrapper);
                }
            }
            catch { entry.ParsedContent = "Parsed via raw proxy fallback."; }

            // 4. Return to client
            context.Response.StatusCode = (int)proxyResponse.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(responseContent);
        }
        catch (Exception ex)
        {
            entry.StatusCode = 500;
            entry.ResponsePayload = $"Proxy Error: Could not reach {targetUrl}. {ex.Message}";
            entry.ParsedContent = "ERROR";
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(entry.ResponsePayload);
        }
        finally
        {
            _trafficService.NotifyUpdate();
        }
    }
}
