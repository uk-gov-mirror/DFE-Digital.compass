using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Compass.Services.Raid;

/// <summary>Cross-field rules for residual vs tolerance ratings.</summary>
public static class RaidRiskScoreRules
{
    public const string ResidualAboveToleranceMessage =
        "The residual risk rating cannot be higher than the tolerance rating. Residual is the expected level after mitigations; tolerance is the maximum the organisation will accept. Reduce residual likelihood or impact, or raise the tolerance rating.";

    public static bool ResidualExceedsTolerance(decimal? residualScore, decimal? toleranceScore) =>
        residualScore.HasValue && toleranceScore.HasValue && residualScore.Value > toleranceScore.Value;

    public static void AddResidualAboveToleranceError(ModelStateDictionary modelState)
    {
        modelState.AddModelError(
            nameof(ModernRaidRiskEditorForm.ResidualLikelihoodId),
            ResidualAboveToleranceMessage);
    }
}
