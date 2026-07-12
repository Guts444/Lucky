# Lucky Architecture

Lucky is a Windows desktop LLM harness with a local-first core. The design separates the WinUI 3 shell from provider, memory, search, persistence, and agent orchestration so the product can grow without binding domain behavior to XAML screens.

## Architectural Goals

- Local-first state: projects, sessions, settings, and memories live under the current user's local app data.
- Provider flexibility: DeepSeek, LM Studio, custom OpenAI-compatible servers, and ChatGPT-backed Codex subscriptions share one Lucky provider interface.
- User-controlled search: web lookup flows through a configured SearXNG endpoint.
- Explicit external capabilities: trusted static page reading, MCP, and Docker sandbox execution are disabled until the user configures and enables them.
- Project-scoped context: chats and memories can be tied to workspace folders.
- Project-scoped action: file reads, searches, creates, and edits are exposed as bounded tools.
- Bounded delegation: subagents can split isolated work while preserving local-first provider, access, and trace rules.
- Transparent capability boundaries: access levels are included in the model context and should be reflected in the UI.
- Evolvable memory: the durable memory concept should support better semantic indexing over time without rewriting the chat model.

## High-Level Components

```text
Lucky.App
  WinUI 3 shell, navigation, views, view models, settings UI, project selection UI

Lucky.Core
  Models.cs                   State, settings, providers, projects, sessions, memories
  LuckyStore.cs               Local JSON persistence
  CredentialProtector.cs      Current-user DPAPI secret protection
  ContextEstimator.cs         Fallback token/character estimates for UI meters
  OpenAiCompatibleClient.cs   /models and /chat/completions provider calls
  CodexAppServerClient.cs     Official local Codex app-server OAuth/model/tool bridge
  ProjectFileToolService.cs   Safe project-scoped list/read/search/write/edit/patch tools
  ProjectTerminalToolService.cs Bounded FullAccess Windows PowerShell command runner
  DockerCodeExecutionSandboxService.cs Opt-in constrained local-Docker code runner
  SearxngSearchClient.cs      SearXNG JSON search client
  WebPageReader.cs            Cookie-free trusted-domain static page reader
  McpToolService.cs            Per-turn local stdio MCP client and tool bridge
  CommandLineArgumentParser.cs Shell-free MCP argument parser for settings UI
  MemoryService.cs            Memory capture, merge, retrieval, and scoring
  AgentInstructionsService.cs AGENTS.md discovery for parent and child agents
  SubagentDefinitionService.cs Built-in, state-backed, and file-backed subagent definitions
  SubagentCoordinator.cs      Bounded subagent fan-out/fan-in and child tool loops
  AgentRunner.cs              One user turn: memory, search, bounded tool loop, provider call, trace

Lucky.Tests
  Unit and behavior tests for core services
```

The app shell mirrors the Codex desktop layout: project/history rail, darker chat canvas, right-aligned user messages without a visible `You` label, left-aligned assistant output, selectable canvas text, a compact Thinking expander, a one-tone rounded multiline composer with compact pill/circle controls, a single chat-only model/reasoning picker, context/usage meter, follow-latest scrolling that stops when the user scrolls upward, a clickable empty-state working-folder path, and a settings workspace with provider setup separated into subscription and API/local groups.

## Public repository boundaries

Lucky is developed in a public repository. Contributor-facing product docs live in `README.md`, `docs/ARCHITECTURE.md`, and `docs/QA.md`. Local-only notes may be kept under `docs/private/` or `*.local.md` (gitignored). Runtime state, API keys, OAuth material, and chat transcripts must never be committed; they belong under the Windows user profile. See `SECURITY.md` for reporting and hardening expectations.

## Runtime State

`LuckyStore` persists `LuckyState` as JSON:

```text
%LOCALAPPDATA%\Lucky\lucky-state.json
```

Packaged WinUI runs can resolve that local app data location through the package LocalCache path, such as `%LOCALAPPDATA%\Packages\<LuckyPackage>\LocalCache\Local\Lucky\lucky-state.json`.

The state graph contains:

- `AppSettings`: persona, active provider, access level, selected project, SearXNG settings, trusted-page-reader settings, MCP settings, memory enablement, context limits, and memory budgets.
- `WebBrowserSettings`: opt-in flag, trusted domains, and bounded page-text length.
- `McpSettings`: opt-in flag, request/output limits, and a local list of `McpServerDefinition` values. Display names and enabled state are normal settings; each stdio command, argument list, and working directory is persisted as one current-user-DPAPI-protected launch configuration and restored only in memory for the current Windows user.
- `SubagentSettings`: enablement, auto-delegation, per-turn and parallel caps, child tool-round caps, timeout, and state-backed custom agents.
- `Projects`: known workspace folders.
- `Sessions`: project-scoped chat sessions and messages.
- `Memories`: durable memory summaries with kind, tags, evidence, confidence, source session, and optional project id.

