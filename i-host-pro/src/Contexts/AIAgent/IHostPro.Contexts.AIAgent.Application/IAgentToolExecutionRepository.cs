using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

public interface IAgentToolExecutionRepository : IRepository<AgentToolExecution, Guid>
{
}
