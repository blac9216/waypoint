namespace Waypoint.Core.Logging;

/// <summary>
/// The log-scrubbing hook point required from the start by <c>docs/security.md</c>
/// control 1: "The logging pipeline maintains the set of secret values currently in
/// play ... and redacts every occurrence before any line reaches a sink." The API's
/// Serilog pipeline routes every rendered log line through the registered
/// implementation before it is written (console/file/Postgres sinks alike).
///
/// This scaffold registers <see cref="NoOpSecretRedactor"/> — the seam exists and is
/// wired end-to-end, but tracking live decrypted secret values and scrubbing them is
/// issue #6's job, once the credential store (ADR-0005) exists to decrypt from.
/// </summary>
public interface ISecretRedactor
{
	/// <summary>Returns <paramref name="line"/> with any known secret values replaced.</summary>
	string Redact(string line);
}

/// <summary>Placeholder redactor: returns input unchanged. Replaced by the real scrubber in issue #6.</summary>
public sealed class NoOpSecretRedactor : ISecretRedactor
{
	public string Redact(string line)
	{
		return line;
	}
}
