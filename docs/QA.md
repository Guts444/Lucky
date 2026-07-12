# Lucky QA Checklist

Use this checklist for manual verification when changes affect the WinUI app shell, provider setup, SearXNG, memory, project-scoped chats, access levels, or persistence.

Automated coverage lives in `tests/Lucky.Tests`. Run it first; use this document for UI, provider, and end-to-end checks that are hard to automate.

## Build And Test

```powershell
dotnet restore Lucky.slnx
dotnet build Lucky.slnx
dotnet test Lucky.slnx
```

WinUI packaging often needs an explicit platform:

```powershell
dotnet build src\Lucky.App\Lucky.App.csproj -p:Platform=x64
```

Before a release or large PR:

- Solution builds cleanly in Debug and Release with no unexpected warnings.
- `dotnet test Lucky.slnx` passes.
- Visual pass of the main shell: project/history rail, chat canvas, composer (model/reasoning, access, context meter), Thinking expander, Settings entry from the bottom-left profile row.

## Provider Checks

- LM Studio: with the local server running at `http://127.0.0.1:1234/v1`, Lucky can list models and complete a chat without an API key.
- DeepSeek: without an API key, Lucky shows setup guidance and does not call the provider.
- DeepSeek: with a protected API key and model configured, Lucky can complete a chat through the OpenAI-compatible endpoint.
- DeepSeek: `deepseek-v4-flash` and `deepseek-v4-pro` both work with Lucky's thinking payload and reasoning effort.
- DeepSeek thinking mode sends `thinking` and `reasoning_effort`; portable providers do not receive DeepSeek-only fields.
- DeepSeek reasoning text appears inside the message's `Thinking` expander when `reasoning_content` is returned.
- DeepSeek V4 thinking requests omit `tool_choice`, replay `reasoning_content` on assistant tool-call messages, convert recognized textual DSML into validated tool calls, and never report DSML fragments as answer deltas.
- Provider token usage keeps combined prompt/completion totals for the turn, while the chat meter uses the latest provider-reported input context only for the matching active provider/model; unsupported providers or a model switch show a `~` local estimate instead of presenting it as exact.
- DeepSeek v4 saved states with stale 131K context values migrate to a 1,000,000-token context meter.
- OpenAI Codex: under Settings > Providers, choose **Connect** and complete the browser OAuth flow. Confirm the opened URI is HTTPS, Lucky never asks for or displays a token, the saved Lucky state has no OAuth credential field, and the isolated Codex home contains `auth.json.dpapi` rather than plaintext `auth.json` after sign-in and after an app restart.
- OpenAI Codex: confirm `%LOCALAPPDATA%\Lucky\CodexHome` is used and that an account logged into the global Codex CLI does not make a fresh Lucky home appear connected. Browser cookies may still recognize the browser user.
- OpenAI Codex: choose **Disconnect**, confirm subscription models disappear from the composer, then reconnect and use **Refresh account models** to repopulate only the signed-in account's advertised models/efforts.
- OpenAI Codex: while connected, use **Refresh account models** repeatedly. Confirm the existing authenticated app-server is reused, no second sign-in begins, and no `git.exe` or child-process application-error dialog appears.
- OpenAI Codex: compare the composer catalog with the official `model/list` response for the isolated Lucky home. Confirm visible entries are present in advertised order and that models found only in another app/global Codex cache are not injected.
- OpenAI Codex: run a Workspace prompt that asks to list project files. Confirm the model invokes Lucky's visible `project.list_files` trace rather than a native Codex command, then confirm the reply streams and the meter reflects the provider-reported input context.
- Custom provider: base URL, model, and API-key requirement are respected.
- Provider errors produce user-facing messages that include enough detail to fix configuration without exposing secrets.

## SearXNG Checks

- `/web test query` calls the configured SearXNG URL and returns or injects JSON results.
- Prefetched `/web` results are described to the model as real SearXNG web access so it should not claim it lacks internet access just because search ran before the model turn.
- The `web_search` tool is available to the parent model and calls the configured SearXNG endpoint.
- `ChatOnly` can use `web_search` without exposing project filesystem tools.
- Automatic web search triggers for current-looking prompts when `AutoWebSearch` is enabled.
- Automatic web search does not trigger for offline artifact/build prompts such as creating a standalone HTML canvas game, unless the user explicitly uses `/web`.
- A current-news prompt renders cleanly without raw decorative markdown artifacts (`##`, `###`, `---`, stray bold markers) in the assistant answer when the formatter is enabled.
- Search failure adds a trace entry but does not crash the turn.
- Search result count respects `WebSearchMaxResults`.

## Trusted Page Reader Checks

