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

namespace Waypoint.Core.ComplianceContent.SemanticImport;

/// <summary>
/// One discovered <c>inspec.yml</c> under a vendor content checkout, before semantic
/// interpretation. <see cref="ProfileKey"/> is the content-root-relative directory path
/// (forward-slash normalized, mirroring the existing importer's collision-free
/// identity -- issue #617), <see cref="RawYaml"/> is the untrusted file text handed to
/// <see cref="InspecManifestParser"/>, and <see cref="HasControlsDirectory"/>/
/// <see cref="HasFilesDirectory"/> record structural facts
/// <see cref="VendorHierarchyInterpreter"/> needs for aggregate-vs-leaf and structure
/// validation without re-touching the filesystem.
/// </summary>
public sealed record VendorContentEntry(
	string ProfileKey,
	string? RawYaml,
	bool HasControlsDirectory,
	bool HasFilesDirectory,
	IReadOnlyList<string> ControlFileNames);
