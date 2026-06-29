# Lucky QA Checklist

Use this checklist for manual verification when changes affect the WinUI app shell, provider setup, SearXNG, memory, project-scoped chats, access levels, or persistence.

## Build And Test

```powershell
dotnet restore Lucky.slnx
dotnet build Lucky.slnx
dotnet test Lucky.slnx
```

Verified on 2026-06-29:

```powershell
dotnet build src\Lucky.App\Lucky.App.csproj -p:Platform=x64
dotnet test Lucky.slnx
```

Latest verified results:

- WinUI x64 app build passed with 0 warnings and 0 errors.
- xUnit passed 40/40 focused tests, including streamed answer deltas, streamed reasoning deltas, streamed tool-call parsing, token usage parsing, answer display cleanup, repeated successful tool-call loop finalization, offline artifact auto-search skipping, no-tools finalization payloads, AGENTS.md prompt injection, subagent execution, access inheritance, and custom subagent discovery.
- Computer Use visual QA launched Lucky and verified the Codex-like shell renders with the glassy project/history rail, darker chat workspace, centered empty state, rounded bottom composer, a single model/reasoning picker, access control, and a `0/1M` DeepSeek context meter.
- Computer Use visual QA launched the patched x64 build and verified right-aligned user messages no longer show a visible `You` label, the user timestamp sits at the bottom of the bubble, assistant output stays left-aligned, the expander label is `Thinking`, and the Xbox answer no longer renders raw `##`, `###`, `---`, or stray bold markers.
- Computer Use visual QA verified assistant canvas text can be drag-selected for copy, with Windows reporting selected text from the chat answer.
- Computer Use visual QA verified the composer uses responsive max-width constraints and the usage meter now sits inside the composer instead of as a bottom-right overlay. Direct edge/corner resize drags were ignored by Windows during this pass, so narrow-width behavior was validated by code constraints plus the previously problematic composer no longer clipping in the captured available canvas.
- Computer Use visual QA verified Settings only opens from the bottom-left profile row, the top-right settings button is absent, subscription/plus copy is absent, and the composer no longer shows stray `Settings` text.
- Computer Use visual QA opened Settings and verified the trimmed settings navigation, General/work-mode cards, combined DeepSeek/LM Studio model picker, endpoint, 1,000,000-token context field, permissions, personalization, memory, and SearXNG sections render without overlap.
- Computer Use visual QA opened Settings after the subagent update and verified the new Subagents card renders cleanly with enable/auto-delegate toggles, max parallel/per-turn number boxes, custom-agent path text, Save button, and no overlap with adjacent sections.
- DeepSeek API smoke test used the protected saved key and received `flash-ok` from `deepseek-v4-flash` and `pro-ok` from `deepseek-v4-pro`.
- Computer Use sent an end-to-end Lucky composer prompt through DeepSeek V4 Flash and verified the assistant response rendered in the chat canvas as `lucky-ui-ok`.
- DeepSeek agent-tool smoke test used a temporary project folder. `deepseek-v4-flash` streamed answer chunks while reading `README.md` through `project.read_file`; `deepseek-v4-pro` streamed answer chunks while using `project.read_file` and `project.edit_file` to change `before edit` to `after edit`.
- Computer Use visual QA verified the current x64 build auto-scrolls the chat canvas to the latest loaded messages, including the `lucky-autoscroll-fixed` and `say ok` responses, when launched with `dotnet run -p:Platform=x64`.

## Provider Checks

- LM Studio: with the local server running at `http://127.0.0.1:1234/v1`, Lucky can list models and complete a chat without an API key.
- DeepSeek: without an API key, Lucky shows setup guidance and does not call the provider.
- DeepSeek: with a protected API key and model configured, Lucky can complete a chat through the OpenAI-compatible endpoint.
- DeepSeek: `deepseek-v4-flash` and `deepseek-v4-pro` both work with Lucky's thinking payload and reasoning effort.
- DeepSeek thinking mode sends `thinking` and `reasoning_effort`; portable providers do not receive DeepSeek-only fields.
- DeepSeek reasoning text appears inside the message's `Thinking` expander when `reasoning_content` is returned.
- Provider token usage, when returned, updates the chat meter with combined prompt/completion usage for the latest turn; unsupported providers fall back to local estimates.
- DeepSeek v4 saved states with stale 131K context values migrate to a 1,000,000-token context meter.
- Custom provider: base URL, model, and API-key requirement are respected.
- Provider errors produce user-facing messages that include enough detail to fix configuration without exposing secrets.