Writes are serialized through a per-store gate and use a temporary file plus replacement/move to reduce concurrent-save races and the chance of corrupting state.

## Provider Layer

`ILlmClient` defines two operations:

- `ListModelsAsync`
- `CompleteChatAsync`, with optional OpenAI-compatible tool schemas and optional streamed deltas

`OpenAiCompatibleClient` implements those operations against:

- `GET {baseUrl}/models`
- `POST {baseUrl}/chat/completions`

Provider settings include display name, transport, base URL where applicable, model name, API-key requirement, protected API key, thinking support, reasoning flag, reasoning effort, effective input-context tokens, and cached provider model capabilities. Provider setup is independent from active chat selection. Settings edits accounts/endpoints/keys and refreshes catalogs; only the composer changes the active model/reasoning option. Defaults are:

- DeepSeek: `https://api.deepseek.com`, `deepseek-v4-pro`, API key required, thinking supported and enabled, 1,000,000 context tokens for v4 models.
- LM Studio: `http://127.0.0.1:1234/v1`, no API key required.
- Custom: `http://127.0.0.1:8000/v1`, no API key required by default.
- OpenAI Codex: local official app-server transport, ChatGPT OAuth managed by Codex rather than Lucky, `gpt-5.5` default, and a safe 258,400-token default input budget until live metadata is available.

API keys are unprotected only at call time through `CredentialProtector`. If a selected provider requires a key and none is configured, `AgentRunner` returns a user-facing setup message instead of calling the provider.

When `SupportsThinking` is true, `OpenAiCompatibleClient` sends `thinking: { type: "enabled" }` plus `reasoning_effort` for thinking mode, or `thinking: { type: "disabled" }` plus temperature when disabled. Providers without thinking support receive the portable payload.

Known provider capabilities are normalized by the store and app view model. DeepSeek v4 models are treated as thinking-capable and use a 1,000,000-token context meter. LM Studio entries disable thinking fields and keep the user-configured local context window. For Codex, `CodexAppServerClient` reuses the authenticated account connection and pages through all visible entries from the official `model/list` catalog. It preserves each advertised reasoning-effort order and treats the effective input budget as the meter limit. It deliberately does not merge another app's/global Codex cache or synthesize unadvertised model ids, which preserves account isolation and avoids offering models the current Lucky sign-in may not be permitted to use. Windows Credential Manager's value limit is smaller than the ChatGPT OAuth bundle, so Lucky serializes helper startup, briefly materializes Codex's file credential, and immediately replaces it with a current-user DPAPI-protected `auth.json.dpapi` blob after initialization and token refreshes.

Tool schemas are sent as OpenAI-compatible `tools`; `tool_choice: auto` is used where supported and deliberately omitted for DeepSeek V4 thinking mode. Assistant tool-call messages, required `reasoning_content`, and `tool` results are serialized into the next round. Native `tool_calls` are parsed into provider-neutral `ToolCallRequest` records. A bounded compatibility parser also converts DeepSeek textual DSML only when the named tool was exposed in that request, and strips protocol markup from chat content. When a loop guard finalizes, Lucky calls the provider with no tools so the model must answer.

When the UI supplies stream progress, `OpenAiCompatibleClient` sends `stream: true` and parses server-sent events. With tools available, answer text is buffered until Lucky can distinguish prose from textual tool protocol; clean final prose is then revealed while DSML is executed rather than rendered. DeepSeek `reasoning_content` still streams to Thinking, and native `tool_calls` accumulate by index before execution.

Provider responses can include standard OpenAI-compatible `usage.prompt_tokens`, `usage.completion_tokens`, and `usage.total_tokens`. Non-streaming responses parse usage directly. For thinking-capable streamed responses, Lucky asks for `stream_options.include_usage` and captures usage-only chunks before skipping empty choices. `AgentRunner` aggregates billed input/output usage across bounded tool-loop rounds, while carrying the latest request's input-context value separately for the composer meter.