- With page reading disabled, `web_open` is not registered with the model and no page request is made.
- With `FullAccess`, page reading enabled, and a trusted domain configured, the `web_open` tool is registered and can return readable static page text.
- `ChatOnly` and `Workspace` do not expose `web_open`, even when the setting is enabled.
- An initial URL or redirect outside the trusted-domain list is refused before Lucky fetches the destination.
- Non-default ports and hosts resolving to loopback/private/link-local/reserved addresses are refused before Lucky fetches the destination.
- HTML script/style content, binary responses, oversized downloads, and oversized model text are not exposed as normal page content.
- Oversized titles and regex parsing timeouts become bounded visible results rather than unbounded prompt text or a crashed turn.
- The page reader sends no stored cookies, authentication headers, form actions, or logged-in browser state.

## MCP Checks

- MCP is off by default; enabling it without any configured server exposes no MCP tool.
- Adding a stdio command, optional quoted arguments, and optional working directory in Settings round-trips for the same Windows user, while the launch configuration is DPAPI-protected rather than plain text in `%LOCALAPPDATA%\Lucky\lucky-state.json`. Legacy plain-text MCP settings migrate on their next save.
- In `FullAccess`, Lucky launches an enabled server for the active parent turn, completes initialize / `tools/list`, and exposes discovered `mcp_*` tools to the model.
- A nested MCP input schema is passed through the OpenAI-compatible provider payload without flattening object/array fields.
- A `tools/call` result returns text and structured content to the model and a visible `mcp.<server>.<tool>` trace entry to the Thinking panel.
- A missing command, bad working directory, timeout, or server protocol failure becomes a visible `mcp.connect` or tool error rather than crashing the turn.
- An oversized stdout JSON-RPC frame terminates the configured server and leaves a visible connection error; large schemas, stderr lines, text, structured result data, and content arrays remain bounded before model exposure.
- Stop cancels MCP/page-reading work and ends child processes; no MCP tool is exposed to subagents.
- Verify the current implementation scope: stdio tools work; Streamable HTTP, OAuth, resources, prompts, subscriptions, and server-initiated requests are not advertised as implemented.

## Docker Code Sandbox Checks

- The Code execution sandbox card is visible at the bottom of Settings, clearly distinguishes itself from host PowerShell, says no host folders are mounted, and has a nearby `Save settings` button.
- Sandbox is off by default. With it off, with an empty image, or below `FullAccess`, `sandbox_execute` is not registered with the model and no Docker command runs.
- Enable it only after manually building or loading a known local image that contains `sh` and the desired runtime. Lucky must not pull an image; a missing image is a visible trace error that says it was not available locally.
- With Docker Desktop stopped, an attempted configured sandbox run becomes a visible Docker availability error rather than a hang, host-shell fallback, or automatic image pull.
- In `FullAccess`, execute a harmless command such as `id; pwd; echo sandbox-ok` in a known local image. Verify the trace reports the exit code and bounded stdout/stderr, `/scratch` as disposable storage, and that `/project` is unavailable because no host path is ever mounted.
- Verify the container uses the configured bounded timeout (5–120 seconds), CPU (0.25–2), memory (64–2048 MiB), PID (16–256), and scratch (16–512 MiB) settings. A long-running command must time out and request forced container removal.
- Verify a network-dependent command fails under `--network=none`, and that a command cannot write the read-only container root.
- Confirm the service uses the local Windows Docker named pipe, rejects remote `DOCKER_HOST` or non-default `DOCKER_CONTEXT`, and refuses an image with declared Docker volumes before `docker run`.
- Older saved read-only-project-mount settings are reset off during load; there is no host bind-mount option in the sandbox.
- Confirm `ChatOnly`, `Workspace`, and subagents never receive `sandbox_execute`. Confirm `project_run_command` remains labeled and documented as unsandboxed current-user PowerShell.

## Composer and Scroll Checks

- Focus and unfocus the composer. Confirm the outer rounded surface remains one tone and the editable area does not gain a separate rectangular background or border.
- Confirm the access and model selectors render as compact rounded controls and the Stop/Send actions remain circular, aligned, and usable at the minimum supported window width.
- On a project's empty state, select the displayed working-folder path. Confirm the folder picker opens and choosing a different folder follows the standard add/select-project flow.
- Type a message taller than the composer. Confirm the text box grows to its cap, exposes an internal vertical scrollbar, and its bottom line remains reachable. Enter inserts a line break; Ctrl+Enter and Send submit the message.
- During a streaming response, scroll upward with the mouse wheel. Confirm Lucky stops forcing the canvas to the bottom. Scroll back to the latest message, then confirm follow-latest resumes for new streamed content.

## Memory Checks

