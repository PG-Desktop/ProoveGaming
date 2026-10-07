# Add Claude Skills — Plan

## Goal

Add four project-scoped Claude Code skills so the team can invoke role-specialized
workflows via slash commands from any checkout of this repo:

- `/architect` — design & planning
- `/coder` — implementation
- `/reviewer` — code review of pending changes
- `/test-writer` — authoring / extending tests

> Note: original ask spelled it "architector" — using **architect** here as the canonical
> English form. Confirm before renaming if you want to keep "architector".

## Scope decisions

- **Skills, not subagents.** Skills give explicit, user-triggered slash commands
  (`/architect`, `/coder`, …). Subagents would auto-route work; we want the
  developer to consciously pick a mode. If we later want auto-routing we can add
  a paired subagent that the skill delegates to.
- **Project scope.** Files live under `.claude/skills/` in the repo root so they
  get committed and shared with the team, not stashed in `C:\Users\<user>\.claude`.
- **.NET / Avalonia flavored.** Skill bodies reference the actual stack
  (.NET 10, Avalonia, `ProveGaming.slnx`, `dotnet build`, `dotnet test`) so
  guidance is grounded in this codebase rather than generic advice.

## Files to create

```
.claude/
  skills/
    architect.md
    coder.md
    reviewer.md
    test-writer.md
docs/plans/add-claude-skills/
  add-claude-skills-plan.md   (this file)
```

Each skill file uses the standard Claude Code skill frontmatter:

```markdown
---
name: <slug>
description: <one-line hook that shows in /help and skill listings>
---

<system prompt / instructions for Claude when this skill is invoked>
```

## Per-skill responsibilities

### `/architect`
- **Trigger:** user wants a design, plan, or trade-off analysis before code is written.
- **Behavior:**
  - Reads relevant existing code first; never plans in a vacuum.
  - Produces a plan doc under `docs/plans/<slug>/<slug>-plan.md` (same convention
    as this file), not inline code changes.
  - Sections: Goal, Scope, Options considered, Recommended approach,
    File-level change list, Risks, Open questions.
  - Ends with a "Ready for `/coder`?" checkpoint — does not implement.
- **Tools:** Read, Grep, Glob, Write (plan doc only), WebFetch/WebSearch allowed.

### `/coder`
- **Trigger:** implementation of a planned change.
- **Behavior:**
  - If a plan doc exists under `docs/plans/…`, load and follow it. If not,
    ask whether to run `/architect` first for anything non-trivial.
  - Follows existing conventions in the touched project (naming, folder layout,
    DI registration, XAML style).
  - Runs `dotnet build ./src/ProveGaming.slnx` after non-trivial edits.
  - No feature-flagging, no speculative abstractions, no "cleanup while I'm here".
- **Tools:** Read, Edit, Write, Grep, Glob, Bash (dotnet build/run only).

### `/reviewer`
- **Trigger:** review pending changes on the current branch before PR / merge.
- **Behavior:**
  - Diffs against `main` (`git diff main...HEAD` + working tree) — reviews the
    *whole* branch delta, not just the last commit.
  - Categorizes findings: **Blockers**, **Should-fix**, **Nits**.
  - Checks: correctness, null-safety, async/await misuse, Avalonia binding
    leaks, DI lifetime mismatches, resource disposal, log noise, missing tests.
  - Ends with an explicit ship / no-ship recommendation.
  - Never edits code — output only.
- **Tools:** Read, Grep, Glob, Bash (git only), WebFetch.

### `/test-writer`
- **Trigger:** add or extend tests for a change.
- **Behavior:**
  - Identifies the test project matching the code under test
    (`ProoveGaming.Device.Testing` and any peer test projects under `tests/`).
  - Mirrors existing test style (xUnit vs NUnit vs MSTest — detect first).
  - Prefers real dependencies over mocks where feasible; mocks only at
    system boundaries.
  - Runs `dotnet test` on the affected project after writing.
  - Reports coverage of the change: what is covered, what is deliberately
    not covered and why.
- **Tools:** Read, Edit, Write, Grep, Glob, Bash (dotnet test).

## Skill file skeleton (example: `architect.md`)

```markdown
---
name: architect
description: Design an implementation plan for a change before any code is written. Produces a plan doc under docs/plans/.
---

You are acting as the **architect** for the Proove Gaming (.NET 10 / Avalonia) codebase.

Your job in this invocation:
1. Understand the request. Ask at most one clarifying question if scope is unclear;
   otherwise proceed.
2. Read the relevant existing code before proposing anything. Use Grep/Glob widely.
3. Produce a plan document at `docs/plans/<kebab-slug>/<kebab-slug>-plan.md` with
   sections: Goal, Scope, Options considered, Recommended approach,
   File-level change list, Risks, Open questions.
4. Do NOT modify source code. Do NOT run builds. End with:
   "Plan ready. Invoke `/coder` to implement."

Constraints:
- Prefer editing existing projects over new ones.
- Call out any change that touches installer / uninstaller flows — those have
  extra release-process implications.
- Flag anything that would require a solution-wide change (new NuGet, target
  framework bump, new project in `.slnx`).
```

The other three files follow the same shape with their own name / description /
body per the responsibilities above.

## Implementation steps

1. Create `.claude/skills/` directory at repo root.
2. Write the four skill files as specified above.
3. Verify each shows up under Skill listing (`/help` or the skill picker) in a
   fresh Claude Code session opened at the repo root.
4. Smoke-test each:
   - `/architect` on a trivial "add a config setting" request — expect a plan doc,
     no code edits.
   - `/coder` against the plan doc — expect code changes + a build.
   - `/reviewer` after the coder run — expect a Blockers/Should-fix/Nits report.
   - `/test-writer` after the coder run — expect new tests + a `dotnet test` run.
5. Commit `.claude/skills/*.md` and this plan on a feature branch and open a PR
   so the team can review the wording of the skill prompts before they become
   the shared default.

## Risks / open questions

- **Naming.** Confirm `architect` vs `architector`. Renaming later means updating
  any muscle memory / docs.
- **Overlap with existing skills.** There is already a built-in `/review` skill.
  `/reviewer` is intentionally different (whole-branch, categorized findings,
  ship-recommendation) — worth noting in its description so users pick the right one.
- **Tool allow-lists.** Skill files themselves don't restrict tools — the
  restrictions above are enforced by wording in the skill prompt. If we want
  hard restrictions (e.g. reviewer must not edit files) we'd need to move those
  roles to **subagents** (`.claude/agents/*.md`), which do support tool
  allow-lists. Flag if hard restriction is required — that's a follow-up.
- **CI.** No CI changes required; skills are editor-time only.

## Not in scope

- Adding subagents.
- Adding hooks or settings.json changes.
- Automating skill invocation from CI.