`CodexAppServerClient` launches `codex app-server --stdio`, initializes the documented local JSON-RPC protocol, and starts managed ChatGPT browser OAuth without receiving raw OAuth material. Every process receives a Lucky-owned `CODEX_HOME`, separate from the user's global Codex state. Lucky writes a restrictive managed config there that disables native shell, web search, apps, hooks, memories, remote plugins, and multi-agent features. Each turn also uses an ephemeral neutral directory, `read-only` sandbox, and `untrusted` approval policy. Lucky exposes selected-project tools as dynamic tools; their results return through the existing tool loop so access levels and traces remain authoritative.

## SearXNG Search

`SearxngSearchClient` calls:

```text
{searxngUrl}/search?q={query}&format=json
```

It parses result title, URL, and snippet/content into `SearchResult`. `AgentRunner` uses search when:

- The user starts a prompt with `/web`.
- `AutoWebSearch` is enabled and the prompt looks current, for example latest, today, current, news, price, release, schedule, weather, verify, look up, or search for.
- The parent model calls the `web_search` tool during the tool loop.

Automatic search is skipped for offline artifact/build prompts, such as creating a standalone HTML file, canvas game, script, app, or website, even if the prompt contains words like current. Explicit `/web` still searches.

Search failures become trace entries and do not fail the entire turn. If search succeeds but the model call fails, Lucky can still show the search results as a fallback.

The system prompt tells the model that SearXNG is Lucky's web access path. Prefetched `/web` or automatic results are real web results for that turn, and `web_search` is the live search tool when the model needs a fresh query.

## Trusted Page Reader

`WebPageReader` provides the `web_open` tool only when all of these conditions are true:

1. `WebBrowserSettings.Enabled` is true.
2. At least one trusted domain is configured.
3. The turn has `FullAccess`.

It performs a cookie-free HTTP(S) GET with redirects disabled at the transport layer and handled manually. The initial URL and every redirect must match an exact trusted domain or a subdomain of one, use a default HTTP/HTTPS port, and resolve without loopback/private/link-local/reserved IP addresses. It rejects non-text content, caps downloads at 2 MiB, caps titles and model text, removes common non-readable HTML elements, and turns regex timeouts into visible tool errors. It is intentionally not an interactive browser: it does not reuse a logged-in browser session, run form actions, or carry cookies. Browser automation can instead be offered by an explicitly trusted MCP server.

## MCP Tool Host

`McpToolService` is a local stdio MCP client. When MCP is enabled during a `FullAccess` parent turn, `AgentRunner` opens a short-lived session for each enabled server, launches its DPAPI-restored configured command without a shell, sends `initialize` and `notifications/initialized`, walks paged `tools/list` results, then exposes the discovered tools to the model. Tool names are namespaced into safe, unique `mcp_*` function names. Nested MCP input schemas are preserved when their bounded size fits the provider request; otherwise Lucky falls back to the tool's bounded flat parameter description.

MCP stdout protocol frames are capped at 256 KiB; an oversized frame terminates the configured server instead of being parsed. Stderr lines, cursors, tool names/descriptions/schemas, content-item counts, text, and structured result JSON are all bounded before model exposure. Each `tools/call` response is returned to the model as bounded text and optional structured JSON. Binary image/audio payloads are not inserted into the model context. MCP startup and call outcomes become visible trace entries. Servers are terminated when the turn finishes or is cancelled; caller cancellation is propagated rather than converted into an ordinary tool failure.

This first host supports stdio tool servers and common protocol versions from `2024-11-05` through `2025-11-25`. It intentionally does not yet implement Streamable HTTP, OAuth, prompt/resource APIs, server-initiated requests, or subscriptions/tool-list-change notifications. MCP is parent-turn-only; subagents do not inherit MCP tools.

## Memory System

`MemoryService` provides three responsibilities:

- Capture: extract durable memories from explicit `/remember` prompts, "remember that" phrases, preferences, identity statements, and naming corrections.
- Safety: skip messages that look like API keys, passwords, bearer tokens, secrets, or similar sensitive material.
- Retrieval: score enabled memories against the current message and return the best matches.

`AppSettings.MemoriesEnabled` gates both capture and retrieval. When memories are disabled, Lucky leaves existing memory items in local state but does not capture new memories or inject recalled memories into the model prompt.

Captured memories are split into:

- `UserProfile`: user identity, preferences, naming corrections, and long-lived profile facts. These are shown to the model as `USER profile notes`.
- `Memory`: project/session facts and durable conversation notes. These are shown as `Relevant durable memories`.

Retrieval currently uses token vectors and cosine similarity, with boosts for:

- Same project id
- Global memories
- Pinned memories
- Confidence
- Recency

