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

namespace Waypoint.Core.Downloads;

/// <summary>
/// The result of parsing one raw VKS/VKR item name: the era the grammar matched (or
/// <see cref="VksNamingEras.Unparsed"/>), the corresponding
/// <see cref="VksParseStatuses"/> value, and the extracted dimensions (all-null on an
/// unparsed name -- <see cref="VksItemDimensions.Empty"/>). A parse never throws.
/// </summary>
public sealed record VksItemNameParseResult(string NamingEra, string ParseStatus, VksItemDimensions Dimensions)
{
	public static VksItemNameParseResult Unparsed { get; } = new(VksNamingEras.Unparsed, VksParseStatuses.Unparsed, VksItemDimensions.Empty);
}

/// <summary>
/// Parses a raw VKS/VKR item name into <see cref="VksItemNameParseResult"/> using the
/// single grammar research #1031 Layer B found parses 138/138 live public library
/// items across four coexisting naming eras:
/// <c>ob-&lt;buildId&gt;-[tkgs-ova-]&lt;distro&gt;-&lt;distroVersion&gt;[-&lt;arch&gt;][-vmi-k8s|-k8s]
/// -v&lt;k8sVersion&gt;---vmware.&lt;n&gt;[-fips][.&lt;n&gt;]-(vkr|tkg).&lt;n&gt;[.&lt;suffix&gt;]</c>.
/// An unrecognized name never throws and never drops the item -- it returns
/// <see cref="VksItemNameParseResult.Unparsed"/> so the caller can store the row with
/// <c>parse_status = 'unparsed'</c> and log/flag it for visibility (#1031 Risk).
/// </summary>
public interface IVksItemNameGrammarParser
{
	VksItemNameParseResult Parse(string rawName);
}
