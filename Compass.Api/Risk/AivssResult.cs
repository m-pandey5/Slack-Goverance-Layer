namespace Compass.Api.Risk;

public sealed class AivssResult
{
    public required string ToolName { get; init; }
    public required ActionTier Tier { get; init; }
    public required double CvssBase { get; init; }
    public required double Aars { get; init; }
    public required double ThM { get; init; }
    public required double MitigationFactor { get; init; }
    public required double Score { get; init; }
    public required string Severity { get; init; }

    public static string ClassifySeverity(double score) => score switch
    {
        >= 9.0 => "CRITICAL",
        >= 7.0 => "HIGH",
        >= 4.0 => "MEDIUM",
        >= 1.0 => "LOW",
        _ => "NONE"
    };
}
