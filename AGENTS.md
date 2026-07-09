# Lucky Contributor Guide

This file is primarily for coding agents working in this repository. Human-facing product and architecture docs live in `README.md` and `docs/`.

Lucky is a WinUI 3, local-first LLM harness for Windows. Keep changes aligned with that identity: user data should stay local by default, model providers should be explicit and configurable, and tool use should be visible enough that users understand what Lucky did on their behalf.

## Documentation Ownership

- Keep `README.md` and `docs/ARCHITECTURE.md` updated with any user-visible behavior, provider setup, storage layout, access-level semantics, or architecture changes.
- Add or update `docs/QA.md` when a change affects manual verification, provider setup, search, memory, persistence, or project-scoped chat behavior.
- Do not let implementation-only changes drift away from the docs. If code changes alter how Lucky works, the same pull request should explain the change in the relevant documentation.

## Project Boundaries

- `src/Lucky.App` contains the WinUI 3 desktop application and view models.
- `src/Lucky.Core` contains provider clients, search, memory, persistence, and agent-turn orchestration.
- `tests/Lucky.Tests` contains automated tests for core behavior.
- `docs` contains product, architecture, and QA documentation.

## Local-First Expectations

- Persist durable state under the current Windows user profile, not in the repository.
- Protect secrets with the platform credential mechanism before writing them to disk.
- Prefer local endpoints, such as LM Studio and SearXNG on `127.0.0.1`, whenever they satisfy the user's intent.
- Make any remote service use explicit in settings and documentation.

## Provider Expectations

- Treat DeepSeek, LM Studio, and custom OpenAI-compatible servers as provider configurations behind the same chat-completions interface.
- Keep provider defaults conservative and editable: base URL, model name, API-key requirement, and reasoning settings should be clear to the user.
- Do not log or display raw API keys. If a provider requires a key and none is configured, Lucky should explain what is missing without trying the request.

## Access Levels

Lucky's access levels are part of the user trust model:

- `ChatOnly`: the assistant may answer from chat context, global configured memories, and configured SearXNG search, but should not imply workspace access.
- `Workspace`: the assistant may use selected project context that Lucky explicitly supplies.
- `FullAccess`: project write/edit tools are available inside the selected workspace. Broader machine access is reserved for future capabilities and must require especially clear UI and tool traces.

## Collaboration Rules

- Do not overwrite unrelated edits. Read before editing files that others may have changed.
- Keep changes small and consistent with existing C# and XAML patterns.
- Add tests for risky core behavior, especially memory capture/retrieval, provider payloads, persistence, and search parsing.
- Do not add network calls that bypass the configured provider or SearXNG settings.
- for complex tasks, write yourself a new goal and spawn agents in parallel - as many as needed to do it better and faster. Split the work into independent pieces, dispatch them concurrently, and synthesize the results as they return. give each agent its own dedicated /goal.
- if computer use gives errors, fix them and try again. don't skip using it if a visual test was requested.
- if anything visual is not working or not looking right, always use visual test mode, check yourself and fix it.   
