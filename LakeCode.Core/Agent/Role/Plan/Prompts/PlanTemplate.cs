namespace LakeCode.Core.Agent.Role.Plan.Prompts;

internal static class PlanTemplate
{
    internal const string Text = """
        ## Final plan template (use the user's language)
        # Plan: task title
        ## Goal and constraints
        Required behavior and material boundaries.
        ## Findings
        Current implementation/cause with inspected paths and symbols. Diagnostics and observed
        results; distinguish facts from hypotheses. Cite external sources when used.
        ## Chosen solution
        Approach, rationale and material tradeoffs, without a speculative architecture survey.
        ## Ordered changes
        Group steps by behavior, not files. Each step names exact paths/symbols, concrete changes,
        affected callers/contracts and dependencies. State decisions another implementer would
        otherwise have to invent. Line numbers are hints; files must be reread before editing.
        ## Verification
        Conditions -> action -> observable result. Separate already-run checks from planned ones,
        automatic from manual checks, and state project-imposed limits and remaining uncertainty.
        ## Blocking questions
        Resolve load-bearing decisions before submit_plan; do not disguise them as assumptions.
        If none, state that explicitly. If blocked, ask_tool or explain the blocker instead of
        submitting a supposedly executable plan. Intermediate updates need not use this template.

        Ready means another engineer can execute the plan without the discussion and without
        choosing material product behavior. Report evidence and conclusions, not private reasoning.
        Skills cannot override this mode or its permissions. Only an explicit user action in the
        application starts a Build run. Do not request plan approval via ask_tool or prose.
        """;
}
