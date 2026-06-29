# Lucky

Lucky is a WinUI 3 local-first LLM harness for Windows. It is designed to give a user one desktop place for project-scoped chats, configurable model providers, SearXNG-backed web search, durable semantic memory, explicit access levels, and real project filesystem tools.

The current repository has the core harness model in `src/Lucky.Core` and a Codex-inspired WinUI 3 shell in `src/Lucky.App`. The app layer has a glassy project/history rail, right-aligned user messages, left-aligned assistant output, a compact Thinking expander, selectable canvas text, a bottom composer with a single model/reasoning selector, access controls, a context/usage meter, automatic chat auto-scroll, and a full settings workspace.

## Goals

- Run as a native Windows desktop app using WinUI 3.
- Keep user state local by default.
- Support both remote and local OpenAI-compatible providers.
- Make DeepSeek and LM Studio first-class setup paths.
- Use a user-controlled SearXNG instance for web search.
- Scope chats to projects so conversations stay tied to the workspace they are about.
- Let the agent read, search, create, and edit files inside the selected project through traceable tools.
- Capture durable memory only when useful, avoid secrets, and retrieve relevant memories into future turns.
- Split memory into Hermes-style `USER` profile facts and `MEMORY` conversation/project facts with visible budgets.
- Expose clear access levels so users know what Lucky can and cannot touch.

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
```

## Requirements

- Windows 10 1809 or newer, with the Windows App SDK runtime required by the app package.
- .NET SDK matching the repository target framework.
- Optional: LM Studio for local model hosting.
- Optional: SearXNG for local or self-hosted web search.
- Optional: a DeepSeek API key for hosted model calls.

## Build

```powershell
dotnet restore Lucky.slnx
dotnet build Lucky.slnx
dotnet test Lucky.slnx
```

The WinUI app is packaged and references Windows App SDK tooling. Use the generated publish profiles under `src/Lucky.App/Properties/PublishProfiles` when packaging for a specific Windows architecture.

## Runtime State

Lucky stores application state outside the repository at:

```text
%LOCALAPPDATA%\Lucky\lucky-state.json
```

When running as a packaged WinUI app, Windows may redirect local app data through the package LocalCache path, for example `%LOCALAPPDATA%\Packages\<LuckyPackage>\LocalCache\Local\Lucky\lucky-state.json`.

That state includes settings, provider configuration, subagent settings, projects, chat sessions, and memory items. API keys are stored only after being protected for the current Windows user. The checked-in repository should not contain user state, transcripts, provider secrets, or local model artifacts.

## Provider Setup

Lucky uses an OpenAI-compatible chat-completions interface for all model providers.

Model calls stream when the provider supports chat-completions streaming. Lucky shows answer tokens as they arrive, keeps displayed answers clean for the plain chat canvas, tracks reasoning/thinking text separately when the provider supplies it, and still parses streamed tool calls so project actions can run before the final answer.

### LM Studio

Use LM Studio when you want model traffic to stay on the local machine.

1. Install LM Studio and download a chat-capable model.
2. Start the local server in LM Studio.
3. Confirm the server is listening at `http://127.0.0.1:1234/v1`.
4. In Lucky settings or the composer, choose the `LM Studio` entry from the combined model selector.
5. Refresh models when LM Studio is running, then set the context window to match the loaded model so the composer meter is meaningful.

The default LM Studio provider does not require an API key and does not use DeepSeek thinking parameters.

### DeepSeek

Use DeepSeek when you want hosted model calls.

1. Create a DeepSeek API key.
2. In Lucky settings or the composer, choose `DeepSeek V4 Flash` or `DeepSeek V4 Pro` from the combined model selector.
3. Use the default base URL `https://api.deepseek.com` unless DeepSeek changes its OpenAI-compatible endpoint.
4. Pick `High` or `Extra High` reasoning from the selector entry.
5. Store the API key through the settings UI so Lucky can protect it before writing state.
6. Use `Refresh models` to validate the connection; DeepSeek v4 models default the context meter to 1,000,000 tokens.

DeepSeek is configured as an API-key provider. For DeepSeek v4 options Lucky sends DeepSeek's `thinking` object and the selected `reasoning_effort`. When DeepSeek returns `reasoning_content`, Lucky shows that provider reasoning inside the message's Thinking expander rather than replacing it with generic status text.

## Chat Canvas

Lucky's chat canvas is optimized for a Codex/Hermes-style desktop flow:

- User messages sit on the right without a visible `You` label, with time at the bottom of the bubble.
- Assistant output sits on the left, with selectable answer text and selectable Thinking text.
- The Thinking expander is always labeled `Thinking`; it shows provider reasoning when available and appends visible tool/search trace entries. Large read-file payloads are summarized to path, size, and preview so generated files do not flood the chat canvas.
- The usage meter prefers provider-reported `prompt_tokens + completion_tokens` totals for the latest turn when available, and falls back to local estimates for older or unsupported provider responses.
- The assistant system prompt asks for clean chat prose, avoiding decorative separators, routine heading hashes, and excessive bold markers.

