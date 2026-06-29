# Lucky Architecture

Lucky is a Windows desktop LLM harness with a local-first core. The design separates the WinUI 3 shell from provider, memory, search, persistence, and agent orchestration so the product can grow without binding domain behavior to XAML screens.

## Architectural Goals

- Local-first state: projects, sessions, settings, and memories live under the current user's local app data.
- Provider flexibility: DeepSeek, LM Studio, and custom OpenAI-compatible servers share one provider interface.
- User-controlled search: web lookup flows through a configured SearXNG endpoint.
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
  ProjectFileToolService.cs   Safe project-scoped list/read/search/write/edit tools
  SearxngSearchClient.cs      SearXNG JSON search client
  MemoryService.cs            Memory capture, merge, retrieval, and scoring
  AgentInstructionsService.cs AGENTS.md discovery for parent and child agents
  SubagentDefinitionService.cs Built-in, state-backed, and file-backed subagent definitions
  SubagentCoordinator.cs      Bounded subagent fan-out/fan-in and child tool loops
  AgentRunner.cs              One user turn: memory, search, bounded tool loop, provider call, trace

Lucky.Tests
  Unit and behavior tests for core services
```

The app shell mirrors the Codex desktop layout: project/history rail, darker chat canvas, right-aligned user messages without a visible `You` label, left-aligned assistant output, selectable canvas text, a compact Thinking expander, rounded bottom composer, compact access control, a single model/reasoning picker, context/usage meter, automatic scroll-to-latest behavior, and a settings workspace with a left settings nav.

## Runtime State

`LuckyStore` persists `LuckyState` as JSON:

```text
%LOCALAPPDATA%\Lucky\lucky-state.json
```

Packaged WinUI runs can resolve that local app data location through the package LocalCache path, such as `%LOCALAPPDATA%\Packages\<LuckyPackage>\LocalCache\Local\Lucky\lucky-state.json`.

The state graph contains:

- `AppSettings`: persona, active provider, access level, selected project, SearXNG settings, context limits, and memory budgets.
- `SubagentSettings`: enablement, auto-delegation, per-turn and parallel caps, child tool-round caps, timeout, and state-backed custom agents.
- `Projects`: known workspace folders.
- `Sessions`: project-scoped chat sessions and messages.
- `Memories`: durable memory summaries with kind, tags, evidence, confidence, source session, and optional project id.

Writes use a temporary file and replacement/move to reduce the chance of corrupting the state file during save.

## Provider Layer

`ILlmClient` defines two operations:

- `ListModelsAsync`
- `CompleteChatAsync`, with optional OpenAI-compatible tool schemas and optional streamed deltas

`OpenAiCompatibleClient` implements those operations against:

- `GET {baseUrl}/models`
- `POST {baseUrl}/chat/completions`

Provider settings include display name, base URL, model name, API-key requirement, protected API key, thinking support, reasoning flag, reasoning effort, and context-window tokens. The WinUI layer exposes DeepSeek and LM Studio through combined `ModelOptionViewModel` entries so the composer and settings use one model picker. Defaults are:

- DeepSeek: `https://api.deepseek.com`, `deepseek-v4-pro`, API key required, thinking supported and enabled, 1,000,000 context tokens for v4 models.
- LM Studio: `http://127.0.0.1:1234/v1`, no API key required.
- Custom: `http://127.0.0.1:8000/v1`, no API key required by default.

API keys are unprotected only at call time through `CredentialProtector`. If a selected provider requires a key and none is configured, `AgentRunner` returns a user-facing setup message instead of calling the provider.

When `SupportsThinking` is true, `OpenAiCompatibleClient` sends `thinking: { type: "enabled" }` plus `reasoning_effort` for thinking mode, or `thinking: { type: "disabled" }` plus temperature when disabled. Providers without thinking support receive the portable payload.

Known provider capabilities are normalized by the store and app view model. DeepSeek v4 models are treated as thinking-capable and use a 1,000,000-token context meter. LM Studio entries disable thinking fields and keep the user-configured local context window.

Tool schemas are sent as OpenAI-compatible `tools` with `tool_choice: auto` only when tools are available for that model round. Assistant tool-call messages and `tool` result messages are serialized back into the next model round. Returned `tool_calls` are parsed into `ToolCallRequest` records so the core loop remains provider-neutral. When the agent loop needs to finalize after a loop guard, Lucky calls the provider with no `tools` and no `tool_choice` so the model must answer instead of continuing to request tools.