Retrieval also filters by scope before scoring: global memories are always eligible when memory is enabled, while project memories are eligible only when the current turn is allowed to use that project context.

This is a lightweight durable-memory layer rather than an embeddings store today. Future vector search should preserve the same `MemoryItem` contract unless a migration is needed.

`ContextEstimator` provides approximate text-token counts, including lightweight chat-message framing, for older turns and providers that do not report usage, plus character totals for the USER and MEMORY settings budgets. The composer meter prefers the latest provider-reported input-context count only when it was reported by the active provider/model, not the assistant turn's aggregate/billed total; `~` marks a fallback estimate after a model switch or when no exact reading exists. Prompt assembly enforces `UserProfileCharLimit` and `MemoryCharLimit` separately when rendering recalled memory sections.

## Agent Turn Flow

`AgentRunner.RunTurnAsync` coordinates a single user turn:

1. Resolve the effective workspace context. In `ChatOnly`, the effective project is null even if a project is selected.
2. If memories are enabled, capture candidate memories from the user message.
3. If memories are enabled, merge captured memories into state and retrieve in-scope memories.
4. Reject obvious project-changing prompts unless the access level is `FullAccess`.
5. Load root project instructions from `AGENTS.override.md` or `AGENTS.md` when the effective project is available.
6. Load built-in, state-backed, profile-file, and project-file subagent definitions. Project-file definitions load only when the effective project is available.
7. Optionally run SearXNG search and append trace entries.
8. Resolve the active provider and unprotect its API key.
9. In `FullAccess`, optionally open short-lived configured stdio MCP sessions and append startup trace entries.
10. Build available tools from the effective project, access level, SearXNG/page-reader settings, discovered MCP tools, explicit sandbox settings, and subagent settings.
11. Build the model message list:
   - Persona and access level
   - Selected project name/path when workspace context is allowed
   - Project AGENTS instructions when workspace context is allowed
   - Available subagents and limits, filtered by auto-activation unless the user explicitly asked for subagents
   - USER profile notes and relevant durable memories within their configured prompt budgets
   - Search results
   - Recent user/assistant messages
   - Current user message
12. Run a bounded tool loop:
   - compact older complete turns/tool exchanges and bound tool definitions before every provider call, reserving configured context for tool schemas and a completion
   - call the provider through `ILlmClient`, reporting streamed answer and reasoning deltas when available
   - execute requested tool calls if allowed
   - run `subagent_run` calls in isolated child contexts, concurrently up to the configured cap; any child that can write, patch, or run PowerShell is serialized with other mutable children
   - append tool result messages
   - append visible trace entries
   - stop when the model returns final assistant text
   - reject excess calls after 12 parent calls in one response (8 in a child) with a visible tool result, detect repeated successful calls, and finalize without tools
   - finalize after three consecutive fully failed tool rounds
   - on the round cap, finalize without tools instead of returning only a safety-limit message
13. Dispose any MCP sessions and return assistant text, trace entries, recalled memories, captured memories, provider reasoning text, provider token usage, and whether a model was used.

The system prompt includes a stable product style rule, separate from the user-editable persona, that asks for clean chat prose in Lucky's plain-text UI. The rule discourages decorative Markdown separators, routine heading hashes, excessive bold markers, and filler preambles while still allowing bullets or tables when they improve scanning.

The UI should surface trace and setup failures clearly enough that users know whether an answer came from the model, search fallback, or a missing-provider-key path. Tool output shown in Thinking is summarized for readability; large `project.read_file` results show the path, character count, and a short preview rather than the entire file contents.

The parent loop is capped at 12 rounds, child loops at their configured 1–8 rounds, and each response is capped at 12 parent or 8 child tool calls. Before every provider/finalization call, Lucky rebudgets messages, tool-call payloads, tool outputs, and exposed schemas against the configured context. Unknown tools, malformed arguments, denied access levels, unsafe paths, and filesystem failures become tool-result messages and trace entries rather than hidden crashes. Three fully failed rounds force finalization. Successful tool calls are tracked by normalized tool name and JSON arguments; if the provider asks for the same already-successful operation again, Lucky treats it as a loop signal and asks for a final answer with tools disabled.

## Subagent Orchestration

Lucky's subagent system keeps child work inside the same local-first harness boundaries as the parent turn.

