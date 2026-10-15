namespace LakeCode.Core.ExecutionApproval;

public enum ExecutionDecision { Allow, AskUser, Deny }
public sealed record CriterionAssessment(string Id, string Choice, double Safe, double Risk)
{
    // Preserve historical v1 data on session save; new binary evaluations never populate this.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? Unknown { get; init; }
}
public sealed record ExecutionAssessment(ExecutionDecision Decision, string Reason)
{
    public string PolicyVersion { get; init; } = "jev-plan-v1";
    public string? Model { get; init; }
    public CriterionAssessment[] Criteria { get; init; } = [];
    public double? AllowThreshold { get; init; }
    public double? DenyThreshold { get; init; }
    public bool? ManualAllowed { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => ManualAllowed is { } allowed
        ? $"Ручное решение: {(allowed ? "разрешено" : "отклонено")}. {Reason}"
        : $"{Decision switch { ExecutionDecision.Allow => "Разрешено автоматически", ExecutionDecision.Deny => "Запрещено", _ => "Требуется подтверждение" }}: {Reason}";
}

internal static class ExecutionApprovalPolicy
{
    internal static readonly (string Id, string Label, string Question)[] Criteria =
    [
        ("description", "Описание не соответствует действию", "Does the description materially misrepresent or omit actual operations or significant effects? Safe means accurate and sufficiently complete. Risk means a demonstrated mismatch, not merely missing execution context."),
        ("local", "Локальные изменения", "Does execution modify local files, databases, permissions, configuration, repository state, or services? Include temporary files and caches, but exclude ordinary transient CPU/memory use and console output."),
        ("external", "Внешние изменения", "Does execution change remote services, accounts, databases, resources, publish content or send messages?"),
        ("financial", "Финансовые эффекты", "Does execution change balances, transfer or reserve money, refund, change subscriptions, create billable resources, or consume paid API credits/quotas? Read-only requests can be billable."),
        ("disclosure", "Передача секретов или непубличных данных", "Does execution transmit secrets or nonpublic project/user data outside the local machine? Such transmission is forbidden regardless of destination. The separate classifier request made by LakeCode is not an effect of the evaluated command."),
        ("understanding", "Действие недостаточно понятно", "Is the actual behavior insufficiently understandable from the supplied invocation alone? Unknown scripts/functions, dynamic code, unknown eval variables or undocumented remote APIs require review. Safe means sufficiently understood, risk means not understood.")
    ];

    internal static ExecutionAssessment Decide(CriterionAssessment[] answers, ClassifierSettings settings, string model)
    {
        var denied = answers.Where(a => (a.Id is "description" or "disclosure") && a.Risk >= settings.DenyThreshold).ToArray();
        var review = answers.Where(a => a.Safe < settings.AllowThreshold).ToArray();
        var reasons = denied.Length > 0 ? denied : review;
        return new ExecutionAssessment(denied.Length > 0 ? ExecutionDecision.Deny : review.Length > 0 ? ExecutionDecision.AskUser : ExecutionDecision.Allow,
            reasons.Length == 0 ? "Все критерии прошли порог авторазрешения." : string.Join("; ", reasons.Select(a =>
                Criteria.Single(c => c.Id == a.Id).Label + " — есть риск")))
        { PolicyVersion = "jev-plan-v2-binary", Model = model, Criteria = answers, AllowThreshold = settings.AllowThreshold, DenyThreshold = settings.DenyThreshold };
    }
}
