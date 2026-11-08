using JERCPLD.Core;

namespace JERCPLD.VersionMap;

public sealed class VersionMapRegistry
{
    private readonly Dictionary<int, IVersionStarAdapter> _adapters = new();
    private readonly Dictionary<int, IAdjacentVersionStep> _steps = new();

    public IReadOnlyCollection<IVersionStarAdapter> Adapters => _adapters.Values;

    public IReadOnlyCollection<IAdjacentVersionStep> Steps => _steps.Values;

    public void Register(IVersionStarAdapter adapter)
    {
        if (!_adapters.TryAdd(adapter.Version.Number, adapter))
        {
            throw new InvalidOperationException($"协议版本 {adapter.Version.Number} 的星型适配器已注册");
        }
    }

    public void Register(IAdjacentVersionStep step)
    {
        if (step.To.Number != step.From.Number + 1)
        {
            throw new ArgumentException(
                $"相邻版本步骤必须从 N 指向 N+1：{step.From.Number} -> {step.To.Number}");
        }

        if (!_steps.TryAdd(step.From.Number, step))
        {
            throw new InvalidOperationException($"相邻版本步骤 {step.From.Number}->{step.To.Number} 已注册");
        }
    }

    public IVersionStarAdapter? GetAdapter(int versionNumber)
        => _adapters.TryGetValue(versionNumber, out var adapter) ? adapter : null;

    public IAdjacentVersionStep? GetStep(int fromVersionNumber)
        => _steps.TryGetValue(fromVersionNumber, out var step) ? step : null;

    public void RegisterAll(IEnumerable<IVersionStarAdapter> adapters)
    {
        foreach (var adapter in adapters)
        {
            Register(adapter);
        }
    }

    public void RegisterAll(IEnumerable<IAdjacentVersionStep> steps)
    {
        foreach (var step in steps)
        {
            Register(step);
        }
    }
}
