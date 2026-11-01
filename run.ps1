param(
    [string]$RepositoryPath,
    [ValidateSet("serve", "start", "stop", "list")]
    [string]$Command = "serve",
    [string]$AgentId,
    [string]$Persona
)

$ErrorActionPreference = "Stop"

$resolvedRepositoryPath = if ($RepositoryPath) { [System.IO.Path]::GetFullPath($RepositoryPath) } else { $null }
$projectPath = Join-Path $PSScriptRoot "src\PortfolioAgent.Api\PortfolioAgent.Api.csproj"
if ($Persona -and $Command -ne "start") {
    throw "-Persona applies to -Command start. Register the persona after starting the host."
}

$cliArguments = switch ($Command) {
    "serve" {
        if ($resolvedRepositoryPath) { @("serve", $resolvedRepositoryPath) }
        else { @("serve") }
    }
    "start" {
        if (-not $resolvedRepositoryPath) { throw "Pass -RepositoryPath when registering an agent." }
        $startArguments = @("start", $resolvedRepositoryPath)
        if ($AgentId) { $startArguments += @("--id", $AgentId) }
        if ($Persona) { $startArguments += @("--persona", $Persona) }
        $startArguments
    }
    "stop" {
        if (-not $AgentId) { throw "Pass -AgentId when stopping an agent." }
        @("stop", $AgentId)
    }
    "list" { @("list") }
}

dotnet run --no-launch-profile --project $projectPath -- @cliArguments
exit $LASTEXITCODE
