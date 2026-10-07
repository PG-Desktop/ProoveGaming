# Plan: refactor-actuation-single-select

## Goal

Change the Actuation section's Per-key mode from multi-select (each click toggles a key's
membership in a selection set, edits write to every selected key) to single-select: at any moment
the user is editing either **All keys** or **exactly one key**. Clicking a key selects it and
deselects whatever was selected before. This simplifies the mental model ("all or one") and removes
the ambiguity of sliders showing one key's values while writing to several.

## Scope

**In**

- Selection semantics in `DeviceDetailsViewModel` (Actuation partial + the `SelectPhysicalKey`
  branch in the KeyAssignments partial).
- The selected-keys label (now names at most one key) and the two localization resources it uses.
- Updating the existing per-key actuation unit tests to the new semantics.
- Comment updates where the old multi-select behavior is described.

**Out**

- No gRPC / proto change: `SetPerKeyActuationAsync` keeps its `IReadOnlyCollection<int> slots`
  signature (the App simply always sends one slot). The DeviceHost, Infrastructure, and wire
  protocol are untouched.
- No change to All-keys mode, the debounce/latest-wins write pipeline, the majority recompute, the
  failure-reload path, or the mode toggle UI.
- No visual redesign of `ActuationSectionView.axaml` — the hint/label block already handles the
  0-or-1 selection states.

## Options considered

1. **Keep the `List<KeyAssignmentOptionViewModel>` selection storage, enforce single membership at
   the click site.** Smallest diff: `ToggleActuationKey` clears the list before adding. But it
   leaves plural machinery lying: the "Selected: N keys" count format, the
   `ActuationMaxNamedSelectedKeys` threshold, loops over a list that can only ever hold one item.
   Future readers must rediscover that the list is secretly a scalar.

2. **Replace the list with a single nullable field (`_actuationSelectedKey`).** More lines touched,
   but the type now states the invariant: the compiler enforces "at most one", the label logic
   collapses to one format string, the count format resource and the naming threshold constant are
   deleted, and `BuildActuationWriteRequest` builds a one-element slot array. Test updates are the
   same size either way.

**Recommended: option 2.** The whole point of the change is the "one key" invariant — encode it in
the type rather than in a comment. The removed plural paths (`SelectedCount_Format`, the max-named
threshold) would otherwise be dead code.

### Same-key click behavior (sub-decision)

Clicking the already-selected key **deselects it**, returning to the "no selection" state (hint
text shown, controls disabled). Rationale: that neutral state already exists and is fully styled;
keeping the toggle-off affordance preserves a way to "step back" without switching modes, and it
minimizes churn in the existing empty-selection tests. (The Key-Assignments radio semantics — a
re-click keeps the selection — are a live alternative; flagged under Open questions.)

## Recommended approach

In `DeviceDetailsViewModel.Actuation.cs`:

- Replace `private readonly List<KeyAssignmentOptionViewModel> _actuationSelectedKeys = []` with
  `private KeyAssignmentOptionViewModel? _actuationSelectedKey`.
- `ToggleActuationKey` (rename to `SelectActuationKey`, keep `internal`): flush the pending edit
  first (unchanged — the edit belongs to the previous selection), then:
  - clicked key == current selection → clear selection (`IsSelected = false`, field = null);
  - otherwise → clear the previous key's `IsSelected`, select the clicked key, load its values
    into the sliders (existing `ShowActuationValuesForLastSelectedKey`, renamed to
    `ShowActuationValuesForSelectedKey` and reading the field instead of `[^1]`).
- `HasActuationSelection` → `_actuationSelectedKey is not null`.
- `ActuationSelectedKeysLabel` → returns `Selected_Format` with the single key's label, or empty.
  Delete `ActuationMaxNamedSelectedKeys` and the count branch.
- `BuildActuationWriteRequest` → in per-key mode, `slots = [_actuationSelectedKey.MatrixIndex]`
  (null request when no selection, as today).
- `ClearActuationSelection`, `ScheduleActuationApply` guard, and the two call sites in
  `LoadActuationFromDeviceAsync` switch from `.Count`/loops to the single field.
- The debounce / flush / latest-wins pipeline, `ActuationWriteRequest` record, and per-key cache
  update in `ApplyActuationToDeviceAsync` are **unchanged** — they already work off the slot array
  snapshot, which now just always has length 1 in per-key mode.

In `DeviceDetailsViewModel.KeyAssignments.cs`: update the actuation-branch comment in
`SelectPhysicalKey` (line ~1162) from "multi-selects / toggles membership" to single-select
wording; the call becomes `SelectActuationKey(option)`.

In `DeviceDetailsView.axaml`: update the overlay comment (line ~641) that documents the
multi-select behavior.

Localization (`Strings.en.axaml`, `Strings.uk.axaml`):

- `DeviceDetails_Actuation_SelectKeysHint` → singular wording, e.g. en: "Click a key on the
  keyboard to adjust it." / uk: "Клацніть клавішу на клавіатурі, щоб налаштувати її."
- `DeviceDetails_Actuation_Selected_Format` stays (now always one name).
- Delete `DeviceDetails_Actuation_SelectedCount_Format` from both files (unused after the change).

## File-level change list

| File | Intent |
| --- | --- |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs` | Replace selection list with a single nullable field; single-select click handling; simplify label; one-slot write request; drop the max-named constant. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | Rename the actuation branch call, fix its comment to describe single-select. |
| `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` | Comment-only: overlay note no longer says "multi-selects". |
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | Singular select-key hint; remove the now-unused `SelectedCount_Format`. |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | Same as en. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs` | Rewrite multi-select tests to single-select semantics; add replace-selection and same-key-deselect tests; single-slot write assertions. |

## Acceptance criteria

- [ ] Given Per-key mode with no selection, when the user clicks key W, then W is highlighted, the
      sliders show W's values, and the label reads "Selected: W".
- [ ] Given W is selected, when the user clicks key A, then only A is highlighted (W's highlight is
      removed), the sliders show A's values, and the label reads "Selected: A".
- [ ] Given W is selected, when the user clicks W again, then nothing is selected, the controls are
      disabled, and the select-a-key hint is shown.
- [ ] Given W is selected, when the user moves the press slider and the debounce elapses, then
      exactly one per-key write is issued whose slot list contains only W's matrix index; the
      global (all-keys) write path is not used.
- [ ] Given W is selected with a slider edit still inside its debounce window, when the user clicks
      A, then the pending edit is flushed as a write targeting W only, and the subsequent edit
      targets A.
- [ ] Given a selection, when the user leaves the Actuation section or switches to All keys, then
      the selection is cleared and the Key-Assignments radio selection is restored (existing
      behavior, must stay green).
- [ ] The string resources `DeviceDetails_Actuation_SelectedCount_Format` no longer exist in
      en/uk resources, and no code references them (`grep` finds no hits).
- [ ] `dotnet build ./src/ProveGaming.slnx` succeeds; `dotnet test ./src/ProveGaming.slnx` is green.

## Test strategy

This is a behavior change to a feature that **already has tests**
(`tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs`, per-key block) — they are
updated in the same change, not before, since the old semantics are being deliberately replaced:

- **Updated:** `KeyClicks_ToggleMembershipInTheSelection` → becomes
  `KeyClicks_MoveTheSingleSelection`: click W then A → only A selected, `SelectedPhysicalKey`
  (radio) still null; click A again → empty selection.
- **Updated:** `PerKeyEdits_CoalesceIntoOnePerKeyWriteTargetingTheSelectedSlots` → selection is one
  key; the write's `Slots` equals exactly `[A.MatrixIndex]` after clicking W then A (replace, not
  accumulate).