When the UI supplies stream progress, `OpenAiCompatibleClient` sends `stream: true` and parses server-sent events. Text deltas are reported immediately for token-by-token rendering, DeepSeek `reasoning_content` deltas are forwarded as actual Thinking text, and streamed `tool_calls` are accumulated by index before the agent loop executes them. Non-streaming calls still use the same response parser with `stream: false`.

Provider responses can include standard OpenAI-compatible `usage.prompt_tokens`, `usage.completion_tokens`, and `usage.total_tokens`. Non-streaming responses parse usage directly. For thinking-capable streamed responses, Lucky asks for `stream_options.include_usage` and captures usage-only chunks before skipping empty choices. `AgentRunner` aggregates usage across bounded tool-loop rounds so a turn with tool calls can report combined model input/output usage.

## SearXNG Search

`SearxngSearchClient` calls:

```text
{searxngUrl}/search?q={query}&format=json
```

It parses result title, URL, and snippet/content into `SearchResult`. `AgentRunner` uses search when:

- The user starts a prompt with `/web`.
- `AutoWebSearch` is enabled and the prompt looks current, for example latest, today, current, news, price, release, schedule, weather, verify, look up, or search for.

Automatic search is skipped for offline artifact/build prompts, such as creating a standalone HTML file, canvas game, script, app, or website, even if the prompt contains words like current. Explicit `/web` still searches.

Search failures become trace entries and do not fail the entire turn. If search succeeds but the model call fails, Lucky can still show the search results as a fallback.

## Memory System

`MemoryService` provides three responsibilities:

- Capture: extract durable memories from explicit `/remember` prompts, "remember that" phrases, preferences, identity statements, and naming corrections.
- Safety: skip messages that look like API keys, passwords, bearer tokens, secrets, or similar sensitive material.
- Retrieval: score enabled memories against the current message and return the best matches.

Captured memories are split into:

- `UserProfile`: user identity, preferences, naming corrections, and long-lived profile facts. These are shown to the model as `USER profile notes`.
- `Memory`: project/session facts and durable conversation notes. These are shown as `Relevant durable memories`.

Retrieval currently uses token vectors and cosine similarity, with boosts for:

- Same project id
- Global memories
- Pinned memories
- Confidence
- Recency

This is a lightweight semantic-memory layer rather than an embeddings store today. Future vector search should preserve the same `MemoryItem` contract unless a migration is needed.

`ContextEstimator` provides approximate text-token counts for older turns and providers that do not report usage, plus character totals for the USER and MEMORY settings budgets. When provider token usage is available on assistant messages, the chat meter prefers the latest reported total instead of the local estimate.

## Agent Turn Flow

`AgentRunner.RunTurnAsync` coordinates a single user turn:

1. Capture candidate memories from the user message.
2. Merge captured memories into state, de-duplicating normalized summaries.
3. Retrieve relevant memories for the selected project.
4. Reject obvious project-changing prompts unless the access level is `FullAccess`.
5. Load root project instructions from `AGENTS.override.md` or `AGENTS.md` when present.
6. Load built-in, state-backed, profile-file, and project-file subagent definitions.
7. Optionally run SearXNG search and append trace entries.
8. Resolve the active provider and unprotect its API key.
9. Build available tools from the selected project, access level, and subagent settings.
10. Build the model message list:
   - Persona and access level
   - Selected project name/path
   - Project AGENTS instructions
   - Available subagents and limits
   - USER profile notes and relevant durable memories
   - Search results
   - Recent user/assistant messages
   - Current user message
11. Run a bounded tool loop:
   - call the provider through `ILlmClient`, reporting streamed answer and reasoning deltas when available
   - execute requested tool calls if allowed
   - run `subagent_run` calls in isolated child contexts, concurrently up to the configured cap
   - append tool result messages
   - append visible trace entries
   - stop when the model returns final assistant text
   - detect repeated successful tool calls and finalize without tools
   - on the round cap, finalize without tools instead of returning only a safety-limit message
12. Return assistant text, trace entries, recalled memories, captured memories, provider reasoning text, provider token usage, and whether a model was used.

The system prompt includes a stable product style rule, separate from the user-editable persona, that asks for clean chat prose in Lucky's plain-text UI. The rule discourages decorative Markdown separators, routine heading hashes, excessive bold markers, and filler preambles while still allowing bullets or tables when they improve scanning.

The UI should surface trace and setup failures clearly enough that users know whether an answer came from the model, search fallback, or a missing-provider-key path. Tool output shown in Thinking is summarized for readability; large `project.read_file` results show the path, character count, and a short preview rather than the entire file contents.

The loop is capped so a provider cannot keep requesting tools indefinitely. Unknown tools, malformed arguments, denied access levels, unsafe paths, and filesystem failures become tool-result messages and trace entries rather than hidden crashes. Successful tool calls are tracked by normalized tool name and JSON arguments; if the provider asks for the same already-successful operation again, Lucky treats it as a loop signal and asks for a final answer with tools disabled.

