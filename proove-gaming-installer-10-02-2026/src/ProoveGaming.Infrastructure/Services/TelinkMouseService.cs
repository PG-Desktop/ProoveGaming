using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

// TAIPAN (Telink) 33-byte HID-report protocol. Bound to one physical mouse. Implements button
// remapping + macros (0x03 / 0x05), the DPI stage table (0x02) and the MsConfig settings block
// (0x01 — polling rate, active DPI stage); RGB and the mouse-param settings return false until
// their phase lands. The byte-level encoders (TaipanFrame, TaipanButtonMap, TaipanMacroEncoder,
// TaipanDpiTable, TaipanMsConfig) are pure and unit-tested — this class is just the I/O around
// them, so it carries no unit tests for the same reason EasenMouseService has none.
//
// Writes work on the CABLE and over the 2.4G DONGLE. The dongle was believed impossible until
// 2026-09-02: it answered every config frame with silence, which was recorded as a transport
// limitation. The vendor's own Bus Hound capture showed the missing piece — their driver brackets
// every write with an enter/exit configuration-mode handshake, commits in one frame instead of two,
// and follows each table with an apply command. With that framing (TaipanConfigSession) the dongle
// acknowledges every frame and the write takes effect; the cable had simply always tolerated its
// absence. READS (0x42/0x43) still go unanswered on both transports.
//
// Config writes go out as 33-byte HID OUTPUT reports on the vendor interface (report id 0x05).
// Confirmed by hardware enumeration: the config collection reports in=33/out=33 and NO feature
// report (feat=0 on the wired unit), so this is an output-report protocol, not a feature one.
public class TelinkMouseService : IMouseRgbService
{
	private readonly HidDevice _device;
	private readonly ILogger? _logger;
	// Stable identity of the physical unit this service drives (Container ID; "" = single-device).
	private readonly string _deviceKey;

	// Serialises device writes so concurrent gRPC calls don't interleave on the wire.
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	// Non-null while ApplyConfigurationAsync holds the device: its open stream, the gate, the write
	// lock and ONE configuration-mode session. Every inner write joins that session instead of
	// opening its own.
	//
	// This is what the user sees. Entering configuration mode suppresses the mouse's own reporting,
	// so each session is a visible freeze — about a second on the 2.4G dongle. One session per table
	// meant a DPI change (stage table + MsConfig) froze the mouse TWICE with a pause between, which
	// the vendor's own software does not do: their capture enters config mode once, writes every
	// table, and exits once. This makes ours behave the same way.
	//
	// ASYNCLOCAL, NOT A PLAIN FIELD — and that is load-bearing.
	//
	// DeviceRgbServiceFactory caches one TelinkMouseService per (VID, PID, deviceKey) in a
	// ConcurrentDictionary, so a single instance serves concurrent gRPC calls. A plain field would
	// make "I am already inside a session, skip the lock" true for EVERY caller rather than only the
	// nested one: while an apply held _writeLock with the field set, a concurrent SetDpiStagesAsync
	// would see it, skip the gate and the lock, and write its frames onto the apply's stream —
	// interleaving two sequenced flash transactions. Because the 0xD1 packet erases the sector, a
	// broken sequence can leave the table blank, which on BLAZE meant unusable buttons.
	//
	// AsyncLocal flows down the logical call stack that set it and nowhere else, so the bypass is
	// visible to the inner writes of one apply and invisible to any other request, which takes the
	// gate and lock normally. _writeLock stays the only thing serialising unrelated callers.
	private readonly AsyncLocal<HidStream?> _sharedStream = new();

	// Nesting depth of the configuration-mode session, per logical call stack for the same reason:
	// only the outermost scope sends the enter/exit frames.
	private readonly AsyncLocal<int> _sessionDepth = new();

	// Entries are written in 24-byte packets (6 whole 4-byte entries) to stay entry-aligned,
	// matching the vendor's worked example. 24 ≤ TaipanFrame.MaxPayload (25).
	private const int EntryPacketBytes = 24;

	// Pause between packets. USB delivers to the MCU instantly, but the 2.4G dongle relays each
	// report over the air and silently drops back-to-back packets — without pacing the commit
	// (0xD3) fires on an incomplete write, so a dongle-connected remap has no effect. A button
	// write is a rare user action (not a hot path), so this pacing is applied on every transport.
	private const int PacketDelayMs = 12;

	// Longest we will wait for another writer to release this device's gate. The interface methods
	// take no CancellationToken, so without this an unhealthy holder — e.g. a driver call wedged
	// inside the gate — would turn every subsequent config write into a permanent hang with no way
	// out but restarting the service. Generous next to the ~100 ms a real flash transaction takes.
	private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

	// Read-reply window. A cable reply lands almost immediately; the dongle never answers a query
	// at all, so this is the point at which a read gives up and the caller no-ops.
	private const int ReadTimeoutMs = 200;
	private const int ReadWindowMs = 1500;

	// The config-mode acknowledgement arrives within a few milliseconds when it arrives at all, so
	// it gets its own short budget rather than the 1.5 s table-read window. This matters: the
	// handshake is retried up to five times, and at the read window it would add 7.5 s to EVERY
	// write on a device that never acknowledges — turning a dongle improvement into a cable hang.
	private const int ConfigAckWindowMs = 250;

