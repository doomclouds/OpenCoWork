using System.ComponentModel.DataAnnotations;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Configuration;

[ConfigSection("tools")]
public sealed record ToolsConfig : IValidatableObject
{
    [Required]
    public ToolEffectPoliciesConfig Effects { get; init; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Effects.ExternalMutation == ToolAuthorityDecision.Allow)
        {
            yield return new ValidationResult(
                "ExternalMutation cannot be configured as Allow.",
                [$"{nameof(Effects)}.{nameof(Effects.ExternalMutation)}"]);
        }
    }
}

public sealed record ToolEffectPoliciesConfig
{
    public ToolAuthorityDecision NetworkRead { get; init; } =
        ToolAuthorityDecision.RequireApproval;

    public ToolAuthorityDecision WorkspaceWrite { get; init; } =
        ToolAuthorityDecision.RequireApproval;

    public ToolAuthorityDecision ProcessExecution { get; init; } =
        ToolAuthorityDecision.RequireApproval;

    public ToolAuthorityDecision ExternalMutation { get; init; } =
        ToolAuthorityDecision.RequireApproval;
}
