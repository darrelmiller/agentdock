using A2A;

namespace PortfolioAgent.Api;

public sealed class RepositoryAgentHandler(CopilotAgentService agent) : IAgentHandler
{
    public async Task ExecuteAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        var userText = context.UserText;
        if (string.IsNullOrWhiteSpace(userText))
        {
            await new MessageResponder(eventQueue, context.ContextId)
                .ReplyAsync("Send a text question about the configured repository.", cancellationToken: cancellationToken);
            return;
        }

        var answer = await agent.AskAsync(context.ContextId, userText, cancellationToken);
        await new MessageResponder(eventQueue, context.ContextId)
            .ReplyAsync(answer, cancellationToken: cancellationToken);
    }

    public Task CancelAsync(RequestContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
