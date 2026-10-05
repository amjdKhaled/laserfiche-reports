using LaserficheReports.Application.DTOs;
namespace LaserficheReports.Infrastructure.Repository;

/// <summary>Async-flow override for background work. Never changes any browser's repository.</summary>
public sealed class RepositoryExecutionContext
{
    private readonly AsyncLocal<RepositoryDescriptor?> _current = new();
    public RepositoryDescriptor? Current => _current.Value;
    public IDisposable Enter(RepositoryDescriptor repository)
    {
        var previous = _current.Value;
        _current.Value = repository;
        return new Restore(() => _current.Value = previous);
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
