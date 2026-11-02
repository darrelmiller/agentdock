[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [string] $DotNetExecutable = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$PackageDirectory = (Resolve-Path $PackageDirectory).Path
$rid = $RuntimeIdentifier
if ($rid -notin @('win-x64', 'win-arm64')) {
    throw "Tool smoke checks require Windows x64 or ARM64; current RID is $rid."
}

$package = Join-Path $PackageDirectory "SpecWorks.RepoAgentHost.Cli.$rid.$Version.nupkg"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $prefix = "tools/any/$rid/"
    foreach ($required in @(
        'rahcli.exe', 'rahcli.runtimeconfig.json', 'LICENSE', 'specs.json', 'plugin.json',
        'skills/repo-agent-host-cli/SKILL.md',
        "runtimes/$rid/native/copilot.exe",
        "runtimes/$rid/native/copilot-runtime.exe",
        "runtimes/$rid/native/copilot_runtime.dll",
        "runtimes/$rid/native/plugins/browser-use/.release-target",
        "runtimes/$rid/native/plugins/computer-use/.release-target"
    )) {
        if ($null -eq $archive.GetEntry("$prefix$required")) {
            throw "Package is missing $prefix$required."
        }
    }
} finally {
    $archive.Dispose()
}

# Keep the nested NuGet runtime executable path below Windows process-launch path limits.
$toolDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "rah-$([Guid]::NewGuid().ToString('N').Substring(0, 16))"
New-Item -ItemType Directory -Path $toolDirectory | Out-Null
try {
    & $DotNetExecutable tool install SpecWorks.RepoAgentHost.Cli --version $Version --tool-path $toolDirectory `
        --source $PackageDirectory --source https://api.nuget.org/v3/index.json --arch $rid.Substring(4)
    if ($LASTEXITCODE -ne 0) { throw "Local tool installation failed ($LASTEXITCODE)." }

    $command = Join-Path $toolDirectory 'rahcli.cmd'
    $versionOutput = & $command --version
    if ($LASTEXITCODE -ne 0 -or $versionOutput -ne "RepoAgentHost $($Version.Split('-')[0])") {
        throw "Unexpected tool version: $versionOutput"
    }
    & $command --help
    if ($LASTEXITCODE -ne 0) { throw "Tool help failed ($LASTEXITCODE)." }

    $runtime = Get-ChildItem $toolDirectory -Recurse -Force -File -Filter 'copilot-runtime.exe' |
        Where-Object { $_.FullName -like "*runtimes\$rid\native\*" } |
        Select-Object -First 1
    if ($null -eq $runtime) {
        throw "Installed tool is missing the $rid runtime. Use a matching-architecture .NET SDK."
    }
    & $DotNetExecutable run (Join-Path $PSScriptRoot 'verify-runtime.cs') -- `
        $runtime.FullName (Join-Path $toolDirectory 'runtime-state') `
        (Join-Path $runtime.Directory.Parent.Parent.Parent.FullName 'rahcli.exe')
    if ($LASTEXITCODE -ne 0) { throw "Bundled Copilot runtime handshake failed ($LASTEXITCODE)." }
    Write-Output "Verified installed RepoAgentHost tool and bundled runtime for $rid."
} finally {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $toolDirectory -Recurse -Force
            break
        } catch [System.UnauthorizedAccessException], [System.IO.IOException] {
            if ($attempt -eq 5) { throw }
            Write-Output "Waiting for temporary runtime file locks to release (attempt $attempt)."
            Start-Sleep -Milliseconds 500
        }
    }
}
