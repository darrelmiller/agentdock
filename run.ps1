param(
    [string]$RepositoryPath = (Join-Path $PSScriptRoot "..\portfolio")
)

$ErrorActionPreference = "Stop"

if (-not $env:REPOSITORY_AGENT_REPOSITORY_ROOT -and -not $env:PORTFOLIO_AGENT_REPOSITORY_ROOT) {
    $env:REPOSITORY_AGENT_REPOSITORY_ROOT = [System.IO.Path]::GetFullPath($RepositoryPath)
}

dotnet run --no-launch-profile --project (Join-Path $PSScriptRoot "src\PortfolioAgent.Api\PortfolioAgent.Api.csproj")
