## Agent skills

### Issue tracker

Issues and specs live in GitHub Issues (`dk900912/ClipLine`, public), read and written through the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default role labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout: root `GLOSSARY.md` plus `docs/adr/`. See `docs/agents/domain.md`.

## Git Commit Policy
- **Atomic Commits**: One commit must represent one logical change. Do not commit file-by-file; stage and batch all related edits into a single atomic commit. Only stage files directly related to the current task (avoid blind `git add .`).
- **Amend Unpushed Fixes**: For follow-up fixes or adjustments to the current unpushed logical unit, use `git commit --amend` instead of creating a new commit. NEVER amend commits that have already been pushed to a remote branch.
- **Conventional Commits (Bilingual)**: Follow the Conventional Commits specification. Keep the type (and optional scope) in lowercase English (`feat`, `fix`, `chore`, `refactor`, `docs`, `test`, `perf`), followed by a colon and space, with the summary and body written in **Simplified Chinese**.
  - Examples: `feat(auth): 新增用户登录接口`, `fix: 修复列表分页越界问题`

## Language Conventions
- **Simplified Chinese for Human-Facing Text**: All human-readable text MUST be written in Simplified Chinese. This includes:
  - Conversational responses, progress updates, and task summaries
  - Documentation (`README.md`, architecture/design docs, API docs)
  - Code comments and docstrings
  - Git commit descriptions/bodies, Issue templates, and Pull Request titles/descriptions
- **English for Technical Identifiers**: Keep all code identifiers (variables, functions, classes, file names), CLI commands, configuration keys, and Conventional Commit prefixes strictly in English.