- `/remember ...` captures a memory with project and session context.
- Preference-like messages capture `USER` profile memory; durable project/session notes capture `MEMORY`.
- Messages containing likely API keys, passwords, tokens, bearer credentials, or secrets are not captured.
- Duplicate memory summaries merge rather than creating unnecessary duplicates.
- Pinned and same-project memories rank above unrelated memories.
- Memories can be disabled in Settings; disabled memory mode captures no new memories and injects no recalled memories while preserving stored items.
- Global memories remain eligible in `ChatOnly`, but project-scoped memories require workspace context.
- Settings show separate USER and MEMORY usage budgets, and prompt injection respects those budgets.

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
- `project_apply_patch` accepts text-only unified diffs for create, modify, and delete operations; it preserves existing line endings, reports any unique nearby-context match, and leaves all files unchanged when any hunk cannot be validated.
- `project_run_command` is available only in `FullAccess`, starts at the selected project root, returns visible stdout/stderr and exit status, and accepts only a 1–300-second timeout.
- Verify a successful command, a non-zero exit, a 1-second timeout, caller cancellation, and large-output truncation. Timeout output must distinguish a confirmed PowerShell-host exit from a case where a process or detached child may remain.
- Patch paths using `..`, absolute outside paths, reparse-point escapes, binary patch sections, ambiguous nearby matches, or rename sections are rejected without editing a file.
- Tool paths using `..`, absolute outside paths, or reparse-point escapes are rejected.
- Tool traces are visible in the assistant thinking/progress expander.
- Large `project_read_file` outputs are summarized in the Thinking expander rather than dumping the full file body.
- Parent turns cap at 12 rounds and 12 calls per response; child turns cap at 8 calls per response. Overflow calls return visible tool errors, repeated successful calls finalize without tools, three fully failed rounds finalize, and every follow-up model call remains within the configured context budget after tool/schema compaction.
- Assistant answer text and Thinking expander text can be selected and copied from the chat canvas.

## Subagent Checks

- Settings exposes subagent enablement, auto-delegation, max parallel subagents, and max subagents per turn.
- With subagents enabled, the parent prompt includes the available built-in agents and the `subagent_run` tool.
- With auto-delegation disabled, Lucky only exposes `subagent_run` when the user explicitly asks for delegation or subagents.
- With auto-delegation enabled, the automatic catalog omits explicit-only agents such as `worker`.
- When the user explicitly asks for subagents or names an explicit-only agent, the explicit-only definitions appear in the catalog.
- Under `Workspace` or `FullAccess`, `AGENTS.md` or `AGENTS.override.md` at the project root is loaded into both the parent prompt and subagent prompt.
- Built-in subagents can read/list/search under `Workspace` but do not receive write/edit/patch tools.
- User-owned custom definitions under `%LOCALAPPDATA%\Lucky\agents\*.json` can explicitly opt into write/edit/patch/terminal tools only under `FullAccess`; mutable child runs are serialized.
- Project `.lucky\agents\*.json` definitions are reparse-point checked, cannot shadow built-ins or user agents, and are forced explicit-only/read-only Workspace policy.
- Custom subagents cannot escalate above the current Lucky access level; a custom write-capable agent under `Workspace` receives a denial instead of editing files.
- Multiple subagent tool calls in one parent tool round respect `MaxParallelAgents` and `MaxAgentsPerTurn`.
- Subagent trace entries use `subagent.<name>` prefixes and summarize child output without dumping full child transcripts.
- Subagent failures appear as traceable tool errors and do not crash the parent turn.

## Access-Level Checks

- `ChatOnly` prompts do not imply filesystem, shell, page reader, MCP, or broad external access beyond configured SearXNG search.
- `ChatOnly` does not inject selected project name/path, project `AGENTS.md`, project memories, project custom agents, or project filesystem tools.
- `ChatOnly` may expose `subagent_run`, but child agents do not receive project context or project filesystem tools.
- `Workspace` exposes read-only project tools: list, read, and search.
- `FullAccess` exposes project write/edit/patch tools, the visible PowerShell command runner, explicitly enabled trusted page-reader and MCP tools, and an explicitly enabled/configured Docker sandbox.
- Project-changing prompts in `ChatOnly` or `Workspace` ask the user to switch to `FullAccess` instead of entering a write-denial loop.
- The command runner starts at the project root but is not an OS filesystem sandbox; it runs under the current Windows account and must remain visibly traceable. The Docker sandbox is a separate, constrained container boundary with local-image-only/no-network limits, not a guarantee of VM-grade isolation. The page reader is static/trusted-domain only; interactive browser automation requires a configured MCP server.

## Persistence Checks

- State is written under `%LOCALAPPDATA%\Lucky\lucky-state.json`.
- Subagent settings and state-backed custom agents round-trip through the local state file with safe defaults.
- The memory enablement setting defaults on for old state and round-trips when explicitly disabled.
- Browser trusted-domain, MCP stdio, and Docker sandbox settings round-trip with bounded safe defaults; MCP launch configuration is encrypted at rest, command arguments containing line breaks are removed, and invalid sandbox image names are cleared.
- User state, transcripts, API keys, and local model files are not written into the repository.
- Protected API keys can be read by the same Windows user and are unavailable as plain text in the state file.
- Protected MCP launch configuration can be read by the same Windows user and is unavailable as plaintext in the state file.
- A missing state file is recreated with default settings.
