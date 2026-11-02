[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $root 'plugin.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.'$schema' -ne 'https://agent-plugins.org/schemas/1.0.0/plugin.schema.json' -or
    $manifest.name -ne 'repo-agent-host' -or
    $manifest.version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw 'Expected an Agent Plugins 1.0 repo-agent-host manifest with a safe semantic version.'
}
$skillPath = Join-Path $root 'skills\repo-agent-host-cli\SKILL.md'
foreach ($path in @($manifestPath, $skillPath, (Join-Path $root 'LICENSE'), (Join-Path $root 'docs\plugin.md'))) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Plugin packaging does not accept reparse points: $path"
    }
}
$output = Join-Path $root 'artifacts\plugins'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zipPath = Join-Path $output "repo-agent-host-$($manifest.version).zip"
$staging = Join-Path ([IO.Path]::GetTempPath()) "repo-agent-host-plugin-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path (Join-Path $staging 'skills\repo-agent-host-cli') -Force | Out-Null
try {
    Copy-Item $manifestPath (Join-Path $staging 'plugin.json')
    Copy-Item $skillPath (Join-Path $staging 'skills\repo-agent-host-cli\SKILL.md')
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $staging 'LICENSE')
    Copy-Item (Join-Path $root 'docs\plugin.md') (Join-Path $staging 'README.md')
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force
    Write-Output $zipPath
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