## SearXNG Checks

- `/web test query` calls the configured SearXNG URL and returns or injects JSON results.
- Automatic web search triggers for current-looking prompts when `AutoWebSearch` is enabled.
- Automatic web search does not trigger for offline artifact/build prompts such as creating a standalone HTML canvas game, unless the user explicitly uses `/web`.
- A current-news prompt such as `give me the latest xbox news` renders cleanly without raw `##`, `###`, `---`, or stray `**` artifacts in the assistant answer.
- Search failure adds a trace entry but does not crash the turn.
- Search result count respects `WebSearchMaxResults`.

## Memory Checks

- `/remember ...` captures a memory with project and session context.
- Preference-like messages capture `USER` profile memory; durable project/session notes capture `MEMORY`.
- Messages containing likely API keys, passwords, tokens, bearer credentials, or secrets are not captured.
- Duplicate memory summaries merge rather than creating unnecessary duplicates.
- Pinned and same-project memories rank above unrelated memories.
- Settings show separate USER and MEMORY usage budgets.

## Project-Scoped Chat Checks

- Opening the same folder reuses the existing project record.
- Opening a new folder creates a project and an initial chat session.
- Selected project id persists after restart.
- Chat history is associated with the correct project id.
- Project name/path supplied to the model matches the selected workspace.
- Project file tools operate only inside the selected project folder.

## Project Tool Checks

- `project_list_files` lists project paths and excludes generated folders such as `.git`, `.vs`, `bin`, `obj`, and `node_modules`.
- `project_read_file` reads UTF-8 text files and refuses large or binary files.
- `project_search` searches project text files and caps result output.
- `project_write_file` creates parent folders inside the project and refuses accidental overwrite unless requested.
- `project_edit_file` replaces exactly one old-text occurrence and rejects zero or ambiguous matches.
- Tool paths using `..`, absolute outside paths, or reparse-point escapes are rejected.
- Tool traces are visible in the assistant thinking/progress expander.
- Large `project_read_file` outputs are summarized in the Thinking expander rather than dumping the full file body.
- Repeated successful tool calls, such as writing or reading the same path with the same arguments twice, finalize with tools disabled and do not end with `stopped after 8 tool rounds`.
- Assistant answer text and Thinking expander text can be selected and copied from the chat canvas.

## Subagent Checks

- Settings exposes subagent enablement, auto-delegation, max parallel subagents, and max subagents per turn.
- With subagents enabled, the parent prompt includes the available built-in agents and the `subagent_run` tool.
- With auto-delegation disabled, Lucky only exposes `subagent_run` when the user explicitly asks for delegation or subagents.
- `AGENTS.md` or `AGENTS.override.md` at the project root is loaded into both the parent prompt and subagent prompt.
- Built-in subagents can read/list/search under `Workspace` but do not receive write/edit tools.
- A custom subagent definition under `%LOCALAPPDATA%\Lucky\agents\*.json` or `<project>\.lucky\agents\*.json` loads without writing user state into the repository.
- Custom subagents cannot escalate above the current Lucky access level; a custom write-capable agent under `Workspace` receives a denial instead of editing files.
- Multiple subagent tool calls in one parent tool round respect `MaxParallelAgents` and `MaxAgentsPerTurn`.
- Subagent trace entries use `subagent.<name>` prefixes and summarize child output without dumping full child transcripts.
- Subagent failures appear as traceable tool errors and do not crash the parent turn.

## Access-Level Checks

- `ChatOnly` prompts do not imply filesystem, shell, browser, or external access.
- `ChatOnly` may expose `subagent_run`, but child agents do not receive project filesystem tools.
- `Workspace` exposes read-only project tools: list, read, and search.
- `FullAccess` exposes project write/edit tools in addition to read-only tools.
- Project-changing prompts in `ChatOnly` or `Workspace` ask the user to switch to `FullAccess` instead of entering a write-denial loop.
- Shell, browser, and broader machine access remain unavailable until explicitly implemented with separate gates.

## Persistence Checks

- State is written under `%LOCALAPPDATA%\Lucky\lucky-state.json`.
- Subagent settings and state-backed custom agents round-trip through the local state file with safe defaults.
- User state, transcripts, API keys, and local model files are not written into the repository.
- Protected API keys can be read by the same Windows user and are unavailable as plain text in the state file.
- A missing state file is recreated with default settings.
