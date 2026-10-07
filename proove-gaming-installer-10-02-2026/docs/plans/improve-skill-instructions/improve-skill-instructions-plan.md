# Improve Skill Instructions — Plan

## Goal

Raise the quality and performance of the code produced by the skill chain
`/architect → /coder → /reviewer → /test-writer` through two changes:

1. **The plan as an end-to-end contract.** A plan stops being just a "list of files" and
   gains verifiable acceptance criteria, a test strategy, a "review focus", and performance
   requirements. The coder checks itself against the contract and runs tests; the reviewer
   reviews the diff **against the plan** (drift, criteria, systemic problems), not only
   against a generic checklist; the test-writer receives a ready-made list of what to cover.
2. **An orchestrator.** A new skill plus subagent that, given a finished plan, drives the
   implementation itself through a fixed pipeline
   `coder → test-writer → reviewer → coder → test-writer` and then hands the result to a
   human developer for the final review and merge. Roles stop being launched one at a time by
   hand — a single conducting pass with explicit stop criteria drives them.

Plus stronger perf/quality instructions in `/architect` and `/coder`, a systemic problem
hunt (thread races, perf regressions, possible failures) in `/reviewer`, and a fix to an
actual error in `/test-writer` (it named a non-existent test project).

## Scope

**In:**
- `.claude/skills/architect/SKILL.md` — new plan sections (Acceptance criteria, Test
  strategy, QA manual steps, Review focus, Performance & quality) + perf/quality design
  instructions, a change-type test policy, and a brainstorming step for large features.
- `.claude/skills/coder/SKILL.md` — perf/quality coding rules, running the affected tests,
  checking against acceptance criteria, self-review.
- `.claude/skills/reviewer/SKILL.md` — a systemic pass (races/perf-regressions/failures),
  review against the plan, consuming "Review focus", complete working-tree diff coverage,
  writing findings into the plan's reserved journal block, and a `/security-review`
  recommendation for installer/uninstaller diffs.
- `.claude/skills/test-writer/SKILL.md` — fix the test-project name, CI caveat, input from
  Test strategy, change-type test policy.
- **NEW** `.claude/skills/orchestrator/SKILL.md` — the `/orchestrator` slash command that
  conducts the fixed pipeline and verifies the build itself.
- **Skill format migration:** all existing skills move from flat
  `.claude/skills/<name>.md` to `.claude/skills/<name>/SKILL.md` (otherwise Claude Code does
  not load them).
- **NEW** `.claude/agents/orchestrator.md` — an orchestrator subagent for direct dispatch;
  it holds no logic of its own and points at the orchestrator skill.
- Deletion of the empty `.claude/skills/device-code-review/` directory.
- `CLAUDE.md` — document the new `/orchestrator` skill and the `.claude/agents/` subagent.

**Out:**
- Turning coder/reviewer/test-writer into subagents with a hard tool allowlist (a separate
  follow-up, flagged in `add-claude-skills-plan.md`).
- Hooks / `settings.json`, CI changes.
- Any changes to product code under `src/**` and tests under `tests/**`.
- Auto-commit / auto-merge / auto-opening PRs by the orchestrator (deliberately withheld —
  see Risks). The final code review and merge stay with a human developer.

## Options considered

**A. Pinpoint wording edits in each skill separately.**
Cheap, but the skills stay disconnected and are launched one at a time by hand — quality
depends on prompt luck, and the "implement → test → review → fix" routine is entirely on the
human.

**B. Plan-as-contract + orchestrator (recommended).**
The architect puts machine-checkable criteria/focus/perf into the plan; the orchestrator
runs the roles through a fixed pipeline against that contract, then hands off to a human.
Maximum quality gain and routine removal while preserving role decoupling (each role can
still be invoked separately).

**C. A single "super-skill" with all phases inside.**
Breaks the deliberate role decoupling and bloats a single invocation's context. The
orchestrator from option B gives the same automation but conducts **isolated** roles (each in
its own subagent context) rather than merging them into one prompt. Rejected.

## Recommended approach

Option **B**. Below is the per-file edit list, detailed enough for `/coder` to apply without
re-analysis, plus a dedicated **Orchestrator design** section with the pipeline protocol.

## Orchestrator design

### Role

A conducting pass: given a finished plan, it drives the change through a fixed role pipeline
and then hands off to a human developer, **without editing code itself** — only conducting
roles, verifying the build itself, and reading the roles' structured reports.

### Two files, one logic

- **`.claude/skills/orchestrator/SKILL.md`** — the primary path. The `/orchestrator [<slug>]`
  command runs in the main context and launches each role as a **subagent** (Agent tool) so
  every role gets a clean context while the orchestrator keeps a thin state "ledger". This is
  the subagent-driven-development pattern.
