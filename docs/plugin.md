# AgentDock Agent Plugin

The `agentdock` plugin follows [Agent Plugins 1.0](https://agent-plugins.org/specification).
It packages the CLI skill that teaches an agent to acquire AgentDock, `ai-catalog`, and `a2a`,
manage hosted workspaces, discover agents, and send tracked work with HTTP+JSON streaming.

## Portable layout

```text
plugin.json
skills/
  agentdock-cli/
    SKILL.md
LICENSE
README.md
```

The repository root and published tool directories use this layout. `plugin.json` supplies
metadata only; clients discover the skill at the fixed `skills/` location. No MCP server,
client-specific hooks, or install-time executable actions are declared.

## Installation

Use your client's **Agent Plugins-compatible plugin installer**, supplying either an authorized
checkout of this repository, its repository URL if supported, or an extracted plugin ZIP.
Select the directory containing `plugin.json` as the plugin root.

Repository URL: `https://github.com/darrelmiller/agentdock`

The repository is currently private; the installer must have access. The manifest does not make
the plugin publicly discoverable or register it in a marketplace/catalog. No public release URL
exists yet. Agent Plugins deliberately leaves distribution and installation syntax to clients,
so there is no portable `agent-plugin install` command to document.

After enabling the plugin in a skills-capable client, confirm `agentdock-cli` is available.
Ask the agent to install AgentDock from an approved package source, then verify `agentdock --version`.
The skill documents `dotnet tool install --global SpecWorks.AgentDock.Cli` with an approved version,
including local feeds before public NuGet publication. Installing/enabling the plugin alone does
not install the tool, authenticate Copilot, start a host, or authorize inference or file changes.

For a harness that supports skills but not Agent Plugins, copy `skills/agentdock-cli` into its
documented skill directory instead. This is skill installation, not plugin conformance.

## Build a distributable ZIP

From the repository root, using PowerShell 7:

```powershell
.\scripts\pack-plugin.ps1
```

The output is `artifacts\plugins\agentdock-0.2.0.zip` (version from `plugin.json`). It contains only
the manifest, skill, license, and this installation guide as its root README, not the source
checkout or native runtime binaries. Extract into a directory before using a directory-based
installer. No symlinks or external package paths are needed.

CI builds and uploads the ZIP as the `agentdock-plugin` artifact. Artifacts require repository
access and are not public releases. No marketplace registration or public release upload occurs.
The same manifest/skill are also included beside the AgentDock executable in publish/tool output.

Plugin versioning is independent of executable packaging overrides: update `plugin.json` when
releasing changed plugin content. A NuGet build's `-p:Version` does not rewrite the plugin version.
