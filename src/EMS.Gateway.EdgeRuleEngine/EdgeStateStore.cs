using System.Collections.Concurrent;
using EMS.Gateway.Contracts;

namespace EMS.Gateway.EdgeRuleEngine;

public class EdgeStateStore : IEdgeStateStore
{
    private readonly ConcurrentDictionary<string, double> _accumulators = new();
    private readonly ConcurrentDictionary<string, (double Value, DateTimeOffset Time)> _lastValues = new();
    private readonly ConcurrentDictionary<string, List<double>> _rollingWindows = new();

    public double Accumulate(string key, double value, double dtSeconds)
    {
        return _accumulators.AddOrUpdate(key, value * dtSeconds, (_, current) => current + (value * dtSeconds));
    }

    public double RollingAverage(string key, double value, double windowSeconds)
    {
        // Simplified window based on samples for now, real version would use time
        var window = _rollingWindows.GetOrAdd(key, _ => new List<double>());
        lock (window)
        {
            window.Add(value);
            if (window.Count > 10) window.RemoveAt(0);
            return window.Average();
        }
    }

    public double Rate(string key, double value, double dtSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var last = _lastValues.GetOrAdd(key, _ => (value, now));
        
        double rate = 0;
        if (dtSeconds > 0)
        {
            rate = (value - last.Value) / dtSeconds;
        }

        _lastValues[key] = (value, now);
        return rate;
    }

    public double ElapsedOn(string key, bool condition)
    {
        // Simplified implementation
        return 0;
    }

    public Task SaveAsync()
    {
        // TODO: Persist to disk
        return Task.CompletedTask;
    }

    public Task LoadAsync()
    {
        // TODO: Load from disk
        return Task.CompletedTask;
    }
}