## Subagent Orchestration

Lucky's subagent system keeps child work inside the same local-first harness boundaries as the parent turn.

`SubagentDefinitionService` loads built-in agents (`explorer`, `reviewer`, `tester`, `writer`, and `worker`), then overlays custom state-backed agents, personal JSON files from `%LOCALAPPDATA%\Lucky\agents\*.json`, and project JSON files from `<project>\.lucky\agents\*.json`. Definitions include a name, description, instructions, tool allowlist, optional model and reasoning overrides, auto-activation flag, and access cap. Project definitions win when names collide with broader definitions.

`AgentInstructionsService` reads only root project `AGENTS.override.md` or `AGENTS.md`, capped at 32 KiB and blocked on reparse points. The rendered instruction text is injected into the parent prompt and every child prompt so subagents follow the same project rules.

The parent model receives a `subagent_run` tool when subagents are enabled and either auto-delegation is enabled or the user explicitly asks for delegation. Each tool call names one agent and one self-contained task. If a provider returns multiple `subagent_run` calls in the same round, `AgentRunner` runs them concurrently through `SubagentCoordinator` up to `MaxParallelAgents`; `MaxAgentsPerTurn` caps total child runs for the turn.

Each child run uses `SubagentRunner`, which calls the same `ILlmClient` and `ProjectFileToolService` as the parent. Child agents inherit the active provider and API-key handling. A definition can request a model, reasoning effort, tools, or access cap, but the effective access is never higher than the parent turn's `HarnessAccessLevel`. Built-in agents use read/list/search tools only; custom agents may request write/edit tools, but those are available only when both the definition and parent turn allow `FullAccess`.

Subagent results return compact summaries to the parent as tool results. Child transcripts are not stored in chat history and are not captured as memory. Trace entries are prefixed as `subagent.<name>` and child tool traces are prefixed as `subagent.<name>.<tool>`, keeping the Thinking expander observable without dumping large intermediate output.

## Project File Tool Service

`ProjectFileToolService` is the safety boundary for filesystem actions. It resolves every tool path to an absolute path under the selected project root and rejects escapes. It blocks paths that cross reparse points, skips common generated directories, refuses large or binary files, and caps list/search output.

Available operations:

- `project_list_files`: list one directory or file inside the project.
- `project_read_file`: read one UTF-8 text file inside the project.
- `project_search`: search project text files with a regex or plain query.
- `project_write_file`: create or overwrite a UTF-8 text file inside the project.
- `project_edit_file`: replace exactly one old-text occurrence inside a project file.

## Project-Scoped Chats

Projects are keyed by normalized folder path. `LuckyStore.EnsureProject` reuses an existing project when the same path is opened, updates `LastOpenedAt`, and sets it as selected. New projects receive an initial chat session.

Sessions include `ProjectId`, title, timestamps, and messages. Memory capture receives the current project id and session id so later retrieval can prefer memories from the active workspace while still allowing global memories.

## Access Levels

`HarnessAccessLevel` is stored in settings and injected into the system prompt.

- `ChatOnly`: no implied workspace or external action access.
- `Workspace`: selected project context can be supplied by Lucky, and read-only project filesystem tools are available.
- `FullAccess`: project write/edit tools are available. Broader machine or shell access remains future work and should require clear UI affordances, confirmation for risky actions, and traceable tool results.

Requests that clearly require project writes, such as creating an HTML file or editing code, are short-circuited in `ChatOnly` and `Workspace` with a user-facing Full access message. Read-only prompts still flow through the model with the read/list/search tools allowed by the selected access level.

Subagents inherit the current access level and cannot escalate above it. In `ChatOnly`, Lucky may expose `subagent_run`, but child agents receive no project filesystem tools. In `Workspace`, child agents can receive read-only project tools. In `FullAccess`, custom child agents can receive write/edit tools only if their definition explicitly allows them.

Access levels are not a substitute for OS permissions. They are Lucky's product-level contract with the user and must be enforced by the app workflow as new tools are added.

## Error Handling Principles

- Provider errors should be returned as actionable assistant-facing messages.
- Search errors should be visible in trace but should not block model use.
- Missing required API keys should produce setup guidance, not failed HTTP calls.
- Corrupt or missing state should recover to a fresh state where possible.

## Contributor Documentation Rule

Any change that modifies user-visible behavior, state shape, provider configuration, SearXNG behavior, semantic memory, project-scoped chats, access levels, or architecture boundaries must update this document and `README.md` in the same change.
