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
    private const string OllamaHost = "http://localhost:11434";

    public ProxyMiddleware(RequestDelegate next, IHttpClientFactory httpClientFactory, TrafficService trafficService, SettingsService settingsService)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _trafficService = trafficService;
        _settingsService = settingsService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only proxy requests for Ollama (api/*)
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
        try
        {
            // 1. Capture and prepare request
            context.Request.EnableBuffering();
            var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            
            // --- DEEP MERGE POLICY ---
            var settings = _settingsService.Current;
            var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(body);
            if (jsonNode != null)
            {
                var options = jsonNode["options"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                jsonNode["options"] = options;

                // Inject defaults if NOT already present (Overridable)
                options["temperature"] ??= settings.Temperature;
                options["top_k"] ??= settings.TopK;
                
                // Special case for JSON format
                if (jsonNode["format"] == null && settings.EnableJsonMode)
                {
                    jsonNode["format"] = "json";
                }

                body = jsonNode.ToJsonString();
                entry.Model = jsonNode["model"]?.ToString() ?? "Unknown";
                entry.PromptText = jsonNode["prompt"]?.ToString() ?? "";
                
                // Save final tuning parameters for logging
                if (options["temperature"] is System.Text.Json.Nodes.JsonNode tempNode) 
                    entry.Temperature = tempNode.GetValue<double>();
                    
                if (options["top_k"] is System.Text.Json.Nodes.JsonNode topKNode) 
                    entry.TopK = topKNode.GetValue<int>();
                    
                entry.JsonMode = settings.EnableJsonMode;
            }
            entry.RequestPayload = body;
            _trafficService.NotifyUpdate(); // REFRESH UI WITH PAYLOAD

            // 2. Forward to Ollama
            var client = _httpClientFactory.CreateClient();
            var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), OllamaHost + context.Request.Path + context.Request.QueryString);
            
            if (!string.IsNullOrEmpty(body))
            {
                proxyRequest.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            var proxyResponse = await client.SendAsync(proxyRequest);
            entry.StatusCode = (int)proxyResponse.StatusCode;

            // 3. Capture response
            var responseContent = await proxyResponse.Content.ReadAsStringAsync();
            entry.ResponsePayload = responseContent;

            // Latency tracking
            sw.Stop();
            entry.LatencyMs = sw.ElapsedMilliseconds;

            // Simple tactical extraction
            try
            {
                using var respDoc = JsonDocument.Parse(responseContent);
                if (respDoc.RootElement.TryGetProperty("response", out var respProp))
                {
                    var innerResponse = respProp.GetString();
                    entry.ResponseText = innerResponse ?? "";
                    entry.ParsedContent = innerResponse ?? "";
                    
                    if (!string.IsNullOrEmpty(innerResponse))
                    {
                        if (innerResponse.Contains("FOE")) entry.ThreatLevel = "FOE";
                        else if (innerResponse.Contains("FRIEND")) entry.ThreatLevel = "FRIEND";
                    }
                }
            }
            catch { entry.ParsedContent = "No structured response received."; }

            // 4. Return to original client
            context.Response.StatusCode = (int)proxyResponse.StatusCode;
            context.Response.ContentType = proxyResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            await context.Response.WriteAsync(responseContent);
        }
        catch (Exception ex)
        {
            entry.StatusCode = 500;
            entry.ResponsePayload = $"Proxy Error: {ex.Message}";
            entry.ParsedContent = "ERROR";
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(entry.ResponsePayload);
        }
        finally
        {
            _trafficService.NotifyUpdate(); // FINAL REFRESH WITH RESULTS
        }
    }
}
