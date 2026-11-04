using Microsoft.Extensions.Hosting;

namespace RepoAgentHost.Api;

public interface IHostedAgentRuntime : IAgentRuntime, IHostedService, IAsyncDisposable;