### Custom OpenAI-Compatible Provider

The core provider layer still supports a custom provider for other local or hosted servers that implement `/v1/models` and `/v1/chat/completions`. The current app UI focuses the first-run picker on DeepSeek and LM Studio.

Configure:

- Display name
- Base URL
- Model
- Whether an API key is required
- Optional reasoning settings

## SearXNG Setup

Lucky can search the web through a user-controlled SearXNG instance. The default URL is:

```text
http://127.0.0.1:8080
```

Lucky calls the JSON search endpoint and injects the top configured results into the model context. Users can explicitly search with `/web query`. Lucky may also search automatically for prompts that look current, such as requests involving latest releases, today's data, prices, schedules, weather, or news. Offline artifact/build prompts, such as requests to create a standalone HTML file or app, skip automatic web search unless the user explicitly starts the prompt with `/web`.

## Semantic Memory

Lucky memory is local and lightweight. The current core captures memories from explicit or preference-like user messages, skips likely secrets, and stores summaries with tags, evidence, confidence, project id, source session id, and memory kind.

Memory kinds:

- `USER`: identity, preferences, naming corrections, and long-lived profile facts.
- `MEMORY`: project/session facts and durable notes from chats.

Memory retrieval is lexical today: Lucky tokenizes the query, scores enabled memories, boosts project matches, boosts pinned memories, and adds a small confidence and recency component. The architecture calls this semantic memory because it is the durable memory layer that should evolve toward embeddings or another semantic index without changing the user-facing concept.

The settings page shows separate USER and MEMORY character budgets so memory does not silently become an unbounded prompt dump.

## Project-Scoped Chats

Projects represent workspace folders. A chat session belongs to one project id, and Lucky stores the selected project in app settings. When a project is opened, Lucky reuses the existing project record for that path or creates one with an initial chat.

Project scoping affects:

- Which chats are shown as relevant to the selected workspace.
- Which memories receive a project id when captured.
- Which memories get a retrieval boost on later turns.
- Which project name and path are supplied to the model.
- Which folder the filesystem tools may touch.

## Project Filesystem Tools

Lucky's agent loop can expose project-scoped file tools to the model. The first tool set is intentionally narrow:

- list files and folders
- read UTF-8 text files
- search project text files with a regex or plain query
- create or overwrite UTF-8 text files
- replace one exact text occurrence in a file

The tool service enforces path safety in code, not just in the prompt. Paths are resolved against the selected project, attempts to escape with `..` or absolute outside paths are rejected, common generated folders such as `.git`, `.vs`, `bin`, `obj`, and `node_modules` are skipped, and large or binary files are refused.

Tool activity is returned as trace data and shown in the chat thinking/progress surface so Lucky does not silently pretend to have inspected or edited files. If a provider repeats a tool call that already succeeded, or reaches the bounded tool-round cap, Lucky asks for one final answer with tools disabled and reports completed file operations instead of ending with a generic loop-limit message.

## Subagents

Lucky can delegate bounded work to subagents during a turn. Subagents use the same configured provider as the parent turn, inherit the current access level, and cannot exceed the selected project's safety boundaries. Automatic delegation is enabled by default for parallelizable work such as exploration, review, test triage, and documentation checks. The user can disable subagents, disable auto-delegation, or cap the number of subagents in Settings.

Built-in subagents include `explorer`, `reviewer`, `tester`, `writer`, and `worker`. Custom JSON definitions can be added under:

```text
%LOCALAPPDATA%\Lucky\agents\*.json
<project>\.lucky\agents\*.json
```

Each definition can set a name, description, instructions, tool allowlist, model override, reasoning override, and access cap. Project custom agents override broader definitions with the same name. Subagent activity appears in the Thinking trace with `subagent.<name>` prefixes and returns compact summaries to the parent instead of dumping child transcripts into the main chat.

When a selected project contains `AGENTS.md` or `AGENTS.override.md` at its root, Lucky loads that instruction file into the parent turn and every subagent run. This keeps project-specific working rules visible to all agents without granting broader filesystem tools.

## Access Levels

Lucky models access as a setting that is included in the system prompt for every turn.

- `ChatOnly`: use chat, configured memories, and supplied search results only.
- `Workspace`: allow read-only project tools: list, read, and search inside the selected project.
- `FullAccess`: allow project write tools as well: create/overwrite and exact text replacement inside the selected project.

Project-changing prompts in `ChatOnly` or `Workspace` receive a direct message asking the user to switch to `FullAccess`, rather than giving the model unavailable write tools and waiting for a denial loop.

The assistant should never imply it performed filesystem, shell, browser, subagent, or network actions unless Lucky explicitly supplied those tool results. Shell, browser, and whole-machine tools are not part of this first filesystem tool layer.

## Contributor Rule

Keep `README.md` and `docs/ARCHITECTURE.md` current. If a change affects goals, provider setup, SearXNG, memory, project-scoped chats, access levels, persistence, or user-visible workflows, update the docs in the same change.
