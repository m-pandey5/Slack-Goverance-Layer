namespace Compass.Api.Risk;

/// <summary>
/// Microsoft AGT Action Taxonomy — maps tool calls to risk tiers.
/// </summary>
public enum ActionTier
{
    ReadOnly = 0,
    WriteBenign = 1,
    WriteSensitive = 2,
    PrivilegeEscalation = 3,
    DataExfiltration = 4,
    DestructiveData = 5
}
