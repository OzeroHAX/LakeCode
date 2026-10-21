using System.Runtime.InteropServices;
using LakeCode.Core.Agent.Delegation;
using LakeCode.Core.Agent.Execution;
using LakeCode.Core.Agent.Prompt;
using LakeCode.Core.Agent.Role;
using LakeCode.Core.Agent.Session;
using LakeCode.Core.Agent.Session.History;
using LakeCode.Core.Agent.Skills;
using LakeCode.Core.Agent.Subagents;
using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Agent.Tools.Ask;
using LakeCode.Core.Agent.Tools.Eval;
using LakeCode.Core.Agent.Tools.Execution;
using LakeCode.Core.Agent.Tools.Registration;
using LakeCode.Core.Providers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Composition;

/// <summary>Explicit composition for the main conversation and independent read-only agents.</summary>
internal static class AgentFactory
{
    public static AgentComponents CreateMain(string workspaceRoot,
        Func<ExternalReadRequest, CancellationToken, Task<ExternalReadDecision>>? confirmExternalRead,
        string sessionId, Func<AskQuestion[], CancellationToken, Task<string[][]>>? askUser,
        string sessionWorkspaceRoot, ToolExecutionApproval approval, Func<string, ImplementationPlan?> savePlan)
    {
        var files = new WorkspaceFiles(workspaceRoot, confirmExternalRead);
        var client = new ProviderChatClient();
        AgentSessionState? session = null;
        var history = new AgentHistory(() => session?.Current, client);
        AgentRunPreparer? preparer = null;
        EvalTool? eval = null;
        DelegationCoordinator? delegation = null;
        AgentSkills? skills = null;
        try
        {
            var invocations = new ToolInvocationRunner(() => preparer!.Configuration, () => eval, () => delegation,
                () => preparer!.ImageGeneration, () => skills?.ValidateRoots(), approval);
            var context = new SystemPromptContext(files.Root, RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString())
            { SessionWorkspaceRoot = sessionWorkspaceRoot };
            var instructions = new InstructionsReader(files.Root);
            delegation = new DelegationCoordinator(
                (questions, callId) => new SubagentRunner(files, context, instructions, questions, callId),
                () => invocations.DelegationInvocation);
            eval = new EvalTool(files.Root, invocations.InvokeEvalToolAsync,
                () => preparer!.Configuration.Tools.DescribeEvalTools());
            skills = new AgentSkills(files);
            var services = new AgentToolServices(files, eval, delegation, history.Projection, invocations,
                new AskTool(askUser), sessionId, () => preparer!.ImageGeneration);
            var researchFiles = string.IsNullOrWhiteSpace(sessionWorkspaceRoot) ? null
                : new WorkspaceFiles(sessionWorkspaceRoot, allowExternalReads: false) { ReportAbsolutePaths = true };
            var roles = new AgentRoleFactory(context, instructions, researchFiles, services, savePlan);
            preparer = new AgentRunPreparer(roles.Get(AgentMode.Build).Configuration, roles, skills,
                invocations, delegation, client, history, sessionId);
            var components = Complete("LakeCode", sessionId, files, history, preparer, invocations,
                skills, eval, delegation, allowMultipleToolCalls: null);
            session = components.Session;
            return components;
        }
        catch
        {
            try { delegation?.Dispose(); }
            finally
            {
                try { eval?.Dispose(); }
                finally { history.Capture.Dispose(); }
            }
            throw;
        }
    }

    public static AgentComponents CreateSubagent(WorkspaceFiles files, AgentConfiguration configuration)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var client = new ProviderChatClient();
        AgentSessionState? session = null;
        var history = new AgentHistory(() => session?.Current, client);
        try
        {
            AgentRunPreparer? preparer = null;
            var invocations = new ToolInvocationRunner(() => preparer!.Configuration, () => null, () => null,
                () => preparer!.ImageGeneration, () => { }, new ToolExecutionApproval());
            preparer = new AgentRunPreparer(configuration, null, null, invocations, null, client, history, sessionId);
            var components = Complete("LakeCodeResearch", sessionId, files, history, preparer, invocations,
                null, null, null, configuration.AllowMultipleToolCalls);
            session = components.Session;
            return components;
        }
        catch
        {
            history.Capture.Dispose();
            throw;
        }
    }

    private static AgentComponents Complete(string name, string sessionId, WorkspaceFiles files,
        AgentHistory history, AgentRunPreparer preparer, ToolInvocationRunner invocations,
        AgentSkills? skills, EvalTool? eval, DelegationCoordinator? delegation, bool? allowMultipleToolCalls)
    {
        var agent = new ChatClientAgent(history.Capture, new ChatClientAgentOptions
        {
            Name = name,
            ChatHistoryProvider = history.Provider,
            // Keep service-call history for stateless Responses reasoning/tool loops (MAF #6268).
            RequirePerServiceCallChatHistoryPersistence = true,
            ChatOptions = new ChatOptions { AllowMultipleToolCalls = allowMultipleToolCalls },
            AIContextProviders = [new RoleAwareContextProvider(skills?.Provider, () => preparer.Configuration,
                () => skills?.ValidateRoots())]
        }).AsBuilder().Use(invocations.ObserveToolCallAsync).Build();
        var session = new AgentSessionState(agent, history);
        var loop = new AgentLoop(agent, session, preparer, delegation);
        return new AgentComponents(sessionId, files, loop, session, history, invocations, eval, delegation);
    }
}