- **`.claude/agents/orchestrator.md`** — a subagent for direct dispatch (`@orchestrator`) or
  non-interactive runs. It carries **no orchestration logic of its own**: it invokes the
  orchestrator skill and follows it, so the pipeline lives in exactly one place. Caveat: as a
  subagent it may not be able to dispatch further subagents — in that mode it invokes the
  roles **inline** via the Skill tool and watches its context budget.

### Preconditions

- A plan `docs/plans/<slug>/<slug>-plan.md` must exist. If none matches — stop and suggest
  running `/architect` first. The orchestrator does **not** invent a plan.

### State (ledger, TodoWrite / scratch file)

- **File-level change list** items → done / pending.
- **Acceptance criteria** items → met / unmet / unknown.
- **Review findings** from the review step (Code / Tests) and whether the fix pass closed them.
- The build result the orchestrator verified itself after each coder step.

### The pipeline — a single fixed pass, no re-looping

Run **exactly one** fixed sequence and then stop:

```
coder → test-writer → reviewer → coder → test-writer → hand off to human
```

There is **one** review, in the middle, followed by **one** fix pass (coder + test-writer) to
address it. There is **no second review and no further cycles** — the human developer does the
final code review and merge. Run more than this fixed pipeline **only if the user explicitly
asks**. No nested or parallel loops.

Steps in order:

1. **Implement** — run `/coder` on the whole plan.
2. **Verify build (orchestrator, not the coder's self-report)** — run
   `dotnet build ./src/ProveGaming.slnx` via Bash and read the result. A red build feeds the
   fix pass; do not proceed on it as if green.
3. **Test** — run `/test-writer` per the plan's **Test strategy**; capture pass/fail from real
   `dotnet test` output.
4. **Review** — run `/reviewer` → Blockers/Should-fix/Nits + Ship verdict + Plan adherence.
   The reviewer writes findings into the plan's reserved `## Review findings` block.
5. **Fix pass — implement** — run `/coder` on the delta only: unmet acceptance criteria + any
   build failure + the plan's **Review findings → Code** block (read from the plan journal).
6. **Verify build again** — re-run the build; never report success on a red build.
7. **Fix pass — test** — run `/test-writer` on the **Review findings → Tests** block plus any
   newly changed code.
8. **Hand off** — stop. Summarize state, reproduce the plan's **QA (manual test steps)**
   verbatim for pasting into the task/story comment, and hand the branch to the human for the
   final review and merge.

### Escalate to the human immediately (do not push through the pipeline)

- A blocker needs a decision outside the plan: a new NuGet package, a target-framework bump,
  a `.slnx` or `Directory.Build.props` edit, installer/uninstaller risk, or architectural
  ambiguity.
- A contract-breaking change with no migration story.
- The fix pass cannot close a Blocker or make the build green — stop and hand the open items
  to the human rather than inventing extra cycles.

### Safety / scope

- The orchestrator **never** edits code itself — coordination, self-verifying the build, and
  reading reports only.
- **The final code review is the human developer's.** The reviewer verdict is advisory; the
  pipeline ends by handing the branch to a person.
- It does not commit, merge, or open a PR — even on `Ship: yes`.
- It honors the same repo constraints as the roles (installer/uninstaller callouts, no NuGet
  without the plan).

### Final report

Which pipeline steps ran, the build/test results the orchestrator verified itself, the
reviewer's advisory verdict, which acceptance criteria are met, the open items left for the
developer, and the QA manual-test checklist reproduced verbatim for the task/story comment.

## File-level change list

> **Skill file layout (correction).** Claude Code discovers a skill only as
> `.claude/skills/<name>/SKILL.md` (a directory named after the command, with `SKILL.md`
> inside) — flat `.claude/skills/<name>.md` files are silently ignored. The pre-existing
> skills were flat files and were therefore never actually loaded. Part of this change is
> migrating each to the directory form: `architect.md → architect/SKILL.md`, and the same for
> `coder`, `reviewer`, `test-writer`, `orchestrator`. Paths below name the skill; the edits
> land in the corresponding `SKILL.md`. Subagents keep the flat form
> (`.claude/agents/orchestrator.md`) — that is correct for agents.

### 1. `.claude/skills/architect` — plan structure + perf/quality design

Extend "produce the plan document with these sections" (between "File-level change list" and
"Risks"):

- **Acceptance criteria** — verifiable "given → action → expected observable result"
  statements. The definition-of-done contract for `/coder`, `/reviewer`, `/orchestrator`. Not
  "works correctly" but measurable.
- **Test strategy** — what to cover and at which level; the concrete test projects
  (`tests/ProoveGaming.App.Tests`, `tests/ProoveGaming.Infrastructure.Tests`); golden path +
  edge cases this change introduces; what is deliberately **not** covered and why. Set the
  strategy by the **kind of change**: bug fix (`fix-*`) → a reproduction test written first;
  new feature (`add-*`) → the tests that must be added; change to existing behavior → confirm
  the feature already has tests, and if it has none the plan adds characterization tests
  **before** the functional change.
- **QA (manual test steps)** — a **mandatory** numbered checklist of "how to verify this
  change by hand", understandable by a human/QA tester without reading code. Each step is a
  concrete action plus its expected result. **Purpose:** this checklist is copied verbatim
  into a comment on the task/story for the QA engineer, so it must be self-contained (no code
  or plan references). `/orchestrator` reproduces it verbatim in its final report.
- **Review focus** — 1–4 of the checks most critical for this change, from a fixed vocabulary:
  `installer/uninstaller`, `DI lifetimes`, `async/await`, `UI-thread affinity`,
  `thread-safety`, `gRPC contract`, `null-safety`, `resource disposal`, `localization`,
  `performance hot-path`. The reviewer starts with these.
- **Performance & quality** — are there hot paths (per-frame LED/RGB, device-poll loops,
  startup), what must not allocate/block the UI thread, where shared state (the singleton
  device-discovery services) needs thread-safe access, where sync-over-async is unacceptable.
  If there are no hot paths, write "none" explicitly.

At the end of the plan structure, reserve an empty **`## Review findings`** section (with
`### Round … / #### Code / #### Tests` subsections) — maintained by `/reviewer`, read by
`/coder` and `/test-writer`. The architect leaves it empty (header + a "filled in by
/reviewer" note) so the journal structure is uniform across plans.

Add a **perf/quality design** block: when choosing the Recommended approach, weigh
(a) data structures and allocations on hot paths; (b) async correctness and no UI-thread
blocking; (c) thread-safety of shared device state (the discovery services are singletons
holding in-memory state); (d) resource/IDisposable lifetime; (e) no premature abstraction —
simple code that is easy to review. In "What good looks like" add: "Acceptance criteria are
phrased so `/reviewer` can render a verdict without guessing the intent."

For a large or fuzzy feature, run `superpowers:brainstorming` before writing the plan; for a
small well-scoped change, at most one clarifying question.

### 2. `.claude/skills/coder` — perf/quality coding + contract + tests

- Step 1: before implementing, pin the plan's **Acceptance criteria** and **Test strategy** as
  the checklist for this run, **and read the `Review findings → Code` block** (if present from
  the review step) and close those first.
- New **Performance & quality rules** block: no allocations/boxing/heavy LINQ on hot paths;
  correct async (no `.Result`/`.Wait()`/`async void` except event handlers, `ConfigureAwait`
  per convention, never block the UI thread); thread-safe access to singleton device-service
  state; dispose `IDisposable`; clear names, short methods, no dead/commented-out code, style
  indistinguishable from the surroundings.
- Step 3 (currently only `dotnet build`): also run the **affected** tests
  (`dotnet test <project>.csproj`, or `./src/ProveGaming.slnx` when both sides are touched). A
  green build is not the absence of regressions.
- New step before "Report": **self-review** — walk the diff against the plan (every change-list
  item closed, nothing extra, every acceptance criterion achievable with the current code).
  Report divergences.
- Hand-off block adds `Tests: <passed|failed|n/a>` and
  `Acceptance criteria met: <all|partial — what is not>`.

### 3. `.claude/skills/reviewer` — systemic pass + review against the plan

- Step 1 (review surface): cover the **complete** working-tree delta, committed and not. Run
  `git status --porcelain`, `git log main..HEAD --oneline`, `git diff main...HEAD`
  (committed), `git diff` (unstaged), `git diff --staged` (staged), and open untracked files;
  cross-check that their union equals `git status --porcelain`. If a plan covers the change,
  review the diff **against it** (change-list items implemented, no scope drift, acceptance
  criteria met).
- New **Systemic issues (global pass)** block — thread races/thread-safety, deadlocks
  (sync-over-async on a captured context, cross-order locks), perf regressions (allocations in
  per-frame/poll loops, O(n²), startup work, synchronous UI I/O), possible failures
  (unhandled background exceptions, null derefs, use-after-dispose, gRPC disconnect/timeout,
  leaked subscriptions). Each is at least a Should-fix; a Blocker when it can crash/corrupt.
- Start "Checks specific to this repo" with the plan's **Review focus** items when present.
- Reconcile the existing async/await block with the new Systemic block (one source of truth on
  `.Result`/`.Wait()`).
- **Installer / uninstaller:** because these run with UAC elevation and ship an embedded
  payload, recommend the built-in `/security-review` skill whenever the diff touches them.
- **Write findings into the plan (review journal).** Append to **exactly one** block — the
  reserved `## Review findings` section the architect leaves at the end of the plan. Never
  create a parallel or differently-named section, and never write findings into `Review
  focus`, `Acceptance criteria`, or any other section. Format is round-by-round, split into
  `#### Code` / `#### Tests`. This is the only exception to "do not edit code": still never
  touch `src/**` or `tests/**`, but maintain the journal in the plan.
- "## Summary" of the output adds `Plan adherence: <matches | drift: … | no plan>`.

### 4. `.claude/skills/test-writer` — fix the error + plan input

- **Fix (important).** The old step 2 named "Known project: `ProoveGaming.Device.Testing`".
  That is wrong: `src/ProoveGaming.Device.Testing` is a console `Exe` harness for manual
  device checks, **not** an xUnit project. The real test projects are
  `tests/ProoveGaming.App.Tests` and `tests/ProoveGaming.Infrastructure.Tests`. Replace the
  reference and drop `Device.Testing`.
- Step 1: if the plan has a **Test strategy**, take the coverage list from there, **and read
  the `Review findings → Tests` block** (if present) and close those gaps/failing tests first.
- **Change-type test policy** when writing: `fix-*` → write the failing reproduction test
  first; `add-*` → add every required test from the Test strategy; change to existing behavior
  → confirm tests exist, and if none, add characterization tests before covering the change.
- CI caveat (from the root CLAUDE.md): CI (`dotnet-ci.yml`) builds/tests only
  `ProoveGaming.App.csproj` and does **not** run `tests/`; target the `.slnx` or the specific
  test `.csproj` locally, otherwise the tests are "green" only because they never ran.
- Output format adds `Acceptance criteria covered: <which plan items are backed by tests>`.

### 5. **NEW** `.claude/skills/orchestrator/SKILL.md`

Frontmatter `name: orchestrator`; description — "drives a planned change through a fixed
`coder → test-writer → reviewer → coder → test-writer` pipeline, then hands off to a human
developer". Body — the protocol from **Orchestrator design** (preconditions, ledger, fixed
pipeline, self-verified build, escalation, safety, final report with the QA checklist). Roles
run as subagents (Agent tool), each from its role skill.

### 6. **NEW** `.claude/agents/orchestrator.md`

A subagent for direct dispatch that carries no logic of its own — it invokes the orchestrator
skill and follows it. Frontmatter: `name`, `description`, `tools`
(Read/Grep/Glob/Bash/TodoWrite/Skill — code editing is delegated). Body — invoke the skill,
pass the slug through, and fall back to inline role invocation if nested dispatch is
unavailable.

### 7. `.claude/skills/device-code-review/` — delete the empty directory

An empty artifact that looks like an unfinished skill. Removed.

### 8. `CLAUDE.md` — document the orchestrator

Update the repo-layout bullet and the "Project-scoped Claude skills" section: five skills now
(add `/orchestrator`), and note the `.claude/agents/` subagent.

## Risks

- **Token spend.** A single fixed pipeline (one review + one fix pass) bounds the spend; no
  iterate-until-ship loop. Extra passes happen only on explicit user request.
- **Subagent nesting.** The orchestrator skill in the main context dispatches roles — fine.
  The orchestrator subagent may be unable to dispatch nested subagents → it invokes roles
  inline via the Skill tool and controls its context budget.
- **Autonomy without oversight.** The orchestrator does not commit/merge/open PRs; the final
  code review and merge are a human developer's, and it escalates anything needing a human
  decision (NuGet, framework, installer, contract-breaking). This is a deliberate limit, not a
  gap.
- **`/architect` bloat.** New sections lengthen the plan; mitigated by brevity and allowing
  "none".
- **Duplicated async checks in the reviewer.** The new Systemic block overlaps async/await —
  reconciled into one source of truth rather than appended.
- **Plan backward compatibility.** Older plans without the new sections stay valid: a missing
  section means "no extra requirements", not an error.
- **Wording, not hard limits.** Skills do not hard-restrict tools; discipline rests on the
  text. Hard limits are a separate follow-up.

## Open questions (resolved)

1. **Pipeline shape** — resolved: a fixed single pass
   `coder → test-writer → reviewer → coder → test-writer`, no iterate-until-ship loop, no extra
   cycles unless the user explicitly asks. The final code review is the human developer's.
2. **Nested subagent dispatch** in this harness — the orchestrator subagent falls back to
   inline role invocation when nested dispatch is unavailable; the skill (main-context)
   dispatch path stays primary.
3. **Review focus** — resolved: a fixed vocabulary (simpler for the reviewer).
4. **`ProoveGaming.Device.Testing` in `.slnx`** — out of scope here; this change only fixes the
   skill reference, it does not touch the project or the solution.
5. **Skill name `architect`** — kept as `architect` (not `architector`).
