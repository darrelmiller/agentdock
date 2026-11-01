using Microsoft.Extensions.Hosting;

namespace PortfolioAgent.Api;

public interface IHostedAgentRuntime : IAgentRuntime, IHostedService, IAsyncDisposable;
