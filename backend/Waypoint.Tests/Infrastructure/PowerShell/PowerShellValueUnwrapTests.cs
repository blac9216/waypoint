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

using System.Management.Automation;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #976: unit-level coverage for <see cref="PowerShellValueUnwrap"/>'s
/// value-type API, <see cref="PowerShellValueUnwrap.UnwrapAsStruct{T}"/> -- the sibling
/// to <see cref="PowerShellValueUnwrap.UnwrapAs{T}"/> for a <c>struct</c>-constrained
/// <typeparamref name="T"/> (bool/int/etc.), which <c>UnwrapAs</c>'s <c>where T : class</c>
/// constraint cannot express. These are hand-built <see cref="PSNoteProperty"/>/
/// <see cref="PSObject"/> fixtures (unit-level, not a real executor round-trip) --
/// the real-executor boundary-fidelity coverage lives in
/// <c>PowerShellExecutorTests.NestedBoundaryShape_RoundTripsWithFullFidelity_ThroughTheRealExecutor</c>.
/// </summary>
public sealed class PowerShellValueUnwrapTests
{
	[Fact]
	public void UnwrapAsStruct_WrappedBool_ReturnsTheBoolValue()
	{
		PSObject wrapped = PSObject.AsPSObject(true);

		Assert.True(PowerShellValueUnwrap.UnwrapAsStruct<bool>(wrapped));
	}

	[Fact]
	public void UnwrapAsStruct_WrappedInt_ReturnsTheIntValue()
	{
		PSObject wrapped = PSObject.AsPSObject(42);

		Assert.Equal(42, PowerShellValueUnwrap.UnwrapAsStruct<int>(wrapped));
	}

	[Fact]
	public void UnwrapAsStruct_UnwrappedBool_PassesThroughUnchanged()
	{
		// Idempotent on a value that was never PSObject-wrapped -- a hand-authored C#
		// fixture, matching UnwrapAs<T>'s documented convention.
		Assert.True(PowerShellValueUnwrap.UnwrapAsStruct<bool>(true));
	}

	[Fact]
	public void UnwrapAsStruct_GenuineNull_ReturnsNull()
	{
		Assert.Null(PowerShellValueUnwrap.UnwrapAsStruct<bool>(null));
	}

	[Fact]
	public void UnwrapAsStruct_TypeMismatch_ReturnsNullRatherThanThrowing()
	{
		// A string wrapped value read as bool: matches UnwrapAs<T>'s "malformed/mismatched
		// field degrades to null, never throws" convention.
		PSObject wrapped = PSObject.AsPSObject("not-a-bool");

		Assert.Null(PowerShellValueUnwrap.UnwrapAsStruct<bool>(wrapped));
	}

	[Fact]
	public void UnwrapAsStruct_PSCustomObjectSentinel_ReturnsNullRatherThanThrowing()
	{
		// The PSCustomObject sentinel carries no CLR-visible data of its own (see
		// Unwrap's doc comment) -- reading it as a struct type must degrade to null,
		// not throw, same as any other mismatch.
		PSObject custom = new();
		custom.Properties.Add(new PSNoteProperty("Name", "value"));
		Assert.IsType<PSCustomObject>(custom.BaseObject);

		Assert.Null(PowerShellValueUnwrap.UnwrapAsStruct<int>(custom));
	}

	[Fact]
	public void UnwrapAsStruct_NullableBoolWrappingNull_ReturnsNull()
	{
		// A property whose .Value genuinely holds a null bool? (rather than the property
		// being absent) -- Unwrap must not throw converting a boxed null.
		Assert.Null(PowerShellValueUnwrap.UnwrapAsStruct<bool>((object?)null));
	}
}
