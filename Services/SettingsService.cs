namespace AnalyzeThis.Services;

public class GlobalSettings
{
    public double Temperature { get; set; } = 0.7;
    public int TopK { get; set; } = 40;
    public bool EnableJsonMode { get; set; } = false;
}

public class SettingsService
{
    public GlobalSettings Current { get; private set; } = new();

    public void Update(GlobalSettings settings)
    {
        Current = settings;
    }
}
