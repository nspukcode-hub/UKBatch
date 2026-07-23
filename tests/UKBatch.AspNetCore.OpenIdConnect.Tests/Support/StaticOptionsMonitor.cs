using Microsoft.Extensions.Options;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Support;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{TOptions}"/> over a fixed value for unit tests that need to
/// hand a component its options without spinning up the options infrastructure.
/// </summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
