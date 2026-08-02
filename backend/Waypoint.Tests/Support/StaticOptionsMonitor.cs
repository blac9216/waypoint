using Microsoft.Extensions.Options;

namespace Waypoint.Tests.Support;

/// <summary>Minimal <see cref="IOptionsMonitor{TOptions}"/> for unit tests: a fixed value, no change notifications.</summary>
public sealed class StaticOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
{
	public TOptions CurrentValue { get; } = value;

	public TOptions Get(string? name)
	{
		return CurrentValue;
	}

	public IDisposable? OnChange(Action<TOptions, string?> listener)
	{
		return null;
	}
}
