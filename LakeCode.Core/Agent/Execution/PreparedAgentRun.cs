using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Execution;

internal sealed record PreparedAgentRun(ChatMessage Message, ChatClientAgentRunOptions Options);
