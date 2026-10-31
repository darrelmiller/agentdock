# Repository Agent

A local A2A agent backed by the GitHub Copilot SDK for .NET. It can answer questions and perform
requested work in a configured Git repository, following that repository's instructions and
dynamically discovered skills. It can also retrieve relevant text from arbitrary repository content
and knowledge bundles; it does not assume a particular project, bundle path, or format.

The private agent repository and the grounding repository are separate. No portfolio content is
copied into this repository.

## Requirements

- .NET 10 SDK
- A GitHub Copilot subscription and a Copilot SDK runtime authenticated for your account
- The `a2a` CLI for A2A discovery and smoke tests

The application binds only to `127.0.0.1`. Its A2A endpoint is unauthenticated. Tool permission
requests are approved automatically so the headless agent can edit repository files and run local
commands as the service account. Any local process that can reach the endpoint can therefore make
those changes. Use only with a trusted local caller and repository; do not expose it beyond loopback
without adding authentication and an approval mechanism.

## Run locally

From the repository root, point the agent at any local Git repository:

```powershell
$env:REPOSITORY_AGENT_REPOSITORY_ROOT = "D:\github\darrelmiller\portfolio"
.\run.ps1
```

By default, the server listens on `http://127.0.0.1:5080`. Override the port with
`REPOSITORY_AGENT_PORT` and the model with `REPOSITORY_AGENT_MODEL`. The default model is
`gpt-5.4`.

The agent scans text files when it receives a question, so changes to bundle documents are picked up
without restarting. Skill directories are discovered when a Copilot session is created; restart the
agent to refresh changed or added skills.

## Talk to it with A2A CLI

```powershell
a2a card get http://127.0.0.1:5080
a2a send -a http://127.0.0.1:5080 "What projects are active?"
```

Health check: `http://127.0.0.1:5080/healthz`

## Repository discovery and safety

- The configured path must be a Git repository. The agent scans supported text formats, skips
  common build, dependency, and Git directories, and does not follow directory junctions or
  symlinks outside normal traversal.
- Skills are found by locating `SKILL.md` files and passing their skill roots to the Copilot SDK.
  Repository instructions and local custom agents are loaded, but automatic configuration discovery
  is disabled so repository-provided MCP servers and hooks are not started.
- Tool calls run under the current Windows account. The agent is instructed to preserve unrelated
  changes and not commit, push, change remotes, or perform destructive actions unless requested.
- The A2A endpoint has no authentication yet. Loopback binding prevents remote network access, not
  access by other processes running on the same machine.
- Responses should cite repository-relative source paths and distinguish current facts from dated
  or exploratory material.
- A2A context IDs map to in-memory Copilot sessions. Restarting the process ends those conversations.
- Retrieved repository content is sent to the configured Copilot runtime for inference. Do not point
  the agent at data that is not permitted to leave the machine through that inference service.
