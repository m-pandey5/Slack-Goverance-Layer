using Compass.Api.Agents;

namespace Compass.Api.Risk;

/// <summary>
/// OWASP AIVSS formula: Score = ((CVSS_Base + AARS) / 2) × ThM × MitigationFactor
/// AARS = sum of 10 agentic amplification factors (each 0 / 0.5 / 1.0, max 10.0)
/// </summary>
public sealed class AivssScorer
{
    // Tool prefix → (ActionTier, CVSS base score)
    private static readonly Dictionary<string, (ActionTier Tier, double Cvss)> ToolMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // READ_ONLY — CVSS 2.0
            ["conversations.history"]    = (ActionTier.ReadOnly, 2.0),
            ["conversations.list"]       = (ActionTier.ReadOnly, 2.0),
            ["channels.list"]            = (ActionTier.ReadOnly, 2.0),
            ["users.list"]               = (ActionTier.ReadOnly, 2.0),
            ["search_messages"]          = (ActionTier.ReadOnly, 2.5),
            ["files.list"]               = (ActionTier.ReadOnly, 2.0),
            ["team.info"]                = (ActionTier.ReadOnly, 2.0),

            // WRITE_BENIGN — CVSS 4.0
            ["chat.postMessage"]         = (ActionTier.WriteBenign, 4.0),
            ["reactions.add"]            = (ActionTier.WriteBenign, 3.5),
            ["pins.add"]                 = (ActionTier.WriteBenign, 3.5),
            ["bookmarks.add"]            = (ActionTier.WriteBenign, 3.5),

            // WRITE_SENSITIVE — CVSS 6.0
            ["conversations.create"]     = (ActionTier.WriteSensitive, 6.0),
            ["conversations.invite"]     = (ActionTier.WriteSensitive, 6.0),
            ["conversations.archive"]    = (ActionTier.WriteSensitive, 6.5),
            ["conversations.kick"]       = (ActionTier.WriteSensitive, 6.5),
            ["conversations.rename"]     = (ActionTier.WriteSensitive, 5.5),
            ["dnd.setSnooze"]            = (ActionTier.WriteSensitive, 5.0),

            // DATA_EXFILTRATION — CVSS 7.5
            ["files.getUploadURLExternal"] = (ActionTier.DataExfiltration, 7.5),
            ["files.upload"]             = (ActionTier.DataExfiltration, 7.0),

            // PRIVILEGE_ESCALATION — CVSS 8.5
            ["admin.users.invite"]       = (ActionTier.PrivilegeEscalation, 8.5),
            ["admin.users.setAdmin"]     = (ActionTier.PrivilegeEscalation, 9.0),
            ["admin.teams.create"]       = (ActionTier.PrivilegeEscalation, 8.0),
            ["admin.apps.approve"]       = (ActionTier.PrivilegeEscalation, 8.5),

            // DESTRUCTIVE_DATA — CVSS 9.0
            ["files.delete"]             = (ActionTier.DestructiveData, 9.0),
            ["conversations.close"]      = (ActionTier.DestructiveData, 8.0),
            ["chat.delete"]              = (ActionTier.DestructiveData, 7.5),
        };

    public AivssResult Score(string toolName, AgentRecord? agent, bool isMultiAgentCall = false)
    {
        var (tier, cvssBase) = ClassifyTool(toolName);
        var aars = ComputeAars(agent, tier, isMultiAgentCall);

        // ThM — Threat Model multiplier based on Ring
        var thm = (agent?.Ring ?? 2) switch
        {
            0 => 0.5,   // Core — internal, hardened
            1 => 0.75,  // Trusted
            2 => 1.0,   // Standard
            3 => 1.25,  // Untrusted/external
            _ => 1.0
        };

        // MitigationFactor — reduced if agent has explicit allow-list
        var hasAllowList = agent?.AllowedTools.Count > 0;
        var mitigationFactor = hasAllowList ? 0.9 : 1.0;

        var raw = (cvssBase + aars) / 2.0 * thm * mitigationFactor;
        var score = Math.Round(Math.Min(raw, 10.0), 2);

        return new AivssResult
        {
            ToolName = toolName,
            Tier = tier,
            CvssBase = cvssBase,
            Aars = aars,
            ThM = thm,
            MitigationFactor = mitigationFactor,
            Score = score,
            Severity = AivssResult.ClassifySeverity(score)
        };
    }

    private static (ActionTier Tier, double Cvss) ClassifyTool(string toolName)
    {
        if (ToolMap.TryGetValue(toolName, out var exact))
        {
            return exact;
        }

        // Prefix match — e.g. "admin.*" → PRIVILEGE_ESCALATION
        foreach (var (key, value) in ToolMap)
        {
            if (toolName.StartsWith(key.Split('.')[0] + ".", StringComparison.OrdinalIgnoreCase) &&
                key.Contains("admin", StringComparison.OrdinalIgnoreCase))
            {
                return (ActionTier.PrivilegeEscalation, 8.5);
            }
        }

        return (ActionTier.WriteBenign, 4.0);
    }

    private static double ComputeAars(AgentRecord? agent, ActionTier tier, bool isMultiAgent)
    {
        // 10 AARS factors, each 0 / 0.5 / 1.0
        double score = 0;

        // 1. Autonomy — ring 3 agents are highly autonomous
        score += (agent?.Ring ?? 2) >= 3 ? 1.0 : (agent?.Ring ?? 2) == 2 ? 0.5 : 0.0;

        // 2. Multi-agent amplification
        score += isMultiAgent ? 1.0 : 0.0;

        // 3. Non-determinism — write/destructive calls are non-deterministic
        score += tier >= ActionTier.WriteSensitive ? 0.5 : 0.0;

        // 4. Self-modification — privilege escalation implies self-modification risk
        score += tier == ActionTier.PrivilegeEscalation ? 1.0 : 0.0;

        // 5. Memory persistence — agents with no allowlist may persist broad access
        score += (agent?.AllowedTools.Count ?? 0) == 0 ? 0.5 : 0.0;

        // 6. Tool access breadth — low trust score → broad tool use assumed
        score += (agent?.TrustScore ?? 500) < 300 ? 1.0 : (agent?.TrustScore ?? 500) < 500 ? 0.5 : 0.0;

        // 7. Data exfiltration risk
        score += tier == ActionTier.DataExfiltration ? 1.0 : 0.0;

        // 8. Blast radius — destructive calls affect many users
        score += tier == ActionTier.DestructiveData ? 1.0 : tier >= ActionTier.WriteSensitive ? 0.5 : 0.0;

        // 9. Reversibility — destructive / archive actions are irreversible
        score += tier >= ActionTier.DestructiveData ? 1.0 : tier >= ActionTier.WriteSensitive ? 0.5 : 0.0;

        // 10. Identity impersonation — anonymous agents get max score
        score += agent is null ? 1.0 : 0.0;

        return Math.Round(score, 1);
    }
}
