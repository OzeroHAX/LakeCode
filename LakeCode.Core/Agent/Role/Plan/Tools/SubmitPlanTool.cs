using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Role.Plan.Tools;

internal sealed class SubmitPlanTool(Func<string, ImplementationPlan?> savePlan)
{
    public bool Submitted { get; private set; }
    public void BeginTurn() => Submitted = false;
    public AIFunction AgentTool() => AIFunctionFactory.Create(Submit, name: "submit_plan");

    [Description("Submit the final self-contained Markdown implementation plan for user review. Does not grant execution permission. Ends this planning turn.")]
    private string Submit(
        [Description("Complete actionable plan following the Plan system prompt template, with no unresolved blocking decisions.")] string markdown)
    {
        if (Submitted) return "Error: Plan already submitted; turn is complete.";
        if (string.IsNullOrWhiteSpace(markdown)) return "Error: A nonempty plan is required.";
        var plan = savePlan(markdown);
        if (plan is null) return "Error: Plan submission is unavailable.";
        Submitted = true;
        return $"Plan {plan.Id}, revision {plan.Revision} submitted for review. No implementation authorized.";
    }
}
