// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

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