	// Pause after a MsConfig commit so a follow-up change doesn't read back a mid-erase sector.
	private const int CommitSettleMs = 150;

	// A MsConfig read can come back as an erased sector — our own writes leave flash blank until
	// the device re-flushes it on its own schedule, so the FIRST read of a session often lands in
	// that window. Retry briefly before giving up rather than skipping the user's change.
	//
	// 2026-07-30: widening this (8x250ms) was tried as a fix for "TAIPAN preset apply does nothing
	// until the DPI button is pressed" and did NOT help — confirming the failure is not read-timing
	// (a slow-to-answer device), so it was reverted back to the original small budget rather than
	// pay UI-perceptible latency for no benefit. See docs/mouse/taipan/protocol.md Open item 7 and
	// [[taipan-cold-start-msconfig]] for the ongoing investigation.
	private const int ReadRetries = 3;
	private const int ReadRetryDelayMs = 120;

	// Last MsConfig block successfully committed, used when a read comes back implausible (see
	// PatchMsConfigAsync). Only touched under _writeLock.
	private byte[]? _lastMsConfig;

	// The device's REAL active DPI stage, or TaipanMsConfig.UnknownStage when the host has not been
	// told one yet. MsConfig's own copy lags reality (the firmware flushes that byte on its own
	// schedule), so a read-modify-write must not trust the block for this one field.
	//
	// ONLY TWO WRITERS, both authoritative: SyncStateFromDevice (the device's 0x49 push) and
	// SetActiveDpiStageAsync (a stage we set ourselves). An inferred value must never land here —
	// it flows straight into the next MsConfig write and moves the user's DPI.
	//
	// volatile int, not byte?: this is written from the state monitor's reader thread and read on
	// the write path, and a two-field Nullable<byte> is not written atomically — a torn read could
	// pair HasValue=true with a stale stage.
	private volatile int _liveDpiStage = TaipanMsConfig.UnknownStage;

