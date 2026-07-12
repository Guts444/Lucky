# Lucky

**Local-first AI agent harness for Windows.**

Lucky is a WinUI 3 desktop app for project-scoped chats with configurable model providers, SearXNG-backed web search, durable local memory, explicit access levels, and real project filesystem tools—with tool activity visible in the chat.

> **Status:** **v0.1.0** first public release. APIs and UI can still evolve; please report issues. Windows x64 installers ship on the [Releases](https://github.com/Guts444/Lucky/releases) page.

| | |
| --- | --- |
| Platform | Windows 10 1809+ (WinUI 3 / Windows App SDK), x64 installer |
| License | [MIT](LICENSE) |
| Core | `src/Lucky.Core` |
| UI | `src/Lucky.App` |
| Tests | `tests/Lucky.Tests` |

## Install (Windows x64)

Download the latest release from [Releases](https://github.com/Guts444/Lucky/releases):

| File | Description |
| --- | --- |
| **Lucky-*-win-x64-Setup.exe** | Recommended guided installer (Inno Setup) |
| **Lucky-*-win-x64.msi** | Windows Installer package (silent deploy friendly) |

Both install a self-contained build (Windows App SDK bundled) to Program Files, create Start Menu shortcuts, and register an uninstaller.

Silent MSI:

```powershell
msiexec /i Lucky-0.1.0-win-x64.msi /qn
```

Uninstall from **Settings → Apps**, or:

```powershell
msiexec /x Lucky-0.1.0-win-x64.msi /qn
```

## Why Lucky

Lucky is a native WinUI 3 desktop agent for Windows—not a thin wrapper around someone else’s chat product. You bring the model; Lucky owns the workspace loop, memory, tools, and trust boundaries.

- **Local-first by default.** Chats, settings, projects, and memories live under your Windows profile. API keys and MCP launch config are protected for the current user (DPAPI). Nothing durable is meant to live in the repo.
- **Project file access you can see.** In Workspace or Full access, the agent can list, read, search, create, edit, and patch files inside the selected project—path-safe, with traces in Thinking. Full access can also run bounded PowerShell at the project root and an optional Docker sandbox that never mounts the host workspace.
- **Durable memory with budgets.** Lucky captures useful facts (and skips likely secrets), splits them into long-lived `USER` profile notes and project/session `MEMORY` notes, retrieves what is in scope for the current access level, and enforces separate character budgets so memory cannot silently flood the prompt.
- **Subagents for parallel work.** Built-ins such as `explorer`, `reviewer`, `tester`, `writer`, and `worker` (plus custom JSON agents) can take bounded child turns. They inherit access limits, stay on the selected project, and return compact summaries instead of dumping full child transcripts into the main chat.
- **Your providers.** DeepSeek, **OpenRouter**, LM Studio, custom OpenAI-compatible servers, and optional ChatGPT subscription models via a local app-server helper. Settings manage accounts and keys; the chat composer is the only model/reasoning picker.
- **Explicit trust levels.** `Chat only`, `Workspace`, and `Full access` gate project context, filesystem tools, page reading, MCP, and sandbox. The agent should not imply it did work Lucky never ran.
- **Visible tool loop.** File, search, shell, MCP, sandbox, and web actions show up in the Thinking expander instead of happening silently.
- **Optional web.** User-controlled SearXNG for search, plus an opt-in trusted-domain static page reader (not a logged-in browser).

## Solution Layout

```text
Lucky.slnx
src/
  Lucky.App/      WinUI 3 desktop app
  Lucky.Core/     Provider, search, memory, persistence, and agent orchestration
tests/
  Lucky.Tests/    xUnit tests for core behavior
docs/
  ARCHITECTURE.md Architecture and runtime behavior
  QA.md           Manual verification checklist
LICENSE           MIT license
SECURITY.md       Vulnerability reporting and hardening notes
AGENTS.md         Contributor / coding-agent guide
```

## Requirements

- Windows 10 1809 or newer, with the Windows App SDK runtime required by the app package.
- .NET SDK matching the repository target framework (see project files under `src/`).
- Optional: LM Studio for local model hosting.
- Optional: SearXNG for local or self-hosted web search.
- Optional: a DeepSeek API key for hosted model calls.
- Optional: an [OpenRouter](https://openrouter.ai/) API key for multi-model access through one endpoint.
- Optional: a ChatGPT plan and the official local OpenAI coding CLI (for subscription models under Settings → Subscription accounts).
- Optional: Docker Desktop configured for Linux containers, plus a sandbox image that you have already built or loaded locally, for isolated code execution.

## Build

```powershell
dotnet restore Lucky.slnx
dotnet build Lucky.slnx
dotnet test Lucky.slnx
```

For the WinUI app on a typical x64 machine:

```powershell
dotnet build src\Lucky.App\Lucky.App.csproj -p:Platform=x64
dotnet run --project src\Lucky.App\Lucky.App.csproj -p:Platform=x64
```

To produce release installers (requires [WiX](https://wixtoolset.org/) CLI + [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
dotnet tool install -g wix
wix extension add -g WixToolset.UI.wixext
winget install --id JRSoftware.InnoSetup -e
./scripts/publish-release.ps1 -Version 0.1.0
```

Outputs:

- `dist/Lucky-0.1.0-win-x64.msi`
- `dist/Lucky-0.1.0-win-x64-Setup.exe`

Release builds use an unpackaged, self-contained publish (`WindowsAppSDKSelfContained`) so end users do not need a separate runtime install.

## Privacy and local state

Lucky is designed so **your data stays on your machine**:

```text
%LOCALAPPDATA%\Lucky\lucky-state.json
```

When running as a packaged WinUI app, Windows may redirect local app data through the package LocalCache path, for example `%LOCALAPPDATA%\Packages\<LuckyPackage>\LocalCache\Local\Lucky\lucky-state.json`.

That state includes settings, provider configuration, subagent settings, projects, chat sessions, and memory items. API keys and MCP launch configuration are stored only after being protected for the current Windows user (Windows DPAPI). ChatGPT subscription OAuth material is kept in a Lucky-isolated helper home and is not stored as a Lucky-owned plaintext token field.

**This repository must not contain** user state, transcripts, provider secrets, or local model artifacts. Local private notes can live under `docs/private/` (gitignored) or any `*.local.md` file—see `.gitignore`.

For vulnerability reports, see [SECURITY.md](SECURITY.md).

## Provider Setup

Lucky uses OpenAI-compatible chat completions for API and local providers. ChatGPT subscription models use a separate local app-server transport under **Subscription accounts**. Settings manages endpoints and keys; the chat composer is the only model/reasoning picker.

Model calls stream when the provider supports chat-completions streaming. Lucky shows answer tokens as they arrive, keeps displayed answers clean for the plain chat canvas, tracks reasoning/thinking text separately when the provider supplies it, and still parses streamed tool calls so project actions can run before the final answer.

### LM Studio

Use LM Studio when you want model traffic to stay on the local machine.

1. Install LM Studio and download a chat-capable model.
2. Start the local server in LM Studio.
3. Confirm the server is listening at `http://127.0.0.1:1234/v1`.
4. In Settings > Providers > API & local providers, choose `LM Studio`, verify the endpoint, and refresh models.
5. Choose any discovered LM Studio model from the chat composer; set the context window to match the loaded model so the meter is meaningful.

The default LM Studio provider does not require an API key and does not use DeepSeek thinking parameters.

### DeepSeek

Use DeepSeek when you want hosted model calls.

1. Create a DeepSeek API key.
2. In Settings > Providers > API & local providers, choose `DeepSeek API` and save the key. The canonical `https://api.deepseek.com` endpoint is locked so a saved key cannot be silently redirected.
3. Choose `DeepSeek V4 Flash` or `DeepSeek V4 Pro` and the reasoning level from the chat composer.
4. Pick `High` or `Extra High` reasoning from the selector entry.
5. Use `Refresh models` to validate the connection; DeepSeek v4 models default the context meter to 1,000,000 tokens.

DeepSeek is configured as an API-key provider. For DeepSeek v4 options Lucky sends DeepSeek's `thinking` object and selected `reasoning_effort`, omits the incompatible `tool_choice` field in thinking mode, and replays `reasoning_content` across tool rounds as required by DeepSeek. Lucky also recognizes the provider's textual DSML fallback, validates it against the tools exposed in that request, and never renders protocol markup as an assistant answer.

### OpenRouter

Use OpenRouter when you want one API key for many hosted models (OpenAI, Anthropic, Google, DeepSeek, and others).

1. Create an API key at [openrouter.ai](https://openrouter.ai/).
2. In Settings > Providers > API & local providers, choose `OpenRouter` and save the key. The endpoint is locked to `https://openrouter.ai/api/v1`.
3. Pick a seed model from the chat composer (defaults include GPT-4o mini, GPT-4o, Claude Sonnet 4, Gemini 2.5 Flash, and DeepSeek Chat), or use **Refresh models** to load the live catalog.
4. The composer keeps the list usable: seed models and your selection stay first; large catalogs are not dumped wholesale into the picker.

Lucky sends standard OpenAI-compatible chat completions (including tools when the route supports them). Requests include optional OpenRouter attribution headers (`HTTP-Referer`, `X-Title`) so the app is identifiable as Lucky. OpenRouter is treated as a portable provider—not DeepSeek thinking mode—so DeepSeek-only payload fields are not sent.

### ChatGPT subscription

Lucky can use models included with a connected ChatGPT plan through the official local OpenAI coding app-server. In Settings > Providers > Subscription accounts, choose **Connect** and finish the browser sign-in. Lucky accepts only an HTTPS authorization URL from the helper; the app-server owns refresh-token storage and refreshes it itself. Lucky never reads or displays a ChatGPT OAuth token.

Lucky uses a dedicated helper home under `%LOCALAPPDATA%\Lucky\` (or the packaged LocalCache equivalent) for every app-server process so subscription state stays isolated from other apps. Because Windows Credential Manager cannot hold the full ChatGPT OAuth bundle, Lucky materializes the helper’s file credential only while a process starts and otherwise stores it as a current-user Windows DPAPI blob. Browser cookies may still make the OpenAI webpage recognize an existing browser session; that does not mean Lucky reused another app’s local credential. Settings provides explicit Connect, Disconnect, and account-model refresh controls.

After connecting, choose **Refresh account models**. Lucky reuses that authenticated session and exposes only the models and reasoning efforts advertised for Lucky’s isolated sign-in. It does not invent model ids or copy catalogs from other installations. Context metering uses model metadata and is replaced by runtime-reported context after a response. The helper runs from an ephemeral neutral directory with a Lucky-managed private configuration that disables the helper’s own shell, web-search, app, hook, memory, plugin, and multi-agent features; selected-project actions continue through Lucky’s visible tools and access levels.

The official OpenAI coding CLI must be installed locally. Lucky detects its native executable (including the binary bundled by the official npm package) but never installs or updates it automatically.

## Chat Canvas

Lucky’s chat canvas is a focused desktop agent workspace:

- The composer uses one continuous, rounded surface; its text area does not switch to a separate highlighted panel on focus, and the access/model/action controls use compact rounded treatments.
- On a project's empty state, the displayed working-folder path is a button that opens the folder picker, so the workspace can be changed without returning to the rail.
- User messages sit on the right without a visible `You` label, with time at the bottom of the bubble.
- Assistant output sits on the left, with selectable answer text and selectable Thinking text.
- The Thinking expander is always labeled `Thinking`; it shows provider reasoning when available and appends visible tool/search trace entries. Large read-file payloads are summarized to path, size, and preview so generated files do not flood the chat canvas.
- The composer meter shows current input context, not the turn's billed total. It prefers the provider-reported latest input count and effective context window for the active provider/model, keeps tool-loop totals separately on the assistant message, and prefixes fallback estimates with `~` after a model switch or when no exact reading exists.
- Press **Ctrl+Enter** or use Send to submit a multiline composer message. While a response streams, chat follows the newest message only until you scroll upward; returning to the bottom re-enables follow-latest behavior.
- The assistant system prompt asks for clean chat prose, avoiding decorative separators, routine heading hashes, and excessive bold markers.

### Custom OpenAI-Compatible Provider

The provider layer supports custom local or hosted servers that implement `/v1/models` and `/v1/chat/completions`. Configure the endpoint and optional key under **API & local providers**, refresh its catalog, then choose a discovered model in the chat composer. Remote custom endpoints require HTTPS; plain HTTP is limited to loopback endpoints.

Configure:

- Base URL
- Optional API key
- Input-context budget

## SearXNG Setup

Lucky can search the web through a user-controlled SearXNG instance. The default URL is:

```text
http://127.0.0.1:8080
```

Lucky calls the JSON search endpoint and injects the top configured results into the model context. Users can explicitly search with `/web query`. Lucky may also search automatically for prompts that look current, such as requests involving latest releases, today's data, prices, schedules, weather, or news. The parent model also receives a `web_search` tool backed by the configured SearXNG endpoint, so it can run a live search during a tool loop when the user asks for current information. Offline artifact/build prompts, such as requests to create a standalone HTML file or app, skip automatic web search unless the user explicitly starts the prompt with `/web`.

## Trusted Page Reader

Lucky can also expose `web_open` as an opt-in, static page-reading tool. Enable it in Settings and list the domains Lucky may read, such as `docs.microsoft.com` or `modelcontextprotocol.io`. The reader has no browser cookies, logins, or form interaction; it accepts only HTTP(S) on default ports, validates every redirect against the same domain list, rejects hosts that resolve to loopback/private/link-local/reserved addresses, rejects binary responses, and caps downloaded and model-visible text.

`web_open` is available only in `FullAccess`, so a model cannot turn a normal chat or workspace-only turn into arbitrary browsing. It is a bounded document reader, not interactive browser automation. For interactive browser workflows, an explicitly configured MCP server can supply browser tools.

## MCP Servers (stdio)

Lucky supports user-configured local **stdio** Model Context Protocol servers. In Settings, add a display name, executable command, optional arguments, and optional working folder. Arguments are parsed by Lucky and passed through `ProcessStartInfo.ArgumentList`; Lucky does not invoke a command shell for the configured argument string. The command, arguments, and working folder are protected with Windows DPAPI when settings are saved, so tokens placed in an MCP launch command are not stored as plaintext in Lucky state. Existing plaintext MCP launch settings migrate on their next save.

MCP is disabled by default and exposed only during a `FullAccess` parent turn. Lucky starts configured servers for that one turn, performs the MCP initialize / `tools/list` / `tools/call` lifecycle, maps discovered tools into the provider's OpenAI-compatible tool schema, adds visible trace entries, and stops the processes after the turn. MCP tools are deliberately not delegated to subagents yet.

Treat an MCP server like installed local software: its tools can access files, services, or the network according to that server's own permissions. Configured server processes inherit the current user's process environment, so avoid placing secrets in a display name or inherited environment. Lucky bounds protocol frames, stderr diagnostics, tool descriptions/schemas, and returned text/structured data before exposing them to a model. The current implementation is operational for stdio tool servers, including paged discovery and text/structured tool results. Streamable HTTP, OAuth, resources/prompts, server-initiated requests, and long-lived tool-list subscriptions remain future work.

## Durable Memory

Lucky memory is local and lightweight. The current core captures memories from explicit or preference-like user messages, skips likely secrets, and stores summaries with tags, evidence, confidence, project id, source session id, and memory kind.

Memory kinds:

- `USER`: identity, preferences, naming corrections, and long-lived profile facts.
- `MEMORY`: project/session facts and durable notes from chats.

Memory retrieval is lexical today: Lucky tokenizes the query, scores enabled memories that are in scope for the current access level, boosts project matches, boosts pinned memories, and adds a small confidence and recency component. This durable memory layer should evolve toward embeddings or another semantic index without changing the user-facing concept.

The settings page can disable memories. When disabled, Lucky neither captures new memories nor recalls existing memories into the model prompt; stored memory items remain in local state so users can re-enable or manage them later. Settings also show separate USER and MEMORY character budgets, and prompt assembly respects those caps so memory does not silently become an unbounded prompt dump.

## Project-Scoped Chats

Projects represent workspace folders. A chat session belongs to one project id, and Lucky stores the selected project in app settings. When a project is opened, Lucky reuses the existing project record for that path or creates one with an initial chat.

Project scoping affects:

- Which chats are shown as relevant to the selected workspace.
- Which memories receive a project id when captured.
- Which memories get a retrieval boost on later turns.
- Which project name and path are supplied to the model.
- Which folder the filesystem tools may touch.

In `ChatOnly`, Lucky does not supply the selected project name, path, root `AGENTS.md`, project custom agents, project memories, or project filesystem tools to the model.

## Project Filesystem Tools

Lucky's agent loop can expose project-scoped file tools to the model. The first tool set is intentionally narrow:

- list files and folders
- read UTF-8 text files
- search project text files with a regex or plain query
- create or overwrite UTF-8 text files
- replace one exact text occurrence in a file
- apply a validated unified diff for multi-line or multi-file text edits
- run a bounded Windows PowerShell command for builds, tests, and project diagnostics

The tool service enforces path safety in code, not just in the prompt. Paths are resolved against the selected project, attempts to escape with `..` or absolute outside paths are rejected, common generated folders such as `.git`, `.vs`, `bin`, `obj`, and `node_modules` are skipped, and large or binary files are refused. Unified diffs are parsed before writes; every hunk is checked first, a stale line offset may use one unique nearby context match, and multi-file patches are staged and restored on a commit failure. Rename and binary patches are refused with an actionable error.

In `FullAccess`, `project_run_command` starts `powershell.exe` at the verified project root with `-NoProfile` and `-NonInteractive`. It defaults to 60 seconds, accepts a 1–300-second timeout, captures bounded stdout and stderr independently, reports the exit code and elapsed time, and requests tree termination on timeout or caller cancellation. Lucky reports whether it confirmed the PowerShell host exit; detached children cannot be independently proven gone. The initial directory is confined to the project root, but a raw PowerShell command is not an OS sandbox: it runs as the current Windows user and must be treated as an explicit Full access capability, with its trace visible to the user.

## Docker Code Execution Sandbox

Lucky also has an optional `sandbox_execute` tool for disposable code execution. It is disabled by default, appears only in `FullAccess`, and requires both an explicit enablement choice and a configured Docker image name. Lucky requires Docker's local Windows named-pipe daemon; it refuses explicit remote `DOCKER_HOST` values and non-default `DOCKER_CONTEXT` values rather than silently running code on another machine. Lucky first verifies that image in Docker's local cache, checks that it declares no Docker volumes, then runs with `--pull=never`; it never downloads an image on the user's behalf. If Docker Desktop is stopped, a remote Docker configuration is selected, the image is missing, or the image declares a volume, the failure is returned visibly in the tool trace and no container is started.

The sandbox runs a Unix `sh -lc` command inside the configured Linux image, overriding its entrypoint to `sh`. It has no network, a read-only container root, no Linux capabilities, `no-new-privileges`, a non-root UID, bounded CPU, memory, PID, file-descriptor, scratch, and execution timeout limits. `/scratch` is an in-container tmpfs that is discarded after the run. Lucky never mounts a host folder, including the selected project: sandbox code cannot read or write the workspace. A named container is removed automatically on exit and Lucky also requests forced removal after timeout or cancellation. Older saved project-mount preferences are migrated off and ignored.

This is a Docker-container boundary, not a claim of perfect VM-grade isolation or secure data erasure. Its effective isolation and resource controls still depend on the local Docker daemon, engine configuration, platform, and image. Use trusted, purpose-built local images; images need a compatible Unix `sh` and any runtime required by the command. The host `project_run_command` PowerShell tool remains explicitly unsandboxed.

Tool activity is returned as trace data and shown in the chat thinking/progress surface so Lucky does not silently pretend to have inspected or edited files. Parent turns are bounded to 12 rounds and 12 tool calls per model response; child turns are bounded separately (up to 8 calls per response). Lucky compacts old tool exchanges and tool schemas before every provider call, stops after three fully failed rounds, honors Stop/cancellation, serializes writable subagents, and asks for a final answer without tools on repeated successful calls or a round cap.

## Subagents

Lucky can delegate bounded work to subagents during a turn. Subagents use the same configured provider as the parent turn, inherit the current access level, and cannot exceed the selected project's safety boundaries. Automatic delegation is model-mediated: when enabled, Lucky exposes only auto-activating agents to the parent model for parallelizable work such as exploration, review, test triage, and documentation checks. Explicit-only agents, such as `worker`, appear when the user asks for subagents or names an agent. The user can disable subagents, disable auto-delegation, or cap the number of subagents in Settings.

Built-in subagents include `explorer`, `reviewer`, `tester`, `writer`, and `worker`. Custom JSON definitions can be added under:

```text
%LOCALAPPDATA%\Lucky\agents\*.json
<project>\.lucky\agents\*.json
```

Each user-owned definition can set a name, description, instructions, tool allowlist, model override, reasoning override, and access cap. Only user-level custom agents may explicitly allow FullAccess write/patch/PowerShell tools; writable children are serialized. Project-owned `.lucky/agents` files are treated as untrusted workspace content: they cannot override built-ins or user agents, are reparse-point checked, and are forced to explicit, read-only Workspace helpers. Subagent activity appears in the Thinking trace with `subagent.<name>` prefixes and returns compact summaries to the parent instead of dumping child transcripts into the main chat.

When workspace context is allowed and the selected project contains `AGENTS.md` or `AGENTS.override.md` at its root, Lucky loads that instruction file into the parent turn and every subagent run. This keeps project-specific working rules visible to all workspace-aware agents without granting broader filesystem tools.

## Access Levels

Lucky models access as a setting that is included in the system prompt for every turn.

- `ChatOnly`: use chat, global configured memories, and configured SearXNG search only. Lucky does not inject selected project path/name, project `AGENTS.md`, project memories, project custom agents, or project filesystem tools.
- `Workspace`: allow read-only project tools: list, read, and search inside the selected project.
- `FullAccess`: allow project write tools as well: create/overwrite, exact text replacement, unified-diff patching, and the visible PowerShell command runner; this is also the explicit gate for trusted page reading, user-configured MCP tools, and an explicitly configured Docker sandbox.

Project-changing prompts in `ChatOnly` or `Workspace` receive a direct message asking the user to switch to `FullAccess`, rather than giving the model unavailable write tools and waiting for a denial loop.

The assistant should never imply it performed filesystem, shell, browser, subagent, MCP, sandbox, or network actions unless Lucky explicitly supplied those tool results. Lucky has a bounded static page reader, stdio MCP host, an explicit FullAccess PowerShell runner, and an opt-in constrained Docker sandbox with clearly documented limits.

## Contributing

Contributions are welcome once you are comfortable building and running from source.

1. Read [AGENTS.md](AGENTS.md) for project boundaries and local-first rules.
2. Keep `README.md` and `docs/ARCHITECTURE.md` current when user-visible behavior changes.
3. Update `docs/QA.md` when provider setup, search, memory, persistence, or project-scoped chat behavior changes.
4. Add tests for risky core behavior (memory, providers, persistence, search, tools).
5. Never commit secrets, personal notes under `docs/private/`, or dumps of `%LOCALAPPDATA%\Lucky`.

## License

Lucky is released under the [MIT License](LICENSE).

## Security

Please report security issues privately as described in [SECURITY.md](SECURITY.md). Do not open public issues that include live API keys, tokens, or personal chat logs.
