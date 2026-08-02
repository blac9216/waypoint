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

using Serilog.Events;
using Serilog.Formatting;
using Waypoint.Core.Logging;

namespace Waypoint.Api.Logging;

/// <summary>
/// Wraps another <see cref="ITextFormatter"/> and routes its fully-rendered output
/// through <see cref="ISecretRedactor"/> before it reaches the sink — the log-scrubbing
/// hook point <c>docs/security.md</c> control 1 requires from the start. Formatting
/// happens first so redaction sees the same text a human or a downstream log shipper
/// would, structured fields included.
/// </summary>
public sealed class RedactingTextFormatter : ITextFormatter
{
	private readonly ITextFormatter _inner;
	private readonly ISecretRedactor _redactor;

	public RedactingTextFormatter(ITextFormatter inner, ISecretRedactor redactor)
	{
		_inner = inner;
		_redactor = redactor;
	}

	public void Format(LogEvent logEvent, TextWriter output)
	{
		StringWriter buffer = new();
		_inner.Format(logEvent, buffer);
		output.Write(_redactor.Redact(buffer.ToString()));
	}
}
