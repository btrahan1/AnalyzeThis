using AnalyzeThis.Models;
using System.Text.Json;

namespace AnalyzeThis.Services;

public class GlobalSettings
{
    public double Temperature { get; set; } = 0.7;
    public int TopK { get; set; } = 40;
    public bool EnableJsonMode { get; set; } = false;
}

public class SettingsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _registryPath = "ai_registry.json";

    public GlobalSettings Current { get; private set; } = new()
    {
        Temperature = 0.7,
        TopK = 40,
        EnableJsonMode = false
    };

    public string? SelectedModelId { get; set; }
    public List<AiModel> ModelRegistry { get; } = new();

    public SettingsService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        LoadRegistry();
    }

    public void SaveRegistry()
    {
        try
        {
            // Only save non-local models (Ollama is auto-discovered)
            var config = new
            {
                SelectedModelId = SelectedModelId,
                GlobalSettings = Current,
                EnterpriseModels = ModelRegistry.Where(m => !m.IsLocal).ToList()
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_registryPath, json);
        }
        catch (Exception ex)
        {
             Console.WriteLine($"Save Error: {ex.Message}");
        }
    }

    private void LoadRegistry()
    {
        try
        {
            if (File.Exists(_registryPath))
            {
                var json = File.ReadAllText(_registryPath);
                using var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("SelectedModelId", out var smid)) 
                    SelectedModelId = smid.GetString();

                if (doc.RootElement.TryGetProperty("GlobalSettings", out var gs))
                    Current = JsonSerializer.Deserialize<GlobalSettings>(gs.GetRawText()) ?? Current;

                if (doc.RootElement.TryGetProperty("EnterpriseModels", out var ems))
                {
                    var models = JsonSerializer.Deserialize<List<AiModel>>(ems.GetRawText());
                    if (models != null)
                    {
                        // Bulletproof Check: Strip any malformed Ollama models that were accidentally saved
                        var sanitized = models.Where(m => !m.IsLocal || m.BaseUrl.Contains("11434")).ToList();
                        ModelRegistry.AddRange(sanitized);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load Error: {ex.Message}");
        }
    }

    public async Task RefreshLocalModelsAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3); // Fast fail

            var response = await client.GetAsync("http://localhost:11434/api/tags");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var models = doc.RootElement.GetProperty("models");
                
                // Sacred Rule: Ollama models are ALWAYS auto-handled
                ModelRegistry.RemoveAll(m => m.IsLocal);
                
                foreach (var model in models.EnumerateArray())
                {
                    var name = model.GetProperty("name").GetString() ?? "Unknown";
                    ModelRegistry.Add(new AiModel
                    {
                        DisplayName = name,
                        ModelName = name,
                        Provider = AiProvider.Ollama,
                        BaseUrl = "http://localhost:11434" // Forcing Sacred Default
                    });
                }
                
                if (SelectedModelId == null && ModelRegistry.Any())
                {
                    SelectedModelId = ModelRegistry.First().Id;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ollama Discovery Error: {ex.Message} (Check if Ollama is running on 11434)");
        }
    }

    public async Task DiscoverRemoteModelsAsync(AiProvider provider, string baseUrl, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        try
        {
            var client = _httpClientFactory.CreateClient();
            string url = "";
            bool useHeader = true;

            if (provider == AiProvider.Gemini)
            {
                // Logic from GeminiVersionFinder: Pass key in URL for v1beta
                url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
                useHeader = false;
            }
            else
            {
                // Standard OpenAI-style discovery
                url = baseUrl.TrimEnd('/') + (baseUrl.EndsWith("/models") ? "" : "/models");
            }

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (useHeader)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                
                if (provider == AiProvider.Gemini && doc.RootElement.TryGetProperty("models", out var gModels))
                {
                    foreach (var m in gModels.EnumerateArray())
                    {
                        var name = m.GetProperty("name").GetString() ?? "";
                        var disp = m.TryGetProperty("displayName", out var d) ? d.GetString() : name;
                        
                        // Append to registry if not already there
                        if (!ModelRegistry.Any(r => r.ModelName == name))
                        {
                            ModelRegistry.Add(new AiModel {
                                DisplayName = disp ?? name,
                                ModelName = name,
                                Provider = provider,
                                BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/", // Use OpenAI-compatible base for routing
                                ApiKey = apiKey
                            });
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("data", out var oModels))
                {
                    foreach (var m in oModels.EnumerateArray())
                    {
                        var id = m.GetProperty("id").GetString() ?? "";
                        if (!ModelRegistry.Any(r => r.ModelName == id))
                        {
                            ModelRegistry.Add(new AiModel {
                                DisplayName = id,
                                ModelName = id,
                                Provider = provider,
                                BaseUrl = baseUrl,
                                ApiKey = apiKey
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Remote Discovery Error ({provider}): {ex.Message}");
        }
    }

    public void Update(GlobalSettings settings)
    {
        Current = settings;
    }
}
