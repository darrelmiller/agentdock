#:project ..\src\RepoAgentHost.Api\RepoAgentHost.Api.csproj
#:property PublishAot=false

using GitHub.Copilot;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

if (args is not [var runtimePath, var stateDirectory, var hostPath])
{
    throw new ArgumentException("Provide the installed runtime path, disposable state directory, and host path.");
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using (var client = new CopilotClient(new CopilotClientOptions
{
    Mode = CopilotClientMode.Empty,
    BaseDirectory = stateDirectory,
    UseLoggedInUser = false,
    Connection = RuntimeConnection.ForStdio(path: runtimePath)
}))
{
    await client.StartAsync(timeout.Token);
    var response = await client.PingAsync("repo-agent-host-package-check", timeout.Token);
    if (response.Message != "pong: repo-agent-host-package-check")
    {
        throw new InvalidOperationException($"Unexpected runtime ping response: '{response.Message}', protocol {response.ProtocolVersion}.");
    }
    Console.WriteLine($"Packaged runtime SDK handshake succeeded (protocol {response.ProtocolVersion}).");
}

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();

var start = new ProcessStartInfo(hostPath)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
start.ArgumentList.Add("start");
start.Environment["REPOSITORY_AGENT_PORT"] = port.ToString();
start.Environment["REPOAGENT_STATE_DIRECTORY"] = Path.Combine(stateDirectory, "host");
using var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the installed host.");
var stdout = host.StandardOutput.ReadToEndAsync();
var stderr = host.StandardError.ReadToEndAsync();
try
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(1) };
    while (true)
    {
        timeout.Token.ThrowIfCancellationRequested();
        if (host.HasExited)
        {
            throw new InvalidOperationException($"Installed host exited ({host.ExitCode}): {await stdout}\n{await stderr}");
        }
        try
        {
            using var health = JsonDocument.Parse(await http.GetStringAsync("/healthz", timeout.Token));
            if (health.RootElement.GetProperty("status").GetString() != "ok")
            {
                throw new InvalidOperationException("Installed host reported an unhealthy status.");
            }
            break;
        }
        catch (HttpRequestException) when (!timeout.IsCancellationRequested)
        {
            await Task.Delay(200, timeout.Token);
        }
        catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
        {
            await Task.Delay(200, timeout.Token);
        }
    }
    using var catalog = await http.GetAsync("/catalog", timeout.Token);
    catalog.EnsureSuccessStatusCode();
    if (catalog.Content.Headers.ContentType?.MediaType != "application/ai-catalog+json")
    {
        throw new InvalidOperationException("Installed host did not expose the AI Catalog media type.");
    }
    Console.WriteLine("Installed RepoAgentHost host health and catalog checks succeeded.");
}
finally
{
    if (!host.HasExited)
    {
        host.Kill(entireProcessTree: true);
    }
    await host.WaitForExitAsync();
    await Task.WhenAll(stdout, stderr);
}
