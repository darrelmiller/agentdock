using System.Net;
using A2A;
using A2A.AspNetCore;
using PortfolioAgent.Api;

var builder = WebApplication.CreateBuilder(args);
var repositoryRoot = builder.Configuration["Repository:Path"]
    ?? Environment.GetEnvironmentVariable("REPOSITORY_AGENT_REPOSITORY_ROOT")
    ?? Environment.GetEnvironmentVariable("PORTFOLIO_AGENT_REPOSITORY_ROOT");

if (string.IsNullOrWhiteSpace(repositoryRoot))
{
    throw new InvalidOperationException(
        "Set REPOSITORY_AGENT_REPOSITORY_ROOT to the Git repository the agent should use.");
}

var repository = new RepositoryKnowledge(repositoryRoot);
var portValue = builder.Configuration["REPOSITORY_AGENT_PORT"]
    ?? Environment.GetEnvironmentVariable("REPOSITORY_AGENT_PORT")
    ?? Environment.GetEnvironmentVariable("PORTFOLIO_AGENT_PORT")
    ?? "5080";

if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
{
    throw new InvalidOperationException("REPOSITORY_AGENT_PORT must be a valid TCP port.");
}

builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Services.AddSingleton(repository);
builder.Services.AddSingleton<CopilotAgentService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CopilotAgentService>());
builder.Services.AddA2AAgent<RepositoryAgentHandler>(CreateAgentCard(port));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapWellKnownAgentCard(CreateAgentCard(port));
app.MapA2A("/a2a");

app.Run();

static AgentCard CreateAgentCard(int port) => new()
{
    Name = "Repository Agent",
    Description = "Works with the configured Git repository, following its instructions, skills, and knowledge.",
    Version = "0.1.0",
    SupportedInterfaces =
    [
        new AgentInterface
        {
            Url = $"http://127.0.0.1:{port}/a2a",
            ProtocolBinding = "JSONRPC",
            ProtocolVersion = "1.0"
        }
    ],
    Capabilities = new AgentCapabilities { Streaming = false },
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills =
    [
        new AgentSkill
        {
            Id = "repository-agent",
            Name = "Repository agent",
            Description = "Answers questions and performs requested work using the configured repository.",
            Tags = ["repository", "coding", "knowledge", "skills"],
            InputModes = ["text/plain"],
            OutputModes = ["text/plain"]
        }
    ]
};
