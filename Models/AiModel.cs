namespace AnalyzeThis.Models;

public enum AiProvider
{
    Ollama,
    OpenAI,
    Gemini,
    Azure,
    Grok
}

public class AiModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = "";
    public string ModelName { get; set; } = ""; // The actual string passed to the API (e.g. gpt-4o)
    public AiProvider Provider { get; set; } = AiProvider.Ollama;
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ApiKey { get; set; } = "";
    
    public bool IsLocal => Provider == AiProvider.Ollama;
}
