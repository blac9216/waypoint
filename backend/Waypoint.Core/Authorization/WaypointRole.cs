namespace Waypoint.Core.Authorization;

/// <summary>
/// Application roles, per <c>docs/domain-model.md</c> "Roles". Each role is a strict
/// superset of the one before it (Cyber = Viewer + scan-initiation, Operator = Cyber +
/// ad hoc/download, Admin = everything), so the underlying numeric value is an ordinal
/// used for "this role or higher" checks, not an independent bitmask.
/// </summary>
public enum WaypointRole
{
	/// <summary>Read-only: dashboards, runs, results.</summary>
	Viewer = 0,

	/// <summary>Viewer + initiate scans with service credentials, export results, full audit history.</summary>
	Cyber = 1,

	/// <summary>Cyber + ad hoc scans with personal credentials + download/catalog/content-library management.</summary>
	Operator = 2,

	/// <summary>Everything: sites, targets, shared credentials, users/roles, STIG config, remediation, updates, transfer.</summary>
	Admin = 3
}
