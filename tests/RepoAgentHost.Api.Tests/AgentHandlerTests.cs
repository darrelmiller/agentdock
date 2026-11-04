using A2A;

namespace RepoAgentHost.Api.Tests;

public sealed class AgentHandlerTests
{
    [Fact]
    public async Task MissingPersonaProducesFailedTaskWithAnExplicitExplanation()
    {
        var events = await ExecuteAsync(new FakeRuntime { MissingPersona = true }, "Do work.");
        var status = Assert.IsType<TaskStatusUpdateEvent>(events.Last().StatusUpdate);
        Assert.Equal(TaskState.Failed, status.Status.State);
        Assert.Contains("Unable to select custom agent 'missing'", status.Status.Message!.Parts[0].Text);
        Assert.DoesNotContain(events, item => item.ArtifactUpdate is not null);
    }

    [Fact]
    public async Task AssistantUpdatesEmitWorkingStatusMessagesBeforeFinalArtifact()
    {
        var runtime = new FakeRuntime { Progress = ["Inspecting documents.", "Comparing findings."] };
        var events = await ExecuteAsync(runtime, "Review the documents.");
        Assert.Equal(6, events.Count);
        Assert.Equal("Inspecting documents.", Assert.IsType<TaskStatusUpdateEvent>(events[2].StatusUpdate).Status.Message!.Parts[0].Text);
        Assert.Equal("Comparing findings.", Assert.IsType<TaskStatusUpdateEvent>(events[3].StatusUpdate).Status.Message!.Parts[0].Text);
        Assert.All(events.Skip(2).Take(2), item =>
        {
            var status = Assert.IsType<TaskStatusUpdateEvent>(item.StatusUpdate);
            Assert.Equal(TaskState.Working, status.Status.State);
            Assert.Equal("test-task", status.Status.Message!.TaskId);
            Assert.Equal("caller-context", status.ContextId);
        });
        Assert.NotNull(events[4].ArtifactUpdate);
        Assert.Equal(TaskState.Completed, events[5].StatusUpdate!.Status.State);
    }

    [Fact]
    public async Task WorkMessage_EmitsTaskLifecycleAndResultArtifact()
    {
        var runtime = new FakeRuntime();
        var events = await ExecuteAsync(runtime, "Explain the architecture.");

        Assert.Equal(TaskState.Submitted, Assert.IsType<AgentTask>(events[0].Task).Status.State);
        Assert.Equal(TaskState.Working, Assert.IsType<TaskStatusUpdateEvent>(events[1].StatusUpdate).Status.State);
        var artifact = Assert.IsType<TaskArtifactUpdateEvent>(events[2].ArtifactUpdate).Artifact;
        Assert.Equal("Result", artifact.Name);
        Assert.Equal("Delivered result", artifact.Parts[0].Text);
        Assert.Equal(TaskState.Completed, Assert.IsType<TaskStatusUpdateEvent>(events[3].StatusUpdate).Status.State);
        Assert.Equal("test-task", runtime.LastTaskId);
        Assert.Equal(0, runtime.Resets);
    }

    [Fact]
    public async Task ExplicitNewSession_ReturnsMessageWithoutTask()
    {
        var runtime = new FakeRuntime();
        var events = await ExecuteAsync(runtime, "/new-session");

        Assert.Single(events);
        Assert.NotNull(events[0].Message);
        Assert.Null(runtime.LastTaskId);
        Assert.Equal(1, runtime.Resets);
    }

    [Fact]
    public async Task EmptyMessage_ReturnsMessageWithoutInvokingRuntime()
    {
        var runtime = new FakeRuntime();
        var events = await ExecuteAsync(runtime, " ");

        Assert.Single(events);
        Assert.NotNull(events[0].Message);
        Assert.Null(runtime.LastTaskId);
        Assert.Equal(0, runtime.Resets);
    }

    private static async Task<List<StreamResponse>> ExecuteAsync(FakeRuntime runtime, string text)
    {
        var queue = new AgentEventQueue(10);
        var context = new RequestContext
        {
            Message = new Message { MessageId = "request", Parts = [Part.FromText(text)] },
            TaskId = "test-task",
            ContextId = "caller-context",
            StreamingResponse = false
        };
        await new RepositoryAgentHandler(runtime).ExecuteAsync(context, queue, CancellationToken.None);
        queue.Complete();
        var events = new List<StreamResponse>();
        await foreach (var item in queue)
        {
            events.Add(item);
        }
        return events;
    }

    private sealed class FakeRuntime : IAgentRuntime
    {
        public string? LastTaskId { get; private set; }
        public int Resets { get; private set; }
        public string[] Progress { get; init; } = [];
        public bool MissingPersona { get; init; }

        public async Task<string> AskAsync(string taskId, string question, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task>? reportProgress = null)
        {
            LastTaskId = taskId;
            if (MissingPersona)
            {
                throw new PersonaSelectionException("missing", new IOException("Missing agent"));
            }
            foreach (var text in Progress)
            {
                await reportProgress!(text, cancellationToken);
            }
            return "Delivered result";
        }

        public Task StartNewSessionAsync(CancellationToken cancellationToken)
        {
            Resets++;
            return Task.CompletedTask;
        }
    }
}
