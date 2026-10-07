# Plan: TAIPAN DPI + polling rate (PG-410)

## Goal

Bring the TAIPAN's **DPI** and **polling rate** to parity with BLAZE in the app (part of
**PG-410**). Three user-facing behaviours: (1) the polling rate is settable from the UI —
TAIPAN has no polling-rate button, so this is UI-write only; (2) DPI stages are shown and, on
**Recommended**, written with the same value set BLAZE uses; (3) the UI **auto-updates the
active DPI stage live** when the user presses the physical DPI button on the mouse. Additionally,
because the TAIPAN protocol supports it (unlike the current BLAZE UI), clicking a DPI stage in the
UI switches the active stage on the device.

The device-facing writes are **cable-only** (the 2.4G dongle does not relay host→mouse frames —
established in the buttons/battery/macros work). The **live stage auto-update rides the mouse's
`0x49` push, which the dongle DOES relay**, so the "listen to the button" behaviour works on both
cable and dongle; only the writes require the cable.

## Scope

**In:**
- Polling rate: UI → device via a MsConfig **read-modify-write** (`0x41` read → patch → `0x01`
  write). Same 4 rates as BLAZE (1000/500/250/125 Hz).
- DPI stage **values**: shown from catalog config (BLAZE's `800…12000` set), and written to the
  device on **Recommended** via the DPI table (`0x02`), register = `round(DPI / 50)`.
- **Active DPI stage auto-update**: parse the device's `0x49` state push (already read by
  `TaipanStateMonitor`), stream it as a `DpiStageChange`, and reflect it in the UI — on cable
  **and** dongle.
- **UI DPI-stage switching for TAIPAN**: clicking a preset writes `DpiSwitch` via MsConfig RMW
  (`SetActiveDpiStage`, cable-only). BLAZE stays display-only/hardware-driven.
- `TelinkMouseService` gains **read capability** (send a `0x4X` read request, read the reply on
  the same handle) — the prerequisite for MsConfig RMW.
- New pure encoders `TaipanMsConfig` + `TaipanDpiTable` with unit tests.

**Out:**
- Independent X/Y DPI (the UI carries one value per stage → write it to both axes).
- Writing per-stage DPI **indicator colours** (`0x0C`) — colours stay display-only, exactly as
  BLAZE does; the single LED work is separate.
- Profiles (`0x07`), and the two TAIPAN-only power settings (motion-light-off, sleep timer) —
  later slices.
- `ButtonDebounce` / `LiftOffDistance` — no documented TAIPAN counterpart; not enabled.
- Dongle / Bluetooth config **writes** (hardware limitation). Stage auto-update over the dongle
  IS in scope (read-only push).

## Protocol facts (from `docs/mouse/taipan/protocol.md` + the vendor xlsx worked examples)

### MsConfig — command `0x01` write / `0x41` read, 19 (`0x13`) bytes, single packet
Worked full write: `01 01 01 01 00 00 00 01 03 01 FF 01 02 04 08 01 00 00 00`.
- Polling-rate bytes live in the `0x0B–0x0F` region (`01 02 04 08 01` in the example), encoded as
  the raw ms period **identical to BLAZE**: `0x01`=1000, `0x02`=500, `0x04`=250, `0x08`=125 Hz.
  The doc labels a `回报率` (report rate) and a `同回报率` (sync report rate) — the exact offset(s)
  and whether wired vs 2.4G rates differ are **verified on hardware** (read MsConfig, change the
  rate in the OEM app, diff). The read-modify-write makes this safe to pin down without wiping
  other settings.
- **`DpiSwitch`** (active DPI stage, range `0x00–0x07`) sits near offset `0x11` — likewise
  confirmed by read-back diff.
- Commit: `ValidBankID = 0x1A` at offset `0x007F`, then `0xD3` (same as the button table).
- **`0xD1` erases the sector**, so a single-byte change MUST be a full-block RMW or it wipes the
  polling rates and active stage together.

### DPI table — command `0x02` write / `0x42` read
Worked full write decodes cleanly as `[maxStageCount, then per stage: enable, X_L, X_H, Y_L, Y_H]`
(5 bytes/stage, little-endian; 8 stages). The vendor example stage values `0x0009, 0x0012,
0x002A, 0x0056, 0x008F, 0x018F` are **all exact multiples of 50 as DPI** (450, 900, 2100, 4300,
7150, 19950), and BLAZE's ranges encode cleanly the same way (`800→0x0010, 1200→0x0018,
2400→0x0030 … 12000→0x00F0`). So **DPI register = `round(DPI / 50)`**, written to both X and Y.
The address-table's per-axis "double" flag and per-stage RGB (a conflicting older doc section) are
NOT in the current-firmware worked example and are out of scope. Commit: `ValidBankID` at `0x007F`
→ `0xD3`. Full-table write (no read needed).

### `0x49` state push (already parsed by `TaipanStateMonitor.TryParseState`)
`Para1 = profile, Para2 = DPI stage, Para3 = report-rate raw`. The mouse pushes it on DPI-button
press and link establishment; the dongle relays it. This is the live auto-update source.

## Options considered

### A. Getting the current MsConfig for the read-modify-write
1. **`TelinkMouseService` reads on its own HID handle** — *recommended.* Windows broadcasts HID
   input reports to every open handle, so the service's `0x41` reply arrives on its handle even
   while `TaipanStateMonitor` holds the same collection open (the monitor ignores non-`0x48/0x49`
   replies). This is exactly how `EasenMouseService` reads coexist with `DpiStageMonitor` for
   BLAZE. Cable-only (queries aren't relayed over the dongle).
2. Route reads through `TaipanStateMonitor` — rejected: the monitor is a read-only state listener;
   giving it write-adjacent RMW duties muddies ownership.

### B. Active-DPI-stage source of truth
1. **Persisted stage on open + live `0x49` push for changes** — *recommended.* Matches the
   existing app flow (`LoadActiveDpiStageAsync` on device open, `HighlightActiveDpiStage` on
   push). No new "read current stage" round-trip needed; works identically on cable and dongle.
2. Read `DpiSwitch` from MsConfig on open — more accurate on cable but cable-only and adds a
   read the dongle can't serve; deferred (the persisted value is correct in practice since only
   the button or our own writes change it).

### C. UI DPI-stage switching
1. **Device-aware `SelectDpiPreset`: TAIPAN writes `SetActiveDpiStage`, BLAZE stays display-only**
   — *recommended.* The docs support `DpiSwitch`, the user asked for it, and the gRPC path already
   exists; only TAIPAN opts in, so BLAZE behaviour is untouched. Cable-only write; the physical
   button still drives it over the dongle.
2. Keep display-only for both (mirror BLAZE) — simpler, but ignores a capability the user
   explicitly asked to use.

### D. DPI value writing
1. **Write BLAZE's set on Recommended, register = DPI/50** — *recommended*, gated on a hardware
   read-back check (`0x42`) in QA. The clean ×50 fit across BLAZE's values and the vendor example
   is strong evidence.
2. Read-only: show the device's own values, never write — the fallback if QA shows the register
   formula is wrong. Kept as the documented contingency ("use what the device has").

## Recommended approach

1. **`TaipanMsConfig`** (pure): `Length = 19`; `PollingRateByte(int hz)` / `HzFromByte(byte)`
   (same table as `X43Settings`); `PatchPollingRate(byte[] block, int hz)` and
   `PatchActiveStage(byte[] block, int stage)` that set the identified offset(s) on a
   read-back block; offset constants (`PollingRateOffset`, `DpiSwitchOffset`) with a comment that
   they are hardware-confirmed.
2. **`TaipanDpiTable`** (pure): `BuildTable(IReadOnlyList<int> cpiValues)` → the `0x02` payload
   (`[count, per stage enable+X_L+X_H+Y_L+Y_H]`, X=Y, disabled stages zeroed); `DpiRegister(cpi)
   = round(cpi/50)`, `CpiFromRegister`, constants `MaxStages = 8`, `BytesPerStage = 5`.
3. **`TelinkMouseService`**:
   - `ReadTableAsync(byte readCmd, int addr, int len)` — write the `0x4X` request, read the reply
     on the same handle, validate `driverCMD`/checksum, return the FIFO payload (or null). Cable
     only; returns null when no reply (dongle) so callers can no-op cleanly.
   - `SetReportRateAsync(hz)` — read MsConfig (`0x41`), `PatchPollingRate`, write full block
     (`0x01` `0xD1` → ValidBankID@`0x007F` → `0xD3`), paced, under `_writeLock`.
   - `SetActiveDpiStageAsync(stage)` — same RMW patching `DpiSwitch`.
   - `SetDpiStagesAsync(stages)` — `TaipanDpiTable.BuildTable` → `0x02` write (`0xD1`→`0xD2`
     chunks→ValidBankID→`0xD3`); colours ignored (display-only), value = `DpiValue`.
   - `RequestCurrentStateAsync` — stays a no-op for TAIPAN (persisted value + `0x49` push cover
     the current stage); returns false.
4. **`TaipanStateMonitor`**: add `StageChanged` / `ReportRateChanged` events (same shapes as
   `DpiStageMonitor`), raised from the parsed `0x49` push with de-dupe per unit.
5. **`DeviceService.SubscribeOnDevices`**: also subscribe/unsubscribe `_taipanStateMonitor`'s
   `StageChanged` / `ReportRateChanged` and stream them via the existing `DpiStageChange` /
   `ReportRateChange` messages (reuse `DpiStageMonitor_StageChanged`'s body via a shared helper).
6. **App**: `SelectDpiPreset` becomes device-aware — for TAIPAN it writes `SetActiveDpiStageAsync`
   and persists; for others it stays display-only. The DPI stage options become hit-testable when
   `Device` supports UI stage switching (TAIPAN). `ApplyDpiRecommendations` and `SelectPollingRate`
   already call the right gRPC methods — no change beyond TAIPAN now honouring them.
7. **Config**: add `"DpiStages": [800,1200,2400,3600,4800,6000,12000]` to the three TAIPAN
   entries so the UI shows BLAZE's set (the plumbing `AllowedDevice.DpiStages` → `DeviceMap` →
   catalog already works). No `SupportedMouseSettings` change (debounce/lift-off stay off).

## File-level change list

- `src/ProoveGaming.Infrastructure/Classes/TaipanMsConfig.cs` *(new)* — polling/stage byte
  patching + rate encoding.
- `src/ProoveGaming.Infrastructure/Classes/TaipanDpiTable.cs` *(new)* — `0x02` table builder +
  DPI↔register conversion.
- `src/ProoveGaming.Infrastructure/Services/TelinkMouseService.cs` *(edited)* — read helper +
  `SetReportRateAsync` / `SetActiveDpiStageAsync` / `SetDpiStagesAsync` implementations.
- `src/ProoveGaming.DeviceHost/Services/TaipanStateMonitor.cs` *(edited)* — `StageChanged` /
  `ReportRateChanged` events raised from `0x49`.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` *(edited)* — subscribe + stream TAIPAN
  stage/rate changes (shared helper with the DpiStageMonitor handlers).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs` *(edited)* —
  device-aware `SelectDpiPreset` (TAIPAN writes active stage + persists).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceCardViewModel.cs` *(edited)* — a
  `SupportsDpiStageSwitch` capability flag (true for TAIPAN) driving the option hit-testing.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/DpiSectionView.axaml` *(edited)* — bind DPI
  option `IsHitTestVisible` to the capability (display-only unless the device supports switching).
- `src/ProoveGaming.DeviceHost/appsettings.json` *(edited)* — `DpiStages` on the 3 TAIPAN entries.
- `tests/ProoveGaming.Infrastructure.Tests/TaipanMsConfigTests.cs` *(new)*,
  `TaipanDpiTableTests.cs` *(new)* — see Test strategy.
- `docs/CONTEXT.md` *(edited)* — add **DpiSwitch** / **MsConfig** terms.

**No proto changes** — `SetDpiStages`, `SetActiveDpiStage`, `SetReportRate`, `DpiStageChange`,
`ReportRateChange` all already exist and are routed by `DeviceService`.

> **Presentation superseded (2026-08-13, branch `fix/PG-506-Change-preset-on-save`).** This plan
> shipped the DPI stages as one-per-row toggle switches. They were later redesigned as **radio
> buttons, three per row**, each with a small colour dot left of its value and a shared cut-corner
> hover frame around the radio circle (the same motif as the Details back button), with the Polling
> Rate group adopting that hover frame too. The redesign changed presentation only — the capability
> gating (`SupportsDpiStageSwitch` → `IsInteractive` → `IsHitTestVisible`), the `OneWay` binding to
> the device's active stage, and every acceptance criterion below still hold. The wording above and
> below says "option" rather than "tile"/"toggle" for that reason. See
> `src/ProoveGaming.App/Views/DeviceDetails/Sections/DpiSectionView.axaml`.

## Acceptance criteria

1. TAIPAN connected by cable → DPI section shows 7 stages with BLAZE's values (800…12000).
2. Press the physical DPI button → the highlighted stage in the UI advances to match, live, on
   **cable and dongle** (criterion for "listen to the mouse button").
3. Click a DPI stage option (cable) → the mouse's active stage changes to it (cursor speed
   changes) and the option highlights; over the dongle the option is display-only.
4. Click **Recommended** (cable) → the DeviceHost logs a `0x02` DPI write; reading the table back
   (`0x42`) shows registers = value/50; cursor speed at each stage matches the shown DPI.
5. Change polling rate in the UI (cable) → DeviceHost logs a MsConfig write; the rate takes effect
   and the other MsConfig bytes (active stage, etc.) are unchanged (RMW preserved them).
6. Reopen the app → the last active stage and polling rate are restored from persistence.
7. BLAZE regression → DPI stages, Recommended, polling rate, and the physical-button live update
   all behave exactly as before.
8. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` green, incl. the
   new encoder tests.

## Test strategy

`add-*` feature — pure-logic coverage required; device I/O is QA.

- **`TaipanDpiTableTests`**: `DpiRegister(800)==0x10`, `(1200)==0x18`, `(2400)==0x30`,
  `(12000)==0xF0`; round-trip `CpiFromRegister`; `BuildTable` golden bytes for BLAZE's 7-value set
  (`[07, 01,10,00,10,00, 01,18,00,18,00, …]`), X==Y, unused stages zeroed, count byte correct;
  values clamped to the 16-bit register range.
- **`TaipanMsConfigTests`**: `PollingRateByte` table (1000→0x01…125→0x08) + inverse;
  `PatchPollingRate` changes ONLY the polling offset(s) of a read-back block and leaves every
  other byte intact; `PatchActiveStage` changes only `DpiSwitch`; stage clamped 0–7.
- **Not automated (QA):** the `0x41`/`0x42` reads and `0x01`/`0x02` writes on hardware, the exact
  MsConfig offsets, the DPI-register formula (read-back check), and the `0x49`→UI live update.
  `TelinkMouseService` stays I/O-only, untested by design.

## QA (manual test steps)

1. Connect the TAIPAN **by cable**. Open its card → DPI section. **Expected:** 7 stages
   800/1200/2400/3600/4800/6000/12000, first one highlighted (or the last-used one).
2. Press the mouse's **DPI button** a few times. **Expected:** the highlighted stage advances in
   step with each press. Repeat on the **dongle** — it must still track the button.
3. Click a different DPI stage option (cable). **Expected:** cursor speed changes to that stage
   and the option highlights. On the dongle the options don't respond (display-only) — not a bug.
4. Click **Recommended** (cable). Move the cursor at each stage. **Expected:** speeds match the
   shown DPI values; no stage feels wrong or dead.
5. Change the polling rate to 500 Hz (cable), then re-open the DPI section. **Expected:** the DPI
   stages are unchanged (the polling write didn't wipe them) and 500 Hz stays selected.
6. Close and reopen the app. **Expected:** the active stage and polling rate are as you left them.
7. Regression — repeat 1–5 on a BLAZE. **Expected:** everything behaves as before; the DPI options
   on BLAZE remain display-only and only the physical button changes the stage.
8. Switch TAIPAN to the dongle and change polling rate / click Recommended. **Expected:** the UI
   updates and persists, but the device does not change until reconnected by cable (cable-only,
   known limitation).

## Review focus

`thread-safety` (MsConfig RMW + button/macro writes share `_writeLock`; the read helper must not
deadlock against the monitor's reader), `async/await` (paced reads/writes stay awaited),
`gRPC contract` (reuse of `DpiStageChange`/`ReportRateChange` for TAIPAN), `null-safety`
(read helper returns null over the dongle — every caller no-ops), `performance hot-path` (none —
user-action frequency).

## Performance & quality

No hot paths. A DPI/polling write is a rare user action: one MsConfig RMW is a read (≤2.5 s
timeout, typically instant on cable) plus a paced single-packet write; a DPI-table write is ~2
packets. The `0x49` live-update path is already running in `TaipanStateMonitor` and only adds a
per-unit de-duped event raise (no allocation on the steady state). All device writes stay
serialized behind the existing `SemaphoreSlim`.

## Risks

- **MsConfig offset uncertainty** (polling byte(s), `DpiSwitch`). Mitigated by RMW (only the
  target byte changes) + a hardware read-back diff in QA. If the polling rate has separate
  wired/2.4G bytes, write both to the requested value.
- **DPI register = DPI/50 is a strong hypothesis, not vendor-stated.** QA step 4 (read-back /
  cursor-speed) is the gate; the read-only fallback ("use what the device has") is the contingency.
- **Read/monitor coexistence.** The read helper relies on Windows broadcasting HID input reports
  to all open handles (proven by the BLAZE monitor + Easen read coexistence). If a reply is ever
  missed, the RMW aborts (null read) rather than writing a fabricated block — never a partial
  MsConfig.
- **Everything device-facing is cable-only.** The auto-update (read-only `0x49`) is the sole
  dongle-capable piece; writes over dongle/BT no-op, consistent with buttons/macros.

## Open questions

1. Confirm the exact MsConfig polling offset(s) and whether wired vs 2.4G polling differ (hardware
   read-back). Plan assumes a single active-profile polling byte, writing the sync byte too if
   present.
2. Confirm DPI register = DPI/50 on hardware. If false, fall back to read-only display of the
   device's own values.
3. UI stage-switching affordance — plan enables it for TAIPAN (user asked). Confirm the
   display-only BLAZE options should stay display-only (yes, per current behaviour).

## Review findings

### Round 1 — Ship: yes (with should-fixes)

Hardware truths established this round, now confirmed and worth treating as settled:
`0x0B` = polling rate, `0x11` = DPI stage (proved by the byte moving on a physical button press),
DPI register = CPI/50 (encoder only — see the test gap), MsConfig takes **no** ValidBankID packet,
and MsConfig's stored DPI-stage byte **lags** the live stage.

#### Code
- [ ] `TaipanMsConfig.cs` (~21-28) — Should-fix. The "NOTE (hardware-derived, 2026-07-20)" block
  documents a **superseded, incorrect** diagnosis as fact: it claims the offsets are *not* where
  the vendor table put them, that `0x11` "is a rate byte", and that "the stage patch is not used
  at all". All three are now false — `0x0B`/`0x11` are confirmed as rate/stage and the stage patch
  is used again by `SetActiveDpiStageAsync`. The class header also still describes writing "every
  polling slot" though `PollingRateSlots` is 1. This is the exact file whose offsets caused several
  rounds of device corruption; leaving a wrong explanation in it is a live trap for the next
  maintainer. Rewrite both comments to the confirmed facts.
- [ ] `TelinkMouseService.cs` (~54) — Should-fix (thread-safety, a plan Review-focus item).
  `_liveDpiStage` is `byte?` — a two-field `Nullable<byte>` struct — written from the monitor's
  reader thread via `SyncStateFromDevice` and read under `_writeLock` in `PatchMsConfigAsync`. The
  comment asserts "Byte assignment is atomic", which is not guaranteed for `Nullable<T>`; a torn
  read can yield `HasValue=true` with a stale value and write the wrong DPI stage — precisely the
  failure this round fixed. Use a `volatile int` with a `-1` sentinel (or `Interlocked`).
- [ ] `TelinkMouseService.cs` — Should-fix (cold start). `_liveDpiStage` is populated only by a
  `0x49` push (physical button) or our own stage write. After a fresh DeviceHost start with no
  button press, the first polling change writes the block's **stale** stage and can still jump the
  DPI once. Seed it — e.g. from the App's persisted active stage on connect, or trust the first
  read and reconcile on the next push.
- [ ] `TelinkMouseService.cs` — Nit. `RequestCurrentStateAsync` routes through `NotImplemented`,
  logging "not implemented for TAIPAN yet" although its own comment says the no-op is deliberate.
  Misleading log.
- [ ] `TelinkMouseService.cs` — Nit. The DPI-table write reaches into `TaipanButtonMap.ValidBankIdOffset`
  for a constant shared by several tables; it reads oddly from the DPI path. Consider hoisting to
  `TaipanFrame` or a shared constants holder.
- [ ] `DeviceDetailsViewModel.Settings.cs` — Nit. `SelectDpiPreset` highlights optimistically before
  the (cable-only) write; over the dongle the UI shows a stage the device never took until the next
  push corrects it. Acceptable given the persistence story, but worth an explicit note.

#### Tests
- [ ] `PatchMsConfigAsync`'s decision logic is untested — Should-fix. The "plausible read → use it /
  implausible → last-committed / neither → skip", plus the live-stage override, is exactly the logic
  that produced every bug this round, and it is currently verified only by hand on hardware. It is
  pure decision-making around the I/O and can be extracted (e.g. a `ResolveSourceBlock` helper) and
  covered without a device.
- [ ] Acceptance criterion 4 (Recommended writes DPI values; register = CPI/50) is **not verified on
  hardware** — the encoder is unit-tested but the end-to-end write was never exercised in QA this
  round. Either run QA step 4 or record CPI/50 as still-unconfirmed.
- [x] Encoder coverage — `TaipanDpiTableTests` (golden table, CPI/50 both directions, unused-stage
  zeroing, clamping) and `TaipanMsConfigTests` (rate encode/decode, patch isolation, stage clamp,
  erased-sector guard) are present and green (285 total).

### Round 2 — Ship: yes (with should-fixes)

Reviewed the whole 7-commit branch delta (working tree clean; the "uncommitted" note in the
request was stale). Build green, 295 tests pass. Round 1's code items are still open except the
`TaipanMsConfig` NOTE block, which was rewritten but left an internally contradictory header.

Scope now extends well beyond this plan: BLAZE UI DPI switching, TAIPAN lift-off distance, the
TAIPAN colour palette, and seven installer fixes all landed here. The plan text still describes
the pre-drift decisions, so QA following it will report false regressions.

#### Code
- [ ] Plan is out of date vs shipped behaviour — Should-fix. Option C and acceptance criterion 7
  say "BLAZE stays display-only" / "BLAZE behaves exactly as before", but BLAZE UI stage switching
  is now implemented (commit 0621a24) at the user's request. Criterion 3 says the tile is
  display-only over the dongle, but `SupportsDpiStageSwitch` is transport-independent, so TAIPAN
  tiles are clickable on the dongle and silently no-op on the device. Lift-off is listed under
  **Out** yet `SupportedMouseSettings: ["LiftOffDistance"]` ships. Update Scope, Acceptance
  criteria and QA before handing the checklist to QA.
- [ ] `MainWindow.Installation.cs` (~168) — Should-fix. `await CleanupStagingDirectoryAsync()` sits
  in the `finally`, which runs *after* `Close()` on the cancel-and-close path. Closing the main
  window starts app shutdown, so the thread-pool delete is likely killed mid-flight and the
  extracted payload (hundreds of MB) leaks in `%TEMP%` — on the exact path the cleanup was added
  for. Clean up before `Close()`, or fold it into `RollbackInstallAsync`.
- [ ] `MainWindow.Installation.cs` (~18-22, ~55) — Should-fix. `_installFolderPreexisted` /
  `_servicePreexisted` are instance fields never reset per run and are assigned *after* the first
  `WaitIfInstallPausedOrCancelled`. A cancellation before the snapshot runs rollback on stale/default
  `false` values, which permits `Directory.Delete(installPath, recursive: true)` on a folder this run
  did not create. The window is sub-millisecond and needs two human clicks, so it is not reachable in
  practice — but this is the destructive class of bug the commit set out to fix. Initialise both to
  the safe default (`true`) alongside `_closeAfterInstallCancellation = false`.
- [ ] `MainWindow.Installation.cs` `RollbackInstallAsync` — Should-fix. Cancelling a **re-install**
  now correctly preserves the folder and service, but the copy has already overwritten part of the
  previous installation, and nothing restores it. The user is left with a mixed old/new file set that
  may not run, and no rollback path. Either back up before overwriting, or warn on the cancel modal
  that a re-install cannot be rolled back.
- [ ] `MainWindow.Installation.cs` `ExtractEmbeddedPayloadAsync` — Should-fix. The extraction takes no
  `CancellationToken`, so confirming cancel during extraction still runs the unzip to completion
  before rollback begins. The dialog opens instantly (fixed), but "cancel dismisses all progress" is
  only true after a multi-second delay.
- [ ] `TaipanMsConfig.cs` (~10-14) — Should-fix (carried from Round 1, partially closed). The stale
  NOTE is gone, but the header still says "every polling slot is written to the requested value —
  that keeps wired and 2.4G in step", which contradicts `PollingRateSlots = 1` and the layout comment
  20 lines below ("Only profile 1's polling slot is written"). Same file, same trap as Round 1.
- [ ] `TelinkMouseService.cs` (~59-62) — Should-fix (carried from Round 1, unchanged). `_liveDpiStage`
  is still `byte?` with the comment "Byte assignment is atomic", which is untrue for `Nullable<byte>`
  and provides no memory barrier between the monitor's reader thread and `PatchMsConfigAsync`. Use a
  `volatile int` with a `-1` sentinel.
- [ ] `TelinkMouseService.cs` — Should-fix (carried from Round 1, unchanged). `_liveDpiStage` cold
  start: after a DeviceHost restart with no button press and no successful `RequestStateRefresh`
  (e.g. dongle), the first polling write still commits the block's stale stage.
- [ ] `TaipanMsConfig.PatchLiftOffDistance` (~106) — Should-fix. The comment says the value is
  "passed through unchanged and confirmed on hardware", but the units (mm vs index) are recorded
  elsewhere as still unconfirmed, and there is no QA record. Either confirm and cite it, or state
  plainly that the units are unverified.
- [ ] `TaipanStateMonitor.RequestStateRefresh` (~117-135) — Nit. `state.LastStage` is written from the
  spawned `Task.Run` while the reader loop writes it too; an interleave costs at most one duplicate
  stage event.
- [ ] `DeviceDetailsViewModel.cs` (~262) — Nit. `"PG TAIPAN"` is now compared in three App files plus
  the factory. Worth a shared constant.
- [ ] `TelinkMouseService.RequestCurrentStateAsync` — Nit (carried from Round 1). Still logs "not
  implemented for TAIPAN yet" for a deliberate no-op.
- [ ] `TaipanStateMonitor` (~213) — Nit. `SyncStateFromDevice(rate, …)` passes Para3 into a parameter
  named `reportRateRaw` three lines after a comment establishing Para3 is not the rate. Harmless
  (Telink ignores it) but confusing.

#### Tests
- [ ] `PendingOperationTargets` / `ClearPendingRebootDeletions` are untested — Should-fix. These guard
  a **destructive, elevated HKLM write** to a value shared with every other installer on the machine,
  and the path filter is the only thing preventing collateral deletion of another app's pending
  operations. `PendingOperationTargets` is a pure function: cover the `\??\` prefix, prefix-vs-sibling
  paths (`C:\App` must not match `C:\AppOther`), trailing separators, and odd-length REG_MULTI_SZ.
  Requires making it visible to tests (`InternalsVisibleTo` already used for installer types).

  **Still open, but lower risk as of 2026-09-15.** The uninstaller no longer writes reboot-scheduled
  deletions at all (`docs/support/missing-files-crash-analysis.md`, cause 3), so this clear now only
  has to cope with entries left by uninstaller builds already in the field, and is expected to be
  deleted once those age out. The HKLM write it guards is unchanged, so the tests are still worth
  having while the code exists.
- [ ] `PatchMsConfigAsync` decision logic still untested — Should-fix (carried from Round 1, unchanged).
- [ ] Acceptance criterion 4 (CPI/50 on hardware) still unverified — carried from Round 1.
- [x] Encoder coverage green: 253 Infrastructure + 42 App tests pass.
