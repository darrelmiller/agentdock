using System.Collections.Concurrent;
using System.Text.Json;
using A2A;

namespace RepoAgentHost.Api;

public sealed class AgentRegistry : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, HostedAgent> _agents = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly string? _initialWorkspace;
    private readonly string _stateDirectory;
    private readonly string[] _allowedRoots;
    private readonly string _baseUrl;
    private readonly Func<AgentRegistration, RepositoryKnowledge, IHostedAgentRuntime> _factory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentRegistry> _logger;
    private FileStream? _registryLock;
    private string _hostId = "";
    private bool _stopped;

    public AgentRegistry(string? initialWorkspace, string stateDirectory, IEnumerable<string> allowedRoots,
        string baseUrl, Func<AgentRegistration, RepositoryKnowledge, IHostedAgentRuntime> factory,
        ILoggerFactory loggerFactory)
    {
        _initialWorkspace = initialWorkspace is null ? null : NormalizePath(initialWorkspace);
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _allowedRoots = allowedRoots.Select(NormalizePath).ToArray();
        _baseUrl = baseUrl.TrimEnd('/');
        _factory = factory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentRegistry>();
    }

    private string StatePath => Path.Combine(_stateDirectory, "agents.json");
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        _registryLock = new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var existingState = File.Exists(StatePath);
            var state = existingState
                ? JsonSerializer.Deserialize<RegistryState>(
                    await File.ReadAllTextAsync(StatePath, cancellationToken), JsonOptions)
                    ?? throw new InvalidOperationException("Agent registry state is null.")
                : new RegistryState(Guid.NewGuid().ToString("N"), []);
            if (!Guid.TryParseExact(state.HostId, "N", out _) || state.Agents is null)
            {
                throw new InvalidOperationException("Agent registry state is invalid.");
            }
            _hostId = state.HostId;
            foreach (var registration in state.Agents)
            {
                AgentIds.Validate(registration.Id);
                if (registration.Persona is not null)
                {
                    PersonaNames.Validate(registration.Persona);
                }
                if (_agents.ContainsKey(registration.Id) || _agents.Values.Any(
                    a => a.Registration.WorkspacePath.Equals(NormalizePath(registration.WorkspacePath), PathComparison)))
                {
                    throw new InvalidOperationException("Agent registry contains duplicate IDs or workspace paths.");
                }
                var normalized = registration with { WorkspacePath = NormalizePath(registration.WorkspacePath) };
                _agents[registration.Id] = await StartAgentAsync(normalized, cancellationToken);
            }
            if (!existingState)
            {
                if (_initialWorkspace is not null)
                {
                    await AddAsync(new RegisterAgentRequest(_initialWorkspace), cancellationToken);
                }
                else
                {
                    await SaveAsync([], cancellationToken);
                }
            }
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public IReadOnlyList<AgentRegistrationView> List() => _agents.Values
        .OrderBy(a => a.Registration.Id, StringComparer.Ordinal)
        .Select(View).ToArray();

    private AgentRegistrationView View(HostedAgent agent) => new(
        agent.Registration.Id, agent.Registration.WorkspacePath, agent.Status, agent.Error,
        $"{_baseUrl}/agents/{agent.Registration.Id}/.well-known/agent-card.json", agent.Registration.Persona);

    public AgentCard GetCard(string id) => RequireReady(id).Card
        ?? throw new InvalidOperationException("Ready agent has no card.");

    public IA2ARequestHandler GetHandler(string id) => RequireReady(id).Server
        ?? throw new InvalidOperationException("Ready agent has no A2A server.");

    private HostedAgent RequireReady(string id)
    {
        if (!_agents.TryGetValue(id, out var agent))
        {
            throw new AgentRegistryException(404, $"Agent '{id}' is not registered.");
        }
        if (agent.Status != "ready")
        {
            throw new AgentRegistryException(503, $"Agent '{id}' is not ready. Inspect the management endpoint for details.");
        }
        return agent;
    }

    public object Catalog() => new
    {
        specVersion = "1.0",
        host = new { identifier = $"urn:agentdock:host:{_hostId}", displayName = "RepoAgentHost" },
        entries = _agents.Values.Where(a => a.Status == "ready").OrderBy(a => a.Registration.Id)
            .Select(a => new
            {
                identifier = $"urn:agentdock:{_hostId}:agent:{a.Registration.Id}",
                displayName = a.Card!.Name,
                description = a.Card.Description,
                type = "application/a2a-agent-card+json",
                tags = new[] { a.Registration.Id, "agent", "knowledge", "skills", "tools" },
                url = View(a).CardUrl
            }).ToArray()
    };

    public async Task<AgentRegistrationView> AddAsync(RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            throw new AgentRegistryException(400, "A workspacePath is required.");
        }
        var path = NormalizePath(request.WorkspacePath);
        var id = request.Id is null ? AgentIds.FromWorkspace(path) : AgentIds.Validate(request.Id);
        if (request.Persona is not null)
        {
            PersonaNames.Validate(request.Persona);
        }
        ValidateWorkspace(path);
        // Read the profile before reserving or persisting an invalid registration.
        var knowledge = new RepositoryKnowledge(path);
        _ = knowledge.Identity;
        await _changes.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_agents.ContainsKey(id))
            {
                throw new AgentRegistryException(409, $"Agent ID '{id}' is already registered. Supply a different ID.");
            }
            if (_agents.Values.Any(a => a.Registration.WorkspacePath.Equals(path, PathComparison)))
            {
                throw new AgentRegistryException(409, "This workspace is already registered.");
            }
            var registration = new AgentRegistration(id, path, request.Persona);
            var agent = await StartAgentAsync(registration, cancellationToken);
            try
            {
                await SaveAsync(_agents.Values.Select(a => a.Registration).Append(registration), cancellationToken);
            }
            catch
            {
                await agent.DisposeRuntimeAsync();
                throw;
            }
            _agents[id] = agent;
            return View(agent);
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (!_agents.TryGetValue(id, out var agent))
            {
                throw new AgentRegistryException(404, $"Agent '{id}' is not registered.");
            }
            agent.Status = "stopping";
            try
            {
                await agent.DisposeRuntimeAsync();
                await SaveAsync(_agents.Values.Where(a => a != agent).Select(a => a.Registration), CancellationToken.None);
                _agents.TryRemove(id, out _);
            }
            catch (Exception exception)
            {
                agent.Status = "failed";
                agent.Error = exception.Message;
                _logger.LogError(exception, "Failed to unregister agent {AgentId}", id);
                throw;
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    private async Task<HostedAgent> StartAgentAsync(AgentRegistration registration, CancellationToken cancellationToken)
    {
        var agent = new HostedAgent(registration);
        IHostedAgentRuntime? runtime = null;
        try
        {
            ValidateWorkspace(registration.WorkspacePath);
            var knowledge = new RepositoryKnowledge(registration.WorkspacePath);
            var card = AgentCards.Create(_baseUrl, registration.Id, knowledge.Identity);
            runtime = _factory(registration, knowledge);
            await runtime.StartAsync(cancellationToken);
            agent.Runtime = runtime;
            agent.Card = card;
            agent.Server = new A2AServer(new RepositoryAgentHandler(runtime), new InMemoryTaskStore(),
                new ChannelEventNotifier(), _loggerFactory.CreateLogger<A2AServer>(),
                new A2AServerOptions { SupportsStreaming = true });
            agent.Status = "ready";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
            agent.Status = "failed";
            agent.Error = exception.Message;
            _logger.LogError(exception, "Failed to start agent {AgentId}", registration.Id);
        }
        catch
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
            throw;
        }
        return agent;
    }

    private void ValidateWorkspace(string path)
    {
        if (_allowedRoots.Length != 0 && !_allowedRoots.Any(root => path.Equals(root, PathComparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison)))
        {
            throw new AgentRegistryException(403, "The workspace is outside the configured allowed roots.");
        }
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AgentRegistryException(400, "Workspace paths through symbolic links or junctions are not supported.");
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new AgentRegistryException(400, "Workspace paths must be absolute.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private async Task SaveAsync(IEnumerable<AgentRegistration> registrations, CancellationToken cancellationToken)
    {
        var state = new RegistryState(_hostId, registrations.OrderBy(a => a.Id).ToArray());
        await File.WriteAllTextAsync(StatePath + ".tmp", JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        File.Move(StatePath + ".tmp", StatePath, overwrite: true);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            var errors = new List<Exception>();
            foreach (var agent in _agents.Values)
            {
                agent.Status = "stopping";
                try
                {
                    await agent.DisposeRuntimeAsync();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to stop agent {AgentId}", agent.Registration.Id);
                    errors.Add(exception);
                }
            }
            _registryLock?.Dispose();
            _registryLock = null;
            if (errors.Count != 0)
            {
                throw new AggregateException("One or more agents failed to stop.", errors);
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private sealed record RegistryState(string HostId, AgentRegistration[] Agents);

    private sealed class HostedAgent(AgentRegistration registration)
    {
        public AgentRegistration Registration { get; } = registration;
        public string Status { get; set; } = "starting";
        public string? Error { get; set; }
        public AgentCard? Card { get; set; }
        public A2AServer? Server { get; set; }
        public IHostedAgentRuntime? Runtime { get; set; }

        public async ValueTask DisposeRuntimeAsync()
        {
            try
            {
                if (Server is not null)
                {
                    await Server.DisposeAsync();
                }
            }
            finally
            {
                if (Runtime is not null)
                {
                    await Runtime.DisposeAsync();
                    Runtime = null;
                }
            }
        }
    }
}
