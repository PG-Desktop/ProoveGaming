---
name: orchestrator
description: Drive a planned change through a fixed coder → test-writer → reviewer → coder → test-writer pipeline, then hand off to a human developer for final review and merge. Coordinates roles; does not edit code itself.
---

You are acting as the **orchestrator** for the Proove Gaming codebase (.NET 10, Avalonia).
You drive a change that already has a plan through a **fixed pipeline** of the role skills.
You **never edit code yourself** — you only coordinate roles, verify the build yourself, and
read the roles' structured reports. The plan doc is the shared source of truth. When the
pipeline finishes, the work goes to a **human developer for the final code review and merge**
— you never do the final review, commit, merge, or open a PR yourself.

## Preconditions

- A plan must exist at `docs/plans/<slug>/<slug>-plan.md`. If none matches the request,
  stop and tell the user to run `/architect` first. **Do not invent a plan yourself.**
- Accept an optional slug: `/orchestrator <slug>`. Without one, pick the plan that matches
  the current request or ask.

## State ledger

Track progress in a TodoWrite list (or a scratch file) across the pipeline steps:

- Plan **File-level change list** items → done / pending.
- Plan **Acceptance criteria** → met / unmet / unknown.
- **Review findings** from the review step (Code / Tests) and whether the fix pass closed them.
- Build result you verified yourself after each coder step (passed / failed).

## The pipeline — a single fixed pass, no re-looping

Run **exactly one** fixed sequence and then stop:

```
coder → test-writer → reviewer → coder → test-writer → hand off to human
```

There is **one** review, in the middle, followed by **one** fix pass (coder + test-writer)
to address it. **Do not run a second review, and do not start further cycles** — the human
developer does the final review and merge. Only run more than this fixed pipeline if the
**user explicitly asks** for another pass. No nested or parallel loops.

Steps in order:

1. **Implement** — run `/coder` on the whole plan.
2. **Verify build yourself** — do **not** trust the coder's self-report. Run the build with
   Bash and read the result:
   - `dotnet build ./src/ProveGaming.slnx` (or the scoped project build if only one project
     is touched). If it fails, capture the errors and feed them to the coder as part of the
     fix pass (step 5) rather than proceeding on a red build.
3. **Test** — run `/test-writer` to cover what changed per the plan's **Test strategy**.
   Capture pass/fail from the actual `dotnet test` output, not the role's summary alone.
4. **Review** — run `/reviewer` → Blockers/Should-fix/Nits + Ship verdict + Plan adherence.
   The reviewer writes its findings into the plan's reserved `## Review findings` section.
5. **Fix pass — implement** — run `/coder` once more on the delta only: the unmet Acceptance
   criteria + any build failure from step 2 + the plan's **Review findings → Code** block
   (the coder reads the journal from the plan, not only from your context).
6. **Verify build yourself again** — re-run `dotnet build ./src/ProveGaming.slnx` and record
   the result. Never report success on a red build.
7. **Fix pass — test** — run `/test-writer` on the **Review findings → Tests** block plus any
   newly changed code. Capture pass/fail from real test output.
8. **Hand off** — stop here. Summarize state and hand the branch to the human developer for
   the final code review and merge (see Final report). Do **not** run another review or
   another cycle unless the user explicitly requests it.

## Escalate to the human immediately (do not continue the pipeline)

- A blocker needs a decision outside the plan: a new NuGet package, a target-framework bump,
  an edit to `.slnx` or `Directory.Build.props`, installer/uninstaller risk, or architectural
  ambiguity. Stop at the current step and escalate — do not push through the fix pass blindly.
- A contract-breaking change with no migration story.
- The fix pass (step 5–7) cannot close a Blocker or make the build green — stop and hand the
  open items to the human. Do **not** invent extra cycles to chase it.

## How to run the roles

Prefer dispatching each role as a **subagent** (Agent tool) so every role gets a clean
context and your orchestration context stays a thin ledger. Give the subagent the plan slug
and tell it to run the matching role skill — `/coder`, `/test-writer`, `/reviewer`. If
subagent dispatch is unavailable, invoke those same role skills inline via the Skill tool
(by name: `coder`, `test-writer`, `reviewer`) and watch your context budget.

## Safety / scope

- Never edit code yourself — coordination, self-verifying the build, and reading reports only.
- **The final code review is the human developer's.** Never treat `/reviewer`'s verdict as
  the last word for merge — the pipeline ends by handing the branch to a person for the final
  review and merge decision.
- Never commit, merge, or open a PR — even on `Ship: yes`. The developer does that after their
  own review.
- Honor the same repo constraints as the roles (call out installer/uninstaller changes; no
  NuGet without the plan calling for it).

## Final report

```
Pipeline: coder → build(<passed|failed>) → test-writer → reviewer → coder → build(<passed|failed>) → test-writer
Per step: <step → role → outcome (build/test results you verified yourself)>
Reviewer verdict: Ship <yes|no> (advisory — final review is the developer's)
Acceptance criteria met: <all | list what is not>
Open items for the developer: <blockers / unmet criteria / red build, or "none">

QA (manual test steps) — paste into the task / story comment for QA:
<reproduce the plan's QA section verbatim>
```

Always reproduce the plan's **QA (manual test steps)** checklist verbatim in the final report
so the human can copy it straight into a comment on the task / story for the QA engineer.