`SubagentDefinitionService` loads built-in agents (`explorer`, `reviewer`, `tester`, `writer`, and `worker`), then user-owned state-backed agents and personal JSON files from `%LOCALAPPDATA%\Lucky\agents\*.json`. Project JSON files from `<project>\.lucky\agents\*.json` are optional untrusted workspace content: the directory and files are reparse-point checked, project definitions cannot replace an existing built-in or user definition, and their effective policy is explicit-only read-only `Workspace` access. Definitions include a name, description, instructions, tool allowlist, optional model and reasoning overrides, auto-activation flag, and access cap.

When workspace context is available, `AgentInstructionsService` reads only root project `AGENTS.override.md` or `AGENTS.md`, capped at 32 KiB and blocked on reparse points. The rendered instruction text is injected into the parent prompt and every child prompt so subagents follow the same project rules.

The parent model receives a `subagent_run` tool when subagents are enabled and the active catalog is non-empty. If auto-delegation is enabled and the user did not explicitly ask for subagents, the active catalog includes only definitions with `AutoActivate = true`. When the user explicitly asks for subagents or names agents, explicit-only definitions such as `worker` are included too. Each tool call names one agent and one self-contained task. If a provider returns multiple `subagent_run` calls in the same round, `AgentRunner` runs read-only children concurrently up to `MaxParallelAgents`; any child allowed to mutate the workspace is serialized. `MaxAgentsPerTurn` caps total child runs for the turn.

Each child run uses `SubagentRunner`, which calls the same `ILlmClient`, `ProjectFileToolService`, and optional terminal service as the parent. Child agents inherit the active provider and API-key handling. A definition can request a model, reasoning effort, tools, or access cap, but the effective access is never higher than the parent turn's `HarnessAccessLevel`. Built-in and project-file agents use read/list/search tools only; user-owned custom agents may explicitly request write/edit/patch/PowerShell tools, but those are available only when both the definition and parent turn allow `FullAccess`.

Subagent results return compact summaries to the parent as tool results. Child transcripts are not stored in chat history and are not captured as memory. Trace entries are prefixed as `subagent.<name>` and child tool traces are prefixed as `subagent.<name>.<tool>`, keeping the Thinking expander observable without dumping large intermediate output.

## Project File Tool Service

`ProjectFileToolService` is the safety boundary for filesystem actions. It resolves every tool path to an absolute path under the selected project root and rejects escapes. It blocks paths that cross reparse points, skips common generated directories, refuses large or binary files, and caps list/search output.

Available operations:

- `project_list_files`: list one directory or file inside the project.
- `project_read_file`: read one UTF-8 text file inside the project.
- `project_search`: search project text files with a regex or plain query.
- `project_write_file`: create or overwrite a UTF-8 text file inside the project.
- `project_edit_file`: replace exactly one old-text occurrence inside a project file.
- `project_apply_patch`: apply a text-only unified diff, including create, modify, or delete sections. Lucky validates every file and hunk before a commit; it can use a single unique nearby-context match for a stale hunk position, stages writes, and attempts rollback if a commit fails. Binary and rename patches are rejected.

## Project Terminal Tool Service

`ProjectTerminalToolService` implements `project_run_command` for parent turns at `FullAccess`. It starts `powershell.exe` from the verified project root using `-NoLogo`, `-NoProfile`, and `-NonInteractive`. Commands have a default 60-second timeout, accept only 1–300 seconds, capture up to 48 KiB each of stdout and stderr while continuing to drain both streams, and return the exit code plus elapsed time in a visible tool trace. On timeout or caller cancellation, Lucky requests process-tree termination and reports whether it confirmed the PowerShell host exit; detached child processes cannot be independently verified.

The verified working directory prevents a path parameter from redirecting startup outside the workspace, but it is not a Windows security sandbox: a raw command still executes with the current user's permissions. The Full access selection and trace are therefore the product-level consent and observability boundary. It must never be described as the Docker sandbox below.

## Docker Code Execution Sandbox

`DockerCodeExecutionSandboxService` implements `sandbox_execute` only when the turn is `FullAccess`, the user has explicitly enabled the sandbox, and a non-empty image name is configured. It is not offered to subagents. The service requires Windows and forces Docker's local `npipe:////./pipe/docker_engine` endpoint; it clears Docker connection environment variables, rejects a remote `DOCKER_HOST`, and rejects a non-default `DOCKER_CONTEXT`. Before executing, it uses `docker image inspect --format '{{json .Config.Volumes}}'` to verify the image exists in the local cache and declares no Docker volumes, then invokes `docker run --pull=never`; Lucky does not pull, build, or update images automatically. Docker Desktop/client failures, remote-context configuration, missing local images, malformed volume metadata, and declared image volumes are returned as visible tool errors before a container run begins.

