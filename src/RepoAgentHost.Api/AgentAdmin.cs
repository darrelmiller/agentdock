using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoAgentHost.Api;

public sealed class AgentAdmin(AgentRegistry registry, string pipeName, ILogger<AgentAdmin> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const string Usage = """
        Usage:
          rahcli serve [initial-workspace-path]
          rahcli start <workspace-path> [--id <friendly-id>] [--persona <custom-agent-name>]
          rahcli stop <friendly-id>
          rahcli list
        'serve' runs a normal foreground host; Ctrl+C shuts it down. Other commands control that
        running host for the current user and REPOSITORY_AGENT_PORT (default 5080).
        A single workspace argument remains supported as an alias for 'serve'.
        """;

    public static string PipeName(int port)
    {
        var account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..16];
        return $"agentdock-{accountHash}-{port}";
    }

    public static AdminCommand ParseCommand(string[] args)
    {
        if (args is ["stop", var stopId])
        {
            return new("stop", Id: AgentIds.Validate(stopId));
        }
        if (args is ["list"])
        {
            return new("list");
        }
        if (args.Length < 2 || args[0] != "start")
        {
            throw new AgentRegistryException(400, Usage);
        }
        string? id = null;
        string? persona = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new AgentRegistryException(400, Usage);
            }
            switch (args[index])
            {
                case "--id" when id is null:
                    id = AgentIds.Validate(args[index + 1]);
                    break;
                case "--persona" when persona is null:
                    persona = PersonaNames.Validate(args[index + 1]);
                    break;
                default:
                    throw new AgentRegistryException(400, "Unknown or repeated option.\n" + Usage);
            }
        }
        return new("start", args[1], id, persona);
    }

    public static async Task<int> RunCommandAsync(string[] args, string pipeName)
    {
        try
        {
            var command = ParseCommand(args);
            if (command.WorkspacePath is not null)
            {
                command = command with { WorkspacePath = Path.GetFullPath(command.WorkspacePath) };
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000, timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions));
            var line = await reader.ReadLineAsync(timeout.Token)
                ?? throw new IOException("The host closed its management connection without a response.");
            var reply = JsonSerializer.Deserialize<AdminReply>(line, JsonOptions)
                ?? throw new JsonException("The host returned an invalid management response.");
            if (!reply.Success)
            {
                Console.Error.WriteLine(reply.Error);
                return 1;
            }
            if (reply.Agents is not null)
            {
                Console.WriteLine(JsonSerializer.Serialize(reply.Agents, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine($"Stopped and unregistered agent '{command.Id}'. Its workspace and saved session were not deleted.");
            }
            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("No running host answered. Run 'serve' in another terminal first.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The management command timed out. Its outcome is unknown; use 'list' before retrying.");
            return 1;
        }
        catch (Exception exception) when (exception is AgentRegistryException or IOException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                // An idle client must not permanently occupy the management interface.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await reader.ReadLineAsync(readTimeout.Token);
                AdminReply reply;
                try
                {
                    var command = JsonSerializer.Deserialize<AdminCommand>(
                        line ?? throw new JsonException("A management command is required."), JsonOptions)
                        ?? throw new JsonException("A management command is required.");
                    reply = await DispatchAsync(command, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Agent management command failed");
                    reply = new AdminReply(false, exception.Message);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, JsonOptions));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("An idle management connection timed out");
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Agent management connection was closed");
            }
        }
    }

    public async Task<AdminReply> DispatchAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        switch (command.Command)
        {
            case "start":
                var agent = await registry.AddAsync(new RegisterAgentRequest(
                    command.WorkspacePath ?? throw new AgentRegistryException(400, "A workspace path is required."),
                    command.Id, command.Persona), cancellationToken);
                return new AdminReply(agent.Status == "ready", agent.Error, [agent]);
            case "stop":
                await registry.RemoveAsync(AgentIds.Validate(
                    command.Id ?? throw new AgentRegistryException(400, "An agent ID is required.")), cancellationToken);
                return new AdminReply(true);
            case "list":
                return new AdminReply(true, Agents: registry.List().ToArray());
            default:
                throw new AgentRegistryException(400, Usage);
        }
    }

    public sealed record AdminCommand(string Command, string? WorkspacePath = null, string? Id = null, string? Persona = null);
    public sealed record AdminReply(bool Success, string? Error = null, AgentRegistrationView[]? Agents = null);
}
