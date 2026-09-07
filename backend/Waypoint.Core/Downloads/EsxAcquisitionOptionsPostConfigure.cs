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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Derives <see cref="EsxAcquisitionOptions.VocabularyDocumentPath"/> from
/// <see cref="ManagedToolOptions.LocalRepositoryPath"/> and
/// <see cref="ManagedToolOptions.ProductVersionCatalogPath"/> when the caller has not
/// set it explicitly (issue #1602). Runs as an <see cref="IPostConfigureOptions{TOptions}"/>
/// step -- mirroring <c>Waypoint.Core.Auth.LocalAuthOptionsPostConfigure</c>'s shape --
/// so every consumer of <c>IOptions&lt;EsxAcquisitionOptions&gt;</c> sees one
/// already-resolved path and an operator who reconfigures
/// <c>ManagedTool:LocalRepositoryPath</c> for a different depot mount never has to
/// separately update <c>EsxAcquisition:VocabularyDocumentPath</c> to match.
/// </summary>
public sealed class EsxAcquisitionOptionsPostConfigure : IPostConfigureOptions<EsxAcquisitionOptions>
{
	private readonly IOptions<ManagedToolOptions> _managedToolOptions;

	public EsxAcquisitionOptionsPostConfigure(IOptions<ManagedToolOptions> managedToolOptions)
	{
		ArgumentNullException.ThrowIfNull(managedToolOptions);
		_managedToolOptions = managedToolOptions;
	}

	public void PostConfigure(string? name, EsxAcquisitionOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (!string.IsNullOrWhiteSpace(options.VocabularyDocumentPath))
		{
			// Explicit override -- leave it exactly as configured.
			return;
		}

		ManagedToolOptions managedTool = _managedToolOptions.Value;
		options.VocabularyDocumentPath = Path.Combine(managedTool.LocalRepositoryPath, managedTool.ProductVersionCatalogPath);
	}
}
