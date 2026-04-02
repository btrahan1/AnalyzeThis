namespace AnalyzeThis.Models;

public class TrafficEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    // Request metadata
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Model { get; set; } = "";
    public string RequestPayload { get; set; } = "";
    public string PromptText { get; set; } = "";
    
    // Tuning Parameters
    public double? Temperature { get; set; }
    public int? TopK { get; set; }
    public bool? JsonMode { get; set; }
    
    // Response metadata
    public string ResponsePayload { get; set; } = "";
    public string ResponseText { get; set; } = "";
    public int StatusCode { get; set; }
    public long LatencyMs { get; set; }
    public double LatencySeconds => LatencyMs / 1000.0;
    
    // Extraction (Tactical Intelligence)
    public string ParsedContent { get; set; } = "";
    public string ThreatLevel { get; set; } = ""; // Extra captured info
}