The service invokes the Docker CLI through `ProcessStartInfo.ArgumentList`, never a host command shell. Each run uses a generated name and `--rm`, `--network=none`, `--read-only`, `--cap-drop=ALL`, `--security-opt=no-new-privileges=true`, a non-root `65532:65532` user, CPU/memory/swap/PID/file-descriptor limits, fixed `/dev/shm`, and bounded tmpfs mounts. `/tmp` is non-executable; `/scratch` is a disposable in-container tmpfs work directory. The command is executed as `sh -lc` inside the chosen Linux image after forcing the image entrypoint to `sh`. There are no host mounts at all (including no project mount), device, privileged, host-PID, host-network, writable-host-volume, or forwarded-host-environment flags. Rejecting image-declared volumes prevents Docker from creating writable anonymous volume storage that would bypass the read-only-root and disposable-scratch contract.

No host path is ever mounted. The former optional read-only project mount is retired because a filesystem reparse-point scan cannot remove bind-mount time-of-check/time-of-use risk; persisted legacy preferences are reset to false during state load. Timeout or cancellation attempts to stop the Docker CLI and requests `docker container rm --force <generated-name>` with an independent cleanup timeout; the visible result says when Docker does not confirm removal. Standard output and standard error are independently capped at 48 KiB.

This is a constrained container boundary, not a guarantee of VM-grade isolation, secure deletion, or universal resource enforcement. Docker daemon configuration, Docker Desktop/host integration, the selected image, and platform support affect the result; operators should use trusted local images and understand Docker's own security model. In particular, the app does not claim that `project_run_command` is sandboxed.

## Project-Scoped Chats

Projects are keyed by normalized folder path. `LuckyStore.EnsureProject` reuses an existing project when the same path is opened, updates `LastOpenedAt`, and sets it as selected. New projects receive an initial chat session.

The empty chat state binds its working-folder button to the selected project's path and routes it through the same folder-picker flow as the project rail. Selecting a different folder therefore reuses the normal project identity, persistence, and initial-session rules rather than mutating a project path in place.

Sessions include `ProjectId`, title, timestamps, and messages. Memory capture receives the effective project id and session id so later retrieval can prefer memories from the active workspace while still allowing global memories. In `ChatOnly`, the effective project id is null.

## Access Levels

`HarnessAccessLevel` is stored in settings and injected into the system prompt.

- `ChatOnly`: no implied workspace or broad external action access. Lucky may use configured SearXNG search, but it does not inject selected project name/path, project root instructions, project-scoped memories, project file-backed subagents, project filesystem tools, trusted page reading, or MCP tools.
- `Workspace`: selected project context can be supplied by Lucky, and read-only project filesystem tools are available.
- `FullAccess`: project write/edit/unified-patch tools and the explicit PowerShell command runner are available, along with explicitly enabled trusted-page-reader and stdio MCP tools. An explicitly configured local Docker sandbox can also be exposed as `sandbox_execute`; it is not delegated to subagents. MCP server commands and sandbox image names are user configuration, never model-provided. The PowerShell runner starts at the selected project root and is traceable but not OS-isolated; the Docker sandbox has the separate constrained-container and limitation contract above.

Requests that clearly require project writes, such as creating an HTML file or editing code, are short-circuited in `ChatOnly` and `Workspace` with a user-facing Full access message. Read-only prompts still flow through the model with the read/list/search tools allowed by the selected access level.

Subagents inherit the current access level and cannot escalate above it. In `ChatOnly`, Lucky may expose `subagent_run`, but child agents receive no project context or project filesystem tools. In `Workspace`, child agents can receive read-only project tools. In `FullAccess`, user-owned custom child agents can receive write/edit/patch/PowerShell tools only if their definition explicitly allows them; project-file children remain explicit, read-only helpers.

Access levels are not a substitute for OS permissions. They are Lucky's product-level contract with the user and must be enforced by the app workflow as new tools are added.

## Error Handling Principles

- Provider errors should be returned as actionable assistant-facing messages.
- Search errors should be visible in trace but should not block model use.
- Missing required API keys should produce setup guidance, not failed HTTP calls.
- Corrupt or missing state should recover to a fresh state where possible.

## Contributor Documentation Rule

Any change that modifies user-visible behavior, state shape, provider configuration, SearXNG behavior, durable memory, project-scoped chats, access levels, or architecture boundaries must update this document and `README.md` in the same change.