	public TelinkMouseService(HidDevice device, ILogger? logger = null, string deviceKey = "")
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_logger = logger;
		_deviceKey = deviceKey ?? string.Empty;
	}

	// Rebuilds the whole button table from the factory baseline with the requested bindings
	// overlaid, then writes it as the mandatory packet sequence: 0xD1 header (erases the sector
	// and sets the max-event count) → 0xD2 entry packets → 0xD2 ValidBankID at 0x007F → 0xD3
	// commit. Because 0xD1 erases the sector, the full table (wheel pair and Buttons 6/7 included)
	// is always re-sent. Callers persist the bindings themselves.
	public Task<bool> SetButtonBindingsAsync(IReadOnlyList<ButtonBinding> bindings) =>
		RunWriteAsync("Buttons", stream => SetButtonBindingsCoreAsync(stream, bindings));

	// The macro upload must land in the same session as the button entries that point at its slots,
	// which is why both live in this one core rather than two calls.
	private Task<bool> SetButtonBindingsCoreAsync(HidStream stream, IReadOnlyList<ButtonBinding> bindings) =>
		InConfigSessionAsync(stream, async () =>
		{
			var table = TaipanButtonMap.BuildTable(bindings);

			// Macros first: upload every macro-bound binding to a device slot, then point the
			// button's table entry at it — a binding must never reference a slot that wasn't
			// just written, because the macro write's 0xD1 packet erases the whole sector.
			await UploadMacrosAsync(stream, bindings, table);

			byte cmd = TaipanButtonMap.ButtonCommand;

			// 1) Header (max-event count) — first packet, erases the flash sector.
			await WriteFrameAsync(stream, TaipanFrame.Build(
				cmd, 0x00, 0x00, TaipanButtonMap.HeaderLength, TaipanFrame.SeqFirst,
				table.AsSpan(0, TaipanButtonMap.HeaderLength)));

			// 2) Entry region (table offset 0x0002 onward) in entry-aligned chunks.
			int entriesLength = table.Length - TaipanButtonMap.HeaderLength;
			for (int written = 0; written < entriesLength; written += EntryPacketBytes)
			{
				int len = Math.Min(EntryPacketBytes, entriesLength - written);
				int tableOffset = TaipanButtonMap.HeaderLength + written;
				await WriteFrameAsync(stream, TaipanFrame.Build(
					cmd, (byte)(tableOffset & 0xFF), (byte)(tableOffset >> 8), (byte)len, TaipanFrame.SeqContinue,
					table.AsSpan(tableOffset, len)));
			}

			// 3) Combined commit, then apply.
			await CommitAndApplyAsync(stream, cmd, TaipanButtonMap.ValidBankIdOffset);

			_logger?.LogInformation("[Taipan][Buttons] button map written for unit={Key} ({N} bindings)",
				_deviceKey, bindings?.Count ?? 0);
			return true;
		});

	// Acquires the device and runs `body` on it — unless ApplyConfigurationAsync already holds the
	// device, in which case `body` reuses that stream and takes no lock. Entering configuration mode
	// is NOT done here: each core decides what belongs inside it, because the MsConfig path
	// deliberately keeps its read outside. Nesting is handled by InConfigSessionAsync.
	//
	// Ordering, when this does acquire: gate first, then _writeLock, the same order at every site so
	// the two can never be taken in opposite orders and deadlock. _writeLock serialises this service
	// instance; the gate serialises against OTHER writers on the same interface — notably
	// TaipanStateMonitor's battery request, which would otherwise land inside the sequenced flash
	// transaction. The acquisition is bounded (see TryAcquireDeviceGateAsync).
	private async Task<bool> RunWriteAsync(string tag, Func<HidStream, Task<bool>> body)
	{
		// Already inside an outer session: reuse its stream, take no lock, open no second session.
		if (_sharedStream.Value is { } shared)
		{
			try
			{
				return await body(shared);
			}
			catch (Exception ex)
			{
				_logger?.LogWarning("[Taipan][{Tag}] failed: {Msg}", tag, ex.Message);
				return false;
			}
		}

		using var deviceGate = await TryAcquireDeviceGateAsync();
		if (deviceGate is null) return false;

		await _writeLock.WaitAsync();
		try
		{
			if (!_device.TryOpen(out var stream))
			{
				_logger?.LogWarning("[Taipan][{Tag}] HidSharp TryOpen failed for unit={Key}", tag, _deviceKey);
				return false;
			}

			using (stream)
				return await body(stream);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[Taipan][{Tag}] failed: {Msg}", tag, ex.Message);
			return false;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	// Uploads every macro-bound binding into a device macro slot (slots allocated sequentially
	// per write, like EasenMouseService), then overlays the 0x91 bind entry into the button
	// table. The whole used region is written as ONE 0x05 sequence — 0xD1 first chunk (erases
	// the sector) → 0xD2 chunks → ValidBankID at 0x0FFF → 0xD3 commit — so every referenced
	// slot survives the erase. No-op when nothing is macro-bound.
	private async Task UploadMacrosAsync(HidStream stream, IReadOnlyList<ButtonBinding> bindings, byte[] table)
	{
		if (bindings == null) return;

		var macroTable = new byte[TaipanMacroEncoder.MaxSlots * TaipanMacroEncoder.SlotLength];
		int slot = 0;

		foreach (var b in bindings)
		{
			if (b.Action != MouseButtonAction.Macro || b.Macro == null) continue;

			int index = TaipanButtonMap.ButtonToIndex(b.Button);
			if (index < 1 || slot >= TaipanMacroEncoder.MaxSlots) continue;

			var slotImage = TaipanMacroEncoder.Encode(b.Macro);
			Buffer.BlockCopy(slotImage, 0, macroTable, slot * TaipanMacroEncoder.SlotLength, TaipanMacroEncoder.SlotLength);

			var entry = TaipanButtonMap.EncodeMacroEntry(slot, b.Macro.Mode, b.Macro.RepeatCount);
			Buffer.BlockCopy(entry, 0, table, TaipanButtonMap.EntryOffset(index), TaipanButtonMap.EntryLength);
			slot++;
		}

		if (slot == 0) return;

		byte cmd = TaipanButtonMap.MacroCommand;
		int used = slot * TaipanMacroEncoder.SlotLength;
		for (int off = 0; off < used; off += TaipanFrame.MaxPayload)
		{
			int len = Math.Min(TaipanFrame.MaxPayload, used - off);
			byte seq = off == 0 ? TaipanFrame.SeqFirst : TaipanFrame.SeqContinue;
			await WriteFrameAsync(stream, TaipanFrame.Build(
				cmd, (byte)(off & 0xFF), (byte)(off >> 8), (byte)len, seq,
				macroTable.AsSpan(off, len)));
		}

		await CommitAndApplyAsync(stream, cmd, TaipanButtonMap.MacroValidBankIdOffset);

		_logger?.LogInformation("[Taipan][Macro] {N} macro slot(s) uploaded for unit={Key}", slot, _deviceKey);
	}

	// Writes one output report, then paces so the 2.4G dongle doesn't drop the next packet.
	// Acquires this device's gate, or gives up after GateTimeout and returns null. Callers treat
	// null as "the write did not happen" and return their own failure result, which is the same
	// thing they already do when TryOpen fails — a bounded failure the caller can report, rather
	// than an unbounded wait nobody can observe.
	private async Task<IDisposable?> TryAcquireDeviceGateAsync()
	{
		using var timeout = new CancellationTokenSource(GateTimeout);
		try
		{
			return await DeviceGate.AcquireAsync(_device.DevicePath, timeout.Token);
		}
		catch (OperationCanceledException)
		{
			_logger?.LogWarning("[Taipan] device gate busy for >{Sec}s on unit={Key}; skipping this write",
				GateTimeout.TotalSeconds, _deviceKey);
			return null;
		}
	}

	private static async Task WriteFrameAsync(HidStream stream, byte[] frame)
	{
		stream.Write(frame);
		await Task.Delay(PacketDelayMs);
	}

	// Writes the full DPI stage table (command 0x02): 0xD1 first chunk (erases the sector) → 0xD2
	// chunks → ValidBankID at 0x007F → 0xD3 commit. Per-stage indicator colours on DpiStageParams
	// are ignored — they live in the separate 0x0C table and are display-only, as on BLAZE.
	public Task<bool> SetDpiStagesAsync(DpiStageParams[] stages)
	{
		if (stages == null || stages.Length == 0) return Task.FromResult(false);

		return RunWriteAsync("DPI", stream => InConfigSessionAsync(stream, async () =>
		{
			var table = TaipanDpiTable.BuildTable(stages);
			await WriteTableAsync(stream, TaipanDpiTable.DpiCommand, table, TaipanButtonMap.ValidBankIdOffset);
			_logger?.LogInformation("[Taipan][DPI] {N} stage(s) written for unit={Key}", stages.Length, _deviceKey);
			return true;
		}));
	}

	// Both of these live in the MsConfig block, which the 0xD1 write erases — so each is a
	// read-modify-write of the whole block and never fabricates one.
	public async Task<bool> SetActiveDpiStageAsync(byte stageIndex)
	{
		var ok = await PatchMsConfigAsync(
			block => TaipanMsConfig.PatchActiveDpiStage(block, stageIndex),
			"active DPI stage", stageIndex, patchSetsStage: true);

		// The device only pushes 0x49 for PHYSICAL button presses, so a stage we set ourselves must
		// update the live cache here — otherwise the next MsConfig write (e.g. a polling change)
		// would restore the previous stage and undo this one.
		if (ok) _liveDpiStage = stageIndex;
		return ok;
	}

	public Task<bool> SetReportRateAsync(int hz) =>
		PatchMsConfigAsync(
			block => TaipanMsConfig.PatchPollingRate(block, hz),
			"polling rate", hz, patchSetsStage: false);

	// Reads MsConfig, applies `patch`, writes the whole block back.
	//
	// The read is the fragile half. A read issued soon after a previous commit comes back as the
	// erased/half-committed sector (all 0x00 / 0xFF), and patching THAT would reset every setting
	// at once — the DPI stage to an extreme and the rate to its default. So an implausible read
	// falls back to the last block we successfully committed, mirroring EasenMouseService's
	// `TryReadBlock(...) ?? _lastBlock0`.
	//
	// When there is NEITHER a good read NOR a cached block — the very first MsConfig write of a
	// session over the 2.4G dongle, which never answers reads at all — this used to just skip the
	// write entirely, silently. That is exactly what "TAIPAN doesn't apply anything until the DPI
	// button is pressed" turned out to be: every cold-start apply hit this branch and no-op'd, and
	// the only thing a physical button press actually did was let the NEXT apply (after the user
	// gave up waiting and touched the mouse) succeed by coincidence, not because anything was
	// actually unblocked. So this now falls back one step further, to the vendor's own worked
	// full-write example (TaipanMsConfig.GetFactoryDefaultBlock()) — a real write beats no write,
	// even though every field this session hasn't explicitly set lands on the vendor's example
	// value rather than whatever the user actually had.
	//
	// `patchSetsStage` gates that fallback specifically for the DPI-stage byte: the factory-default
	// block carries the vendor's own example stage (0x00), which has nothing to do with this device's
	// real one. ResolveStageForWrite below leaves that byte untouched when _liveDpiStage is
	// unknown, so unless `patch` itself is about to overwrite the stage (patchSetsStage: true) or
	// the live stage IS known, falling back would silently commit a fabricated stage to the
	// device — the exact "800 DPI + polling change ⇒ max DPI" class of bug the rest of this file's
	// _liveDpiStage machinery exists to prevent. In that case the write is skipped instead, same as
	// the pre-fallback behaviour.
	private Task<bool> PatchMsConfigAsync(Action<byte[]> patch, string what, int value, bool patchSetsStage) =>
		RunWriteAsync("Config", async stream =>
		{
			{
				var read = await ReadMsConfigAsync(stream);
				var source = TaipanMsConfig.ResolveSourceBlock(read, _lastMsConfig);

				if (source == null)
				{
					if (!TaipanMsConfig.IsFactoryDefaultFallbackSafe(patchSetsStage, _liveDpiStage))
					{
						_logger?.LogWarning(
							"[Taipan][Config] no usable MsConfig read, no cached block, and no known DPI stage " +
							"for unit={Key} — skipping {What} write (the factory-default fallback would leave " +
							"the vendor's own example stage in place and silently move the device's real DPI)",
							_deviceKey, what);
						return false;
					}

					_logger?.LogWarning(
						"[Taipan][Config] no usable MsConfig read and no cached block for unit={Key} — " +
						"falling back to the vendor factory-default block for {What} write (every other " +
						"field in this write lands on the vendor's example value, not the device's real one)",
						_deviceKey, what);
					source = TaipanMsConfig.GetFactoryDefaultBlock();
				}
				// Seed the cache from the read itself, not just after a successful write, so a
				// failed write still leaves us able to serve the next change.
				else if (ReferenceEquals(source, read)) _lastMsConfig ??= read;
				else _logger?.LogWarning("[Taipan][Config] unusable MsConfig read — using last committed block for unit={Key}", _deviceKey);

				var block = (byte[])source.Clone();

				// MsConfig's stored DPI-stage byte lags the live one — confirmed on hardware: with
				// the mouse on stage 0 (800 DPI) the block still read stage 6. Writing the block
				// back verbatim therefore SNAPPED the mouse to that stale stage (the "polling change
				// jumps DPI to maximum" bug), so a stage we KNOW replaces it.
				var knownStage = _liveDpiStage;
				if (knownStage == TaipanMsConfig.UnknownStage)
				{
					// Nothing authoritative yet — no 0x49 push and no stage of our own. The byte is
					// left as read rather than replaced with a guess; the app asserts its persisted
					// stage on attach, so this should be rare and is worth seeing in the log.
					_logger?.LogWarning("[Taipan][Config] no known DPI stage — leaving 0x{Offset:X2} as read for {What} write on unit={Key}",
						TaipanMsConfig.DpiSwitchOffset, what, _deviceKey);
				}
				TaipanMsConfig.ResolveStageForWrite(block, knownStage);

				patch(block);
				// Only the WRITE goes inside configuration mode. The read above works on the cable
				// today and its behaviour in config mode is untested, so it is deliberately left
				// outside rather than changed on speculation. Nested inside an outer
				// ApplyConfigurationAsync session this scope is a no-op — see InConfigSessionAsync.
				await InConfigSessionAsync(stream, async () =>
				{
					await WriteTableAsync(stream, TaipanMsConfig.ConfigCommand, block, bankIdOffset: null);
					return true;
				});
				_lastMsConfig = block;

				// Let the flash commit settle so a follow-up change doesn't read a mid-erase sector.
				await Task.Delay(CommitSettleMs);

				_logger?.LogInformation("[Taipan][Config] {What}={Value} written for unit={Key}", what, value, _deviceKey);
			}
			return true;
		});

	// A BOOTSTRAP GUESS at the current stage, for a first-run display only — see
	// TaipanMsConfig.LiveDpiStage for why it cannot be trusted. Cable only; the dongle answers no
	// queries, so this returns null there and the display waits for the first button press.
	//
	// Deliberately does NOT populate _liveDpiStage. Letting this value into that cache is the
	// defect this method is named to warn about: it flows into the next MsConfig write and moves
	// the user's DPI. Callers may show it; only the app's persisted stage or a real 0x49 push may
	// become the known stage.
	public async Task<int?> TryInferActiveDpiStageAsync()
	{
		// Null here means "no stage inferred", which is exactly what a skipped read should report —
		// the same result this method already returns when the device answers nothing.
		using var deviceGate = await TryAcquireDeviceGateAsync(); // see SetButtonBindingsAsync
		if (deviceGate is null) return null;

		await _writeLock.WaitAsync();
		try
		{
			if (!_device.TryOpen(out var stream)) return null;

			using (stream)
			{
				// Single attempt, not the usual ReadRetries: this is a display-only bootstrap guess
				// (see the doc comment above) fired once per App connect by
				// TaipanStateMonitor.RequestStateRefresh, typically right at app startup before any
				// physical button press has "woken" the mouse. On a cold/unresponsive mouse each
				// retry attempt holds _writeLock for up to ~2s (the hard read-timeout race in
				// ReadTableAsync), and the App's own real preset-apply writes — e.g.
				// EnsureDefaultPresetAppliedAsync, which fires as soon as Device Details opens — queue
				// behind whatever is holding that lock. A wrong guess here is harmless (it
				// self-corrects on the first real push or user action); the App's actual apply queuing
				// for 3x that long behind a low-stakes guess is what looked like "TAIPAN doesn't apply
				// anything until the DPI button is clicked" — the click's input report is what was
				// unblocking the stuck read and releasing the lock.
				var block = await ReadMsConfigAsync(stream, retries: 1);
				if (block == null) return null;

				// The wrap needs the DPI table's own stage count — MsConfig has no such field.
				var header = await ReadTableAsync(stream, TaipanDpiTable.ReadCommand, 0, 1);
				var stageCount = header is { Length: > 0 } ? header[0] : 0;

				var stage = TaipanMsConfig.LiveDpiStage(block, stageCount);
				_lastMsConfig ??= block;

				_logger?.LogInformation("[Taipan][DPI] stored stage={Stored} => inferred stage={Live} (of {Count}) for unit={Key}",
					TaipanMsConfig.ActiveDpiStage(block), stage, stageCount, _deviceKey);
				return stage;
			}
		}
		catch (Exception ex)
		{
			_logger?.LogDebug(ex, "[Taipan][DPI] active-stage read failed for unit={Key}", _deviceKey);
			return null;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	// Reads MsConfig, retrying while the block comes back unusable (no reply, or an erased sector).
	// The first read of a session commonly lands in the erased window: our own writes leave flash
	// blank until the device re-flushes it, so a cold start with no cached block would otherwise
	// skip the user's very first change and appear to do nothing. Returns null only when every
	// attempt failed. `retries` defaults to the full budget for real reads (PatchMsConfigAsync,
	// where giving up early risks writing back a bad block); TryInferActiveDpiStageAsync passes 1
	// — see its own comment for why a low-stakes display guess shouldn't hold _writeLock that long.
	private async Task<byte[]?> ReadMsConfigAsync(HidStream stream, int retries = ReadRetries)
	{
		for (var attempt = 0; attempt < retries; attempt++)
		{
			if (attempt > 0) await Task.Delay(ReadRetryDelayMs);

			var read = await ReadTableAsync(stream, TaipanMsConfig.ReadCommand, 0, TaipanMsConfig.Length);
			if (read != null && TaipanMsConfig.IsPlausible(read)) return read;

			_logger?.LogDebug("[Taipan][Config] MsConfig read attempt {Attempt}/{Total} {Reason} for unit={Key}",
				attempt + 1, retries,
				read == null ? "returned nothing" : "looked like an erased sector",
				_deviceKey);
		}

		return null;
	}

	// Sends a 0x4X read request and returns `length` bytes of the reply's FIFO payload, or null
	// when no matching reply arrives. Windows delivers HID input reports to every open handle, so
	// this read coexists with TaipanStateMonitor's listener — the monitor ignores non-0x48/0x49
	// replies and this ignores everything but `readCmd`.
	private async Task<byte[]?> ReadTableAsync(HidStream stream, byte readCmd, int address, int length)
	{
		var result = new byte[length];
		var offset = 0;

		while (offset < length)
		{
			var chunk = Math.Min(TaipanFrame.MaxPayload, length - offset);
			var addr = address + offset;
			await WriteFrameAsync(stream, TaipanFrame.Build(
				readCmd, (byte)(addr & 0xFF), (byte)(addr >> 8), (byte)chunk, 0x00, ReadOnlySpan<byte>.Empty));

			// ReadReply blocks synchronously on stream.Read, guarded by stream.ReadTimeout/ReadWindowMs —
			// but if the HID driver doesn't honour that timeout (seen: reads that never return), this
			// call never comes back, and since it runs while _writeLock is held, EVERY future TAIPAN
			// write (buttons, DPI table, MsConfig — the lock is shared by all of them) queues up behind
			// it forever. Race it against a hard outer deadline so a stuck read can never hold the lock
			// past ~1.75s: the abandoned Task.Run keeps blocking on the stream in the background, but
			// this call — and the lock — moves on regardless.
			byte[]? reply;
			try
			{
				reply = await Task.Run(() => ReadReply(stream, readCmd)).WaitAsync(TimeSpan.FromMilliseconds(ReadWindowMs + 250));
			}
			catch (TimeoutException)
			{
				reply = null;
			}
			if (reply == null) return null;

			reply.AsSpan(TaipanFrame.PayloadOffset, chunk).CopyTo(result.AsSpan(offset, chunk));
			offset += chunk;
		}

		return result;
	}

	// Runs a flash-table write inside a configuration-mode session.
	//
	// The exit frame is sent in a finally on EVERY path, exceptions included: Para1 = 0x00 suppresses
	// the mouse's own reporting, so a device left in configuration mode stops working as a mouse
	// until it is replugged. That is the one outcome this must never produce.
	//
	// A failed handshake is logged and the write proceeds anyway. The cable has always accepted a
	// write without it, so refusing to write would turn a dongle-only improvement into a cable
	// regression; over the dongle the write simply behaves as it did before this change.
	private async Task<bool> InConfigSessionAsync(HidStream stream, Func<Task<bool>> body)
	{
		// NESTABLE. Only the outermost call enters and leaves configuration mode, so an
		// ApplyConfigurationAsync that writes buttons, DPI and MsConfig produces ONE freeze rather
		// than one per table — and an inner scope (the MsConfig write, which deliberately keeps its
		// preceding READ outside config mode) costs nothing when it is already inside one.
		if (_sessionDepth.Value > 0)
		{
			_sessionDepth.Value++;
			try { return await body(); }
			finally { _sessionDepth.Value--; }
		}

		// The depth is raised and the handshake attempted INSIDE the try, so that a throw from
		// EnterConfigModeAsync still unwinds the depth and still attempts the exit frame.
		//
		// It can throw: WriteFrameAsync does an unguarded stream.Write, and ReadReply catches only
		// TimeoutException, so an IOException from a device unplugged mid-handshake escapes. With
		// the increment outside the try that left the depth stuck above zero for the LIFE OF THE
		// SERVICE — every later session nested to a no-op, config mode was never entered again, and
		// dongle writes silently stopped applying until the DeviceHost restarted. One knocked-out
		// cable would have reintroduced the very bug this session framing fixes.
		_sessionDepth.Value = 1;
		try
		{
			var entered = await EnterConfigModeAsync(stream);
			if (!entered)
				_logger?.LogWarning("[Taipan] config mode not acknowledged for unit={Key}; writing anyway", _deviceKey);

			return await body();
		}
		finally
		{
			_sessionDepth.Value = 0;
			try
			{
				// Attempted even when entering threw: a partially-entered session must still be left.
				await WriteFrameAsync(stream, TaipanConfigSession.ExitFrame());
			}
			catch (Exception ex)
			{
				// Nothing further can be done here, and the state is not persisted — a replug clears
				// it. Logged at warning because a mouse that stops responding is very visible.
				_logger?.LogWarning("[Taipan] failed to leave config mode for unit={Key}: {Msg}", _deviceKey, ex.Message);
			}
		}
	}

	// Asks the mouse to enter configuration mode, retrying until it acknowledges. The vendor's
	// driver needed five attempts; ours was acknowledged on the second.
	private async Task<bool> EnterConfigModeAsync(HidStream stream)
	{
		for (var attempt = 1; attempt <= TaipanConfigSession.EnterAttempts; attempt++)
		{
			await WriteFrameAsync(stream, TaipanConfigSession.EnterFrame());

			var reply = await Task.Run(() => ReadReply(stream, TaipanConfigSession.SetMouseStateAckCommand, ConfigAckWindowMs))
				.WaitAsync(TimeSpan.FromMilliseconds(ConfigAckWindowMs + 250));

			if (reply is not null && TaipanConfigSession.IsCommitted(reply))
				return true;

			await Task.Delay(TaipanConfigSession.EnterGapMs);
		}

		return false;
	}

	// Commit a written table and then apply it. `bankIdOffset` is null for MsConfig, the one table
	// that takes no ValidBankID.
	private async Task CommitAndApplyAsync(HidStream stream, byte cmd, int? bankIdOffset)
	{
		await WriteFrameAsync(stream, bankIdOffset is { } bank
			? TaipanConfigSession.CommitFrame(cmd, bank)
			: TaipanConfigSession.CommitFrameWithoutBankId(cmd));

		if (TaipanConfigSession.ApplyMaskFor(cmd) is { } mask)
			await WriteFrameAsync(stream, TaipanConfigSession.ApplyFrame(mask));
	}

	// Drains input reports until the reply echoing `expectedCmd` arrives, or the window elapses.
	private static byte[]? ReadReply(HidStream stream, byte expectedCmd) =>
		ReadReply(stream, expectedCmd, ReadWindowMs);

	private static byte[]? ReadReply(HidStream stream, byte expectedCmd, int windowMs)
	{
		stream.ReadTimeout = ReadTimeoutMs;
		var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
		var buffer = new byte[TaipanFrame.Length];

		while (DateTime.UtcNow < deadline)
		{
			int read;
			try { read = stream.Read(buffer); }
			catch (TimeoutException) { continue; }

			if (read >= TaipanFrame.PayloadOffset &&
				buffer[0] == TaipanFrame.ReportId &&
				buffer[2] == TaipanFrame.DriverId &&
				buffer[3] == expectedCmd)
			{
				return buffer;
			}
		}

		return null;
	}

	// Writes a whole table: 0xD1 first chunk (erases) → 0xD2 chunks → optional ValidBankID → 0xD3.
	//
	// `bankIdOffset` is null for tables that take NO ValidBankID packet — MsConfig is the one such
	// table, exactly as the vendor's D1 → D3 example shows.
	//
	// Confirmed on hardware (2026-07-20): sending a BankID packet for MsConfig writes 0x1A at offset
	// 0x007F, far outside the 19-byte block. The firmware rejects the transaction and leaves the
	// sector ERASED — a read straight after came back all-0xFF. The device keeps running from RAM
	// (so the new rate seems to apply) but then derives the DPI stage from blank flash, i.e. 0xFF
	// clamped to the maximum stage; a DPI-button press makes it re-flush RAM to flash and the
	// settings reappear. That is the "800 DPI + polling change ⇒ max DPI" bug.
	private async Task WriteTableAsync(HidStream stream, byte cmd, byte[] table, int? bankIdOffset)
	{
		for (var offset = 0; offset < table.Length; offset += TaipanFrame.MaxPayload)
		{
			var len = Math.Min(TaipanFrame.MaxPayload, table.Length - offset);
			var seq = offset == 0 ? TaipanFrame.SeqFirst : TaipanFrame.SeqContinue;
			await WriteFrameAsync(stream, TaipanFrame.Build(
				cmd, (byte)(offset & 0xFF), (byte)(offset >> 8), (byte)len, seq,
				table.AsSpan(offset, len)));
		}

		// One combined commit frame (ValidBankID carrying 0xD3), then the apply — the vendor's own
		// framing. The previous two-frame form (0xD2 ValidBankID, then an empty 0xD3) is what the
		// dongle never answered.
		await CommitAndApplyAsync(stream, cmd, bankIdOffset);
	}

	// Applies the full desired configuration. The DPI table and button map live in their own flash
	// sectors, so those stay separate writes; active stage, report rate and lift-off all live in the
	// SAME 19-byte MsConfig block, so those three are folded into ONE read-modify-write via
	// ApplyMsConfigAsync instead of three independent read-erase-write cycles — three separate writes
	// invite exactly the class of bug PatchMsConfigAsync's own comments warn about (a later write
	// undoing an earlier one before _liveDpiStage catches up). Still one gRPC round-trip from the App.
	// Null members are skipped; RGB is skipped (not implemented for TAIPAN).
	public async Task<bool> ApplyConfigurationAsync(DeviceConfiguration config)
	{
		byte? liftOff = config.Settings is { } settings ? settings.LiftOff : null;
		_logger?.LogInformation(
			"[Taipan][Apply] unit={Key} requested: buttons={B} dpiStages={D} activeStage={St} reportRate={R} liftOff={L} led={Led} (liveDpiStage={Live})",
			_deviceKey, config.Buttons?.Count ?? 0, config.DpiStages?.Length ?? 0,
			config.ActiveStage is { } s ? s.ToString() : "null",
			config.ReportRateHz is { } r ? r.ToString() : "null",
			liftOff is { } l ? l.ToString() : "null",
			config.Led is not null, _liveDpiStage);

		// ONE device acquisition and ONE configuration-mode session for every table this apply
		// touches. Entering config mode suppresses the mouse's own reporting, so each session is a
		// visible freeze — roughly a second over the 2.4G dongle. Doing it per table meant a DPI
		// change (stage table + MsConfig) froze the mouse twice with a pause between; the vendor's
		// own software enters once, writes every table and exits once, and now so does this.
		//
		// _sharedStream is what makes the inner calls join in: each sees the device already held and
		// reuses the stream without taking the gate or _writeLock (SemaphoreSlim is not re-entrant,
		// so trying would deadlock). Their own InConfigSessionAsync scopes nest to no-ops.
		return await RunWriteAsync("Apply", stream => InConfigSessionAsync(stream, async () =>
		{
			_sharedStream.Value = stream;
			try
			{
				var ok = true;
				if (config.Buttons is { Count: > 0 } bindings)
					ok &= await SetButtonBindingsAsync(bindings);
				if (config.DpiStages is { Length: > 0 } stages)
					ok &= await SetDpiStagesAsync(stages);

				if (config.ActiveStage is not null || config.ReportRateHz is not null || liftOff is not null)
					ok &= await ApplyMsConfigAsync(config.ActiveStage, config.ReportRateHz, liftOff);

				if (config.Led is { } led)
					ok &= await SetLedColorAsync(led.R, led.G, led.B, led.Effect, led.Brightness, led.Speed);

				_logger?.LogInformation("[Taipan][Apply] unit={Key} result: ok={Ok}", _deviceKey, ok);
				return ok;
			}
			finally
			{
				// Cleared before the session closes, so nothing can hand out a disposed stream.
				_sharedStream.Value = null;
			}
		}));
	}

	// Applies active stage, report rate and lift-off distance as ONE MsConfig read-modify-write —
	// all three live in the same flash block, so patching them together (instead of three sequential
	// PatchMsConfigAsync calls, one per field) avoids a later write in the sequence undoing an
	// earlier one while _liveDpiStage is still catching up. Any subset may be omitted (null).
	private async Task<bool> ApplyMsConfigAsync(byte? activeStage, int? reportRateHz, byte? liftOffMm)
	{
		var ok = await PatchMsConfigAsync(block =>
		{
			if (activeStage is { } stage) TaipanMsConfig.PatchActiveDpiStage(block, stage);
			if (reportRateHz is { } hz) TaipanMsConfig.PatchPollingRate(block, hz);
			if (liftOffMm is { } lift) TaipanMsConfig.PatchLiftOffDistance(block, lift);
		}, "coordinated config (stage/rate/lift-off)", activeStage ?? TaipanMsConfig.UnknownStage,
		patchSetsStage: activeStage.HasValue);

		// Same rule as SetActiveDpiStageAsync: a stage we set ourselves must update the live cache,
		// since the device only pushes 0x49 for physical button presses.
		if (ok && activeStage is { } applied) _liveDpiStage = applied;
		return ok;
	}

	// ── Not implemented for TAIPAN yet (arrive with their own phases) ──────────────────────────
	public Task<bool> SetLedColorAsync(byte r, byte g, byte b, byte effect, byte brightness, byte speed) =>
		NotImplemented(nameof(SetLedColorAsync));

	// The active stage and polling rate come from the app's persisted values plus the device's own
	// 0x49 state push (which the dongle relays), so there is nothing to query here.
	public Task<bool> RequestCurrentStateAsync() =>
		NotImplemented(nameof(RequestCurrentStateAsync));

	// Keeps the live DPI stage aligned with what the device reports on its 0x49 push, so a later
	// MsConfig write preserves the REAL stage instead of the stale one stored in the block. Same
	// role this plays for EasenMouseService's param_0 cache. One of only two authoritative writers
	// of _liveDpiStage — the push carries the stage the mouse is actually on.
	public void SyncStateFromDevice(byte reportRateRaw, byte dpiStage, byte ledEffect) =>
		_liveDpiStage = dpiStage;

	// The stage the host actually knows, or TaipanMsConfig.UnknownStage. Lets the state monitor
	// report a fact when it has one instead of falling back to the bootstrap guess.
	public int KnownDpiStage => _liveDpiStage;

	private Task<bool> NotImplemented(string member)
	{
		_logger?.LogDebug("[Taipan] {Member} not implemented for TAIPAN yet", member);
		return Task.FromResult(false);
	}
}
