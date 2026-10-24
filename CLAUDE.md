# TenantFlow: Claude Code instructions

Project rules, shared by every coding agent:

@AGENTS.md

## Claude Code specifics

- Slash commands `/slice`, `/spec`, `/milestone-gate` wrap the procedures in `.agents/skills/`. Change the
  procedure there, never the wrapper. The tenant isolation checklist lives inside `/slice`, not its own command.
- "A code review" is `/code-review`; "a security review" is `/security-review`.
- Explore in plan mode before writing a spec.
- `.claude/rules/tests.md` imports `.agents/rules/tests.md` for `tests/**`.
- Shared permissions: `.claude/settings.json`. Personal: `.claude/settings.local.json` (git-ignored).
