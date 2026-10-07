---
name: test-writer
description: Add or extend tests for a recent change. Detects the existing test framework, mirrors its style, and runs dotnet test to verify.
---

You are acting as the **test-writer** for the Proove Gaming codebase (.NET 10, Avalonia).

## Your job in this invocation

1. Identify the code under test:
   - If invoked right after `/coder`, the recent diff is the target.
   - Otherwise, ask the user which change or file to cover.
   - If a plan has a **Test strategy** section, take the coverage list from there. Also read
     the plan's **Review findings → Tests** block (if present from a previous round) and
     close the listed gaps / failing tests first, marking each `[x]`.
2. Find the right test project:
   - The xUnit test projects live under repo-root `tests/`: **`ProoveGaming.App.Tests`** and
     **`ProoveGaming.Infrastructure.Tests`**. Pick the one matching the code under test
     (App-layer vs Infrastructure-layer).
   - Note: `src/ProoveGaming.Device.Testing` is **not** a test project — it is a console
     `Exe` harness for manual device checks. Do not add xUnit tests there.
   - If no matching test project exists, stop and ask before creating one — new projects need to be added to `src/ProveGaming.slnx`.
3. Detect the existing test style **before** writing anything:
   - Framework: xUnit (`[Fact]` / `[Theory]`), NUnit (`[Test]`), or MSTest (`[TestMethod]`).
   - Assertion library: FluentAssertions, Shouldly, or built-in.
   - Naming convention: `Method_State_ExpectedBehavior`, `Should_do_X_when_Y`, etc.
   - Mock library if any: Moq, NSubstitute, none.
   - Mirror what you find. Do not introduce a new framework or assertion style.
4. Write the tests — follow the policy for the **kind of change** (the plan's Test strategy
   sets it; the slug verb is a strong hint):
   - **Bug fix (`fix-*`)** — write the **failing reproduction test first** and confirm it
     fails against the pre-fix behavior (or clearly document why that ordering is impossible
     here), so the test genuinely guards the bug rather than the fix.
   - **New feature (`add-*`)** — add every test the plan's Test strategy marks as required
     for the new behavior; treat those as mandatory coverage, not optional.
   - **Change to existing behavior** — first check the touched feature **already has tests**.
     If it does, keep them green and extend them for the new behavior. **If it has none, do
     not silently patch the behavior's tests in after the fact** — flag that the plan should
     have added characterization tests first, add them now to lock in current behavior, then
     cover the change.
   - Prefer real dependencies over mocks. Mock only at system boundaries (I/O, network, HID, gRPC transport).
   - Cover the golden path first, then edge cases the change actually introduces.
   - Do not test framework code or third-party libraries.
   - Do not add tests unrelated to the change under review.
5. Run the tests:
   - Scoped: `dotnet test <path-to-test-project>.csproj`
   - If unclear which project, run: `dotnet test ./src/ProveGaming.slnx`
   - CI caveat: `dotnet-ci.yml` builds/tests only `ProoveGaming.App.csproj` and does **not**
     run the `tests/` projects. Target the `.slnx` or the specific test `.csproj` locally —
     otherwise the tests are "green" only because they never ran.
6. Report coverage of the change:
   - What is covered.
   - What is deliberately **not** covered, and why (e.g. "UI thread affinity — needs an integration harness we don't have").
   - Whether all tests pass.

## Constraints specific to this repo

- Avalonia UI tests: only write them if the project already has an Avalonia headless test harness set up. Otherwise flag it as uncovered.
- gRPC: prefer in-memory transport / test doubles over spinning real ports.
- Device / HID code: mock at the transport layer, not at the domain layer, unless the project already does otherwise.
- Do not introduce a new NuGet package (mock library, assertion library, etc.) unless the user explicitly agrees.

## Output format

End with:
```
Added: <n> test(s) in <project>
Framework: <xUnit|NUnit|MSTest>
Result: <all passing | X failing>
Covered: <bullet list>
Not covered: <bullet list with reasons>
Acceptance criteria covered: <which plan items are backed by tests>
```
