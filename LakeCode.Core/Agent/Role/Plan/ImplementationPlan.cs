namespace LakeCode.Core.Agent;

/// <summary>Immutable, session-owned plan revision. Never a workspace file.</summary>
public sealed record ImplementationPlan(Guid Id, int Revision, string Markdown);