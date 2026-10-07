# BLAZE / X43 — DPI Stage & Report-Rate Monitoring

How the app knows which DPI stage **and report (polling) rate** the mouse is currently
on, and how that flows to the UI — including reading the *current* values on startup.

> The DPI stage and the report rate are **carried by the same `0x05` vendor report**
> (report rate at byte offset 2, DPI stage at offset 3). So everything below about the
> `0x80` query and the Col04 reader applies to *both* — one mechanism, two values. The
> report-rate specifics are in the [Report rate](#report-rate-polling-rate) section at
> the end.

## Reading the current stage: the 0x80 query → 0x05 notification

**You cannot read the active DPI stage with a HID feature read** — those return all
zeros (confirmed even for the OEM VS750 app: its `bmRequestType 0xA1` reads come back
`00 00 00 00 00 00 00 00`, and `ReadFile` on the mouse/keyboard collections is denied
with `err 5`). For a long time we believed the stage was therefore unreadable on
startup and kept `200` as a default.

**A cold-start USB capture (`cap-10.07.2026-with-startup.pcapng`) proved otherwise.**
The mechanism is:

```
host → device:  SET_REPORT (feature)  data:[80 00 00 00 00 00 00 …]   # command 0x80
device → host:  INTR-IN 0x05 notify:  05 00 02 05 02 00 00            # current state
                                              ^^ dpi_stage = 5
```

**Sending command `0x80` makes the firmware push a `0x05` state report** carrying the
current `[profile, reportRate, dpi_stage, ledEffect]`. That `0x05` report is the exact
same one the physical DPI button produces — and the same one `DpiStageMonitor` already
reads on Col04. So to learn the current stage we don't read at all; we **poke the
device with `0x80` and catch the notification it emits.**

In the cold-start capture, VS750 issued **zero** feature reads at startup — its only
startup query was this single `0x80`. So this is exactly how the OEM app does it too.

### How we use it
Two triggers send the `0x80` query; both replies flow through the normal
notification pipeline (below):

1. **When a Col04 reader opens** (device connect / host start) — `DpiStageMonitor`
   sends `0x80` right after it owns the read pipe, so the active stage becomes known
   even though the firmware never broadcasts it spontaneously.
2. **When a UI client subscribes** — `DeviceService` calls
   `DpiStageMonitor.RequestStateRefresh()`, which resets each reader's dedupe state
   and re-queries, so a freshly-opened app immediately gets the real stage pushed to
   it (instead of the UI default).

### Fallback
If the query gets no reply (e.g. a unit that doesn't answer, or the command
collection can't be opened), the UI simply keeps its default highlight of **stage 0
(`200`)**, and self-corrects the instant the user presses the physical DPI button.
So `200` remains the graceful fallback, not the normal case.

> Why the firmware needs poking: the Col04 notification is **change-only** — it fires
> on a DPI-button press but never sends a "here's my current state" message on
> connect. The `0x80` query is what turns "notify on change" into "tell me now."

## Architecture: DpiStageMonitor lives in the DeviceHost

`src/ProoveGaming.DeviceHost/Services/DpiStageMonitor.cs` — a `BackgroundService`.

Why in DeviceHost and not the App:
- **Always-on.** The host runs continuously (can be installed as a Windows service
  `ProoveGamingDeviceHost`), so notifications aren't missed while the UI is closed.
- **Single owner.** Col04 is single-reader; centralising ownership in one process
  avoids fighting over the handle.
- **Future-proof.** The same reader will carry **button-click statistics** later
  (planned), which also need an always-on collector.
- Reading Col04 uses plain `ReadFile` — **no message window / Raw Input needed** —
  so it doesn't have to live in the GUI process. (The old `RawInputDpiListener` in
  the App was deleted.)

### Multi-device correctness
The monitor **enumerates every `&mi_01&col04` interface** (via SetupDi, since
`HidSharp` won't list Col04) and runs **one reader thread per physical device**,
keyed by device interface path. Two identical mice are read independently, and each
`StageEvent` carries its own `DevicePath`. Readers for unplugged devices exit on
their own (`ReadFile` fails) and are restarted by the 3-second refresh loop if the
device reappears.

```
StageEvent(int VendorId, int ProductId, string DevicePath, int Stage)
```

Report parse: `if (read >= 4 && buf[0] == 0x05) stage = buf[3];` — deduplicated
against `LastStage` so we only raise on real changes.

### Triggering the current-state broadcast
Because the firmware only notifies on change, the monitor also **sends the `0x80`
query** (see the top of this doc) to make the device report where it is *now*:
- `DpiStageMonitor.QueryDeviceState(vid, pid, delayMs)` — fire-and-forget; delegates
  to `IMouseRgbService.RequestCurrentStateAsync()` (which owns the command
  collection) rather than opening a second handle. A small delay lets the Col04 read
  loop be pumping before the reply lands.
- Called automatically when a reader opens, and by `RequestStateRefresh()` (which
  also resets `LastStage = -1` so an unchanged stage is still re-raised) when a UI
  client subscribes.

## Flow to the UI (gRPC push)

```
[client subscribes] -> DeviceService.RequestStateRefresh -> 0x80 query -\
[physical DPI press] -------------------------------------------------\  \
                                                                       v  v
[mouse] --Col04 0x05 notify--> DpiStageMonitor.StageChanged
     -> DeviceService (SubscribeOnDevices stream) writes SubscribeOnDevicesResponse{ DpiStageChange }
         -> App UsbHidDeviceService.ReadServerResponses raises DpiStageChanged event
             -> DeviceDetailsViewModel.OnDpiStageChanged (filters by vid/pid)
                 -> Dispatcher.UIThread.Post(HighlightActiveDpiStage(stageIndex))
```

Files involved:
- Proto: `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` —
  `SubscribeOnDevicesResponse.dpiStageChange` (field 5) + message
  `DpiStageChange { vendor_id, product_id, device_path, stage_index }`.
- Host: `DeviceService.SubscribeOnDevices` subscribes to
  `DpiStageMonitor.StageChanged` (and unsubscribes in `finally`), writing responses
  under a `SemaphoreSlim` (`_streamWriteLock`) so the stream isn't written
  concurrently.
- App: `UsbHidDeviceService.ReadServerResponses` routes `DpiStageChange` to the
  `DpiStageChanged` event; other responses go to `DeviceFound`.
- App VM: `DeviceDetailsViewModel` holds display-only `DpiPresets` (7 stages) and
  highlights the matching one on the UI thread.

## The ladder is user-defined (2026-09-13)

The seven-stage ladder this document refers to throughout (and the "display-only `DpiPresets`
(7 stages)" note above) is now only a **starting point**, not an invariant. Each stage's
DPI value is editable in the App's DPI section, and stages can be added or removed
between 1 and 8 (`X43DpiTable` writes the count into block 0 byte 4, and unused slots keep
`UnusedStageDpi` rather than zero). Values are bounded by the catalog's
`DpiMin`/`DpiMax`/`DpiStep` (200 / 12000 / 50 for this device) and are persisted per preset.

The vendor never filled in the "Minimum DPI / Highest DPI" fields of
`X43 Standard Mouse Function Specifications 3311.xlsx`, so those bounds are ours,
conservatively chosen — and whether the 3327-class sensor actually resolves 50-DPI
increments (rather than snapping to 100) is **unverified on hardware**. If it snaps, the fix
is the catalog's `DpiStep`, not code. See `docs/plans/add-custom-dpi-values/`.

## Setting the active stage from the app (separate from reading)

Reading the stage and *setting* it are different problems. To change the active DPI
stage from software **do not rewrite param_0** (that also rewrites the LED-effect
byte and kills the LED — see [led.md](led.md)). The block-scoped writes in
`EasenMouseService` keep DPI and LED independent. The physical DPI button remains
the primary way the *user* changes stages, exactly as the OEM designed it (the OEM
UI doesn't even expose direct stage selection).

## Report rate (polling rate)

The report rate rides the **same `0x05` vendor report** as the DPI stage — at **byte
offset 2**, raw-ms encoded exactly like param_0 byte 0:

| offset-2 byte | polling rate |
|---------------|--------------|
| `0x01` | 1000 Hz |
| `0x02` | 500 Hz |
| `0x04` | 250 Hz |
| `0x08` | 125 Hz |

`DpiStageMonitor` parses offset 2, maps it to Hz (`RawRateToHz`), de-duplicates
against `LastRateHz` *independently* of the DPI stage (so a rate-only change still
raises), and fires a separate `ReportRateChanged` event. `RequestStateRefresh()`
resets `LastRateHz = -1` too, so a fresh subscriber gets the current rate re-raised.

> ### ⚠️ The polling-rate button does NOT broadcast — we must poll
> Unlike the DPI button (which pushes a `0x05` report the instant it's pressed), the
> **physical polling-rate button does not emit a notification on change** (confirmed
> from the DeviceHost log: every `[RATE]` line is preceded by a `0x80` query; there is
> never a spontaneous one). So a rate change made on the device is invisible until
> *something* triggers a `0x05` — an app resubscribe, or a DPI-button press.
> To get near-real-time reflection, `DpiStageMonitor` **polls `0x80` every 2 s** for
> each connected device. The reply is de-duplicated, so the poll is silent unless the
> rate (or stage) actually changed. The per-poll `0x80` log line is at Debug to avoid
> flooding. Verified raw→Hz encoding from real button presses: `0x01→1000, 0x02→500,
> 0x04→250, 0x08→125` — and note **the button cycles the rate *down* then wraps**
> (500→250→125→1000), which is the device's own behaviour, not a bug.

Pipeline mirrors the DPI one:
- Proto: `SubscribeOnDevicesResponse.reportRateChange` (field 6) + message
  `ReportRateChange { vendor_id, product_id, device_path, hz }`.
- Host: `DeviceService` subscribes to `DpiStageMonitor.ReportRateChanged` and pushes
  the message under the same `_streamWriteLock`.
- App: `UsbHidDeviceService` routes `ReportRateChange` to a `ReportRateChanged` event
  and caches it (`TryGetLastReportRate`); `DeviceDetailsViewModel.OnReportRateChanged`
  and `ApplyKnownDeviceState` (on device open) call **`HighlightPollingRate(hz)`**.

> ### ⚠️ The polling-rate UI writes to the device — the highlight must not
> Unlike `DpiPresets` (display-only), the `PollingRateOptions` are user-clickable and
> `SelectPollingRate` **writes** the chosen rate via `SetReportRateAsync`. The
> hardware-driven reflection therefore uses a **separate write-free**
> `HighlightPollingRate` that only sets `IsSelected`. If it reused the click path, a
> device-reported rate would echo straight back to the device. Keep these two paths
> distinct.

> ### ⚠️ Report-rate writes go through param_0 — keep the cache fresh
> `SetReportRateAsync` writes **param_0** (cmd `0x02`) — the same 64-byte block that
> holds the **DPI stage (byte 1)** and **LED effect (bytes 2–5)**. This is what VS750
> does too (the capture shows 9 param_0 writes and **no** dedicated `0x01` command),
> and it's safe *only if the block written is complete and current*. Feature reads
> return zeros, so we can't read param_0 back to refresh it. Instead,
> `DpiStageMonitor` feeds every `0x05` report into `EasenMouseService.SyncStateFromDevice`,
> aligning the cached param_0 bytes `[0]`=rate `[1]`=stage `[2]`=ledEffect with reality.
> Without this, a UI report-rate change after the user pressed the physical DPI button
> would **reset the DPI stage** (and possibly the LED) to a stale cached value. This is
> the mirror image of the old "DPI change kills the LED" bug — same root cause (param_0
> couples several settings), same discipline required.

> **Unverified encoding detail:** the offset-2 raw→Hz mapping is assumed identical to
> param_0 byte 0 (confirmed for writes). If a physical polling-button press ever
> highlights the wrong rate, check the logged `[RATE] … raw=0xNN hz=NNNN` line in the
> DeviceHost log against the actual device rate and adjust `RawRateToHz`.
