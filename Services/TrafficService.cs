using AnalyzeThis.Models;

namespace AnalyzeThis.Services;

public class TrafficService
{
    public event Action? OnTrafficReceived;
    private readonly List<TrafficEntry> _entries = new();
    private const int MaxEntries = 100;

    public IReadOnlyList<TrafficEntry> Entries => _entries.AsReadOnly();

    public void AddEntry(TrafficEntry entry)
    {
        lock (_entries)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
        NotifyUpdate();
    }

    public void NotifyUpdate() => OnTrafficReceived?.Invoke();
    
    // Stats extraction
    public int TotalRequests => _entries.Count;
    public double AverageLatency => _entries.Any() ? _entries.Average(e => e.LatencyMs) : 0;
    public int SuccessRate => _entries.Any() ? (int)(_entries.Count(e => e.StatusCode == 200) / (double)_entries.Count * 100) : 100;
}