- **Kept as-is (must stay green):** `EmptySelection_DisablesTheControlsAndNothingIsEverWritten`,
  `ClickingAKey_ShowsItsOwnValuesWithoutWriting`, `SuccessfulPerKeyWrite_UpdatesTheLocalCache…`,
  `FailedPerKeyWrite_RaisesTheBanner…`, both B1 in-flight regression tests, both S1
  radio-vs-actuation selection tests, `LeavingTheActuationSection_DropsPerKeyModeAndSelection` —
  these all already use a single selected key.
- **New:** `SelectingASecondKey_FlushesThePendingEditToTheFirstKey` — edit W inside the debounce
  window, click A → first write targets W's slot with the edited value.
- **New:** label test: one selected key → `ActuationSelectedKeysLabel` contains the key label;
  empty → empty string.

Not covered by automated tests (as today): real HID traffic, Avalonia visual states
(highlight rendering, disabled opacity), and UI-thread dispatch — the VM tests run the same
synchronous-context pattern the existing suite uses.

## QA (manual test steps)

1. Launch the app with a Cyrex (or other actuation-capable) keyboard connected and open its device
   page → **Actuation** section. Expected: "All keys" / "Per-key" mode buttons are visible.
2. Click **Per-key**. Expected: sliders are dimmed/disabled and a hint asks you to click a key on
   the keyboard image.
3. Click the **W** key on the keyboard image. Expected: W becomes highlighted, the sliders light up
   and show W's values, the text under the mode buttons reads "Selected: W".
4. Click the **A** key. Expected: the highlight moves — only A is highlighted (W is no longer), and
   the label reads "Selected: A". It must be impossible to have two keys highlighted at once.
5. Click **A** again. Expected: no key is highlighted, sliders dim again, the hint text returns.
6. Select **Q**, drag the "Initial Actuation Point" slider to a new value, wait a second. Expected:
   no error banner; re-select another key and come back to Q — Q still shows the value you set.
7. Verify only Q changed: switch to **All keys**. Expected: the whole-board values do not jump to
   Q's new value (majority of the board is unchanged).
8. While a key is selected, switch sections (e.g. to Presets) and return to Actuation → Per-key.
   Expected: the selection is cleared, hint is shown, nothing was written by itself.
9. Switch the app language to Ukrainian (and back). Expected: the hint and the "Selected: …" label
   are translated; no raw resource keys or English leftovers in the Ukrainian UI.

## Review focus

- `UI-thread affinity` — the selection state stays UI-thread-only; no new async hops.
- `async/await` — the flush-before-mutate discipline (pending edit committed against the old
  selection) must survive the rewrite.
- `localization` — resource removed from both files, hint reworded in both, no orphaned keys.
- `null-safety` — the new nullable selection field is guarded at every read.

## Performance & quality

No hot paths touched. The change removes allocations (no list, no name-join for multi-key labels).
The write pipeline (debounce, latest-wins, snapshot requests) is untouched, so T800 flash-wear
behavior is unchanged. All selection state remains UI-thread-only, matching the existing
"UI-thread-only" comments — no locking needed.

## Risks

- The B1 regression guarantee (a mode/section switch mid-write must not retarget a pending edit)
  is easy to break if the rewrite bypasses `FlushPendingActuationApply` on the deselect path —
  the two in-flight tests must stay green untouched.
- The `IsSelected` flag is shared with the Key-Assignments/Macros radio selection (S1). The
  single-select clear must only touch the actuation selection, exactly as `ClearActuationSelection`
  does today.
- Removing `SelectedCount_Format` breaks nothing at compile time if a stale reference survives in
  AXAML (DynamicResource fails silently) — the reviewer should grep for the key.

## Open questions

- Same-key click: this plan deselects (back to hint state). If the intended UX is radio-style
  ("a selected key stays selected until another is clicked"), say so before `/coder` runs — the
  diff is one branch in `SelectActuationKey` and one test.

## Review findings

_filled in by /reviewer_
