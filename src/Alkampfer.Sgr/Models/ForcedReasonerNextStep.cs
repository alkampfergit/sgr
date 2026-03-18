using Newtonsoft.Json;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Alkampfer.Sgr.Models;

[Description("Next step to execute with a structured remaining plan")]
public sealed class ForcedReasonerNextStep
{
    [Description("Execute first remaining step")]
    public required ToolCall Function { get; set; }

    [Description("Current status of the workflow")]
    public required string CurrentState { get; set; }

    [Description("Brief list of remaining planned steps with their corresponding tool id")]
    public required List<ForcedReasonerPlanStep> PlanRemainingStepsBrief { get; set; }

    [Description("Indicates if the task is completed")]
    public bool TaskCompleted { get; set; } = false;
}

public sealed class ForcedReasonerPlanStep
{
    [JsonProperty("description")]
    [Required]
    [Description("Brief description of the planned step")]
    public required string Description { get; set; }

    [JsonProperty("toolid")]
    [Required]
    [Description("Tool discriminator to use for this planned step")]
    public required string ToolId { get; set; }
}
