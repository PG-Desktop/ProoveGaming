using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public record DpiStageParams(ushort DpiValue, byte R, byte G, byte B);

// Easen 9-byte SetFeatureReport protocol (Holtek HT68FB571 MCU — e.g. BLAZE/X43).
// Handles RGB LED control, hardware mouse settings (debounce, lift-off) and DPI stages.
// Bound to one physical device; open a HidSharp stream per call rather than holding it open,
// since the underlying HidDevice can disappear (unplug) between calls.
//
// Write strategy (settled 2026-07-02 after the flash-corruption incident, refined 2026-08-06):
//   EVERY change goes through ApplyConfigurationAsync — there are no per-aspect single-block
//   writes any more. One transaction sends param_0 and then all three 64-byte param_1 blocks
//   with COMPLETE content rebuilt from the caches, so changing the LED still ships the current
//   DPI table and key matrix. This is the shape the OEM software uses.
//
//   Partial-content all-blocks writes are what zeroed the key matrix and bricked the buttons;
//   single-block writes are what left the LED dark after a button remap. Full content in one
//   coordinated write avoids both.
//
//   Details that turned out to matter, all confirmed against OEM captures on hardware:
//     - param_0 is written BEFORE the param_1 blocks.
//     - Frames are paced (~22 ms between blocks, ~40 ms between phases and before the handle
//       closes); back-to-back writes applied only intermittently.
//     - NO 0x0D/0x80 LED drive is sent. The OEM sends neither and the sequence applies on its own;
//       sending the drive with no gap after param_0 is what made neon fail most often.
//     - The status bitmask names only the blocks that changed, as the OEM does (0x02 for an
//       LED-only change), plus block 1 whenever the key matrix moves.
//     - No DPI slot may hold zero — the firmware rejects the write and the LED goes down with it.
//       See X43DpiTable.UnusedStageDpi.
//
//   The CACHES are authoritative — the device is never read back. The per-aspect writers used to
//   read block 0 off the device before patching it (TryReadBlock, removed with them), so a settings
//   change layered onto whatever the device happened to hold. Now every write is composed from
//   _lastBlock0 / _lastParam0, which start at CreateDefaultBlock0 / CreateDefaultParam0. That is
//   deliberate: the App owns the configuration, its defaults match the UI's DpiPresets exactly, and
//   a preset apply always supplies the full DPI table. The trade-off is that a table set outside the
//   App (by the OEM software) is overwritten by the first write rather than preserved.
//
// The physical RGB switch (key-matrix slot 7) is left as the firmware's built-in LED-effect
// cycle [03,00,00,0A] and is NOT touched in software. Rebinding it as a vendor key and
// toggling the LED from its report was tried and abandoned — the switch is not
// software-hookable on this hardware (see docs/mouse/blaze/key-matrix.md).
//
// App effect byte -> (protocolEffect, colorSlot):
//   0x01 = Breathing        -> 0x02, slot=0  (custom colour, block-1 bytes 24-47)
//   0x00 = RainbowBreathing -> 0x02, slot=7  (sweeps the block-1 palette at bytes 24-47)
//   0x02 = Neon             -> 0x03, slot=0  (also sweeps the palette)
//   0x03 = ColorCycle       -> 0x04, slot=0  (sweeps the block-1 palette, like Neon)
//   0xED = Off              -> 0x02, slot=0, brightness=0 (persists across reconnect)
public class EasenMouseService : IMouseRgbService
{
	private readonly HidDevice _device;
	private readonly HidDevice? _readDevice;
	private readonly ILogger? _logger;
	// Stable identity of the physical unit this service drives (Container ID; "" = single-device).
	// Carried for log context so two identical mice are distinguishable in the device logs.
	private readonly string _deviceKey;

	// Serialises device writes so concurrent gRPC calls don't interleave on the wire.
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	// Caches from which every all-blocks param_1 write is built:
	//   _lastBlock0    — DPI table + debounce/lift/scale (block 0)
	//   _cachedLed*    — LED effect colour + slot (block 1 bytes 24-47)
	//   _lastParam0    — effect/brightness/speed for param_0 patch writes
	private readonly byte[] _lastBlock0 = CreateDefaultBlock0();
	private byte _cachedLedR, _cachedLedG, _cachedLedB, _cachedColorSlot;
	private readonly byte[] _lastParam0 = CreateDefaultParam0();

	// True once the app has applied an explicit LED colour/effect. Until then the colour cache
	// is meaningless (black), so re-asserting it after a key-matrix write would leave the LED
	// dark — the reassert falls back to rainbow breathing (slot 7) instead. See SetButtonBindings.
	private bool _ledEverApplied;

	// Which block images this device already holds, so an apply does not re-commit one to flash that
	// has not changed. See X43BlockCommitCache — it is the reason a colour change no longer rewrites
	// the button table.
	private readonly X43BlockCommitCache _committed = new();

	public EasenMouseService(HidDevice device, HidDevice? readDevice = null, ILogger? logger = null, string deviceKey = "")
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_readDevice = readDevice;
		_logger = logger;
		_deviceKey = deviceKey ?? string.Empty;
	}

	// Carries the write-state of a predecessor instance for the SAME physical unit into this one.
	// The factory evicts cached services on every HID list change so a replugged mouse gets a
	// service bound to its live DevicePath — but the caches describe the device's flash and the
	// app-owned configuration, neither of which changed because an unrelated USB device appeared.
	// Without this, a recreated instance restarts from CreateDefault* and the first apply after an
	// eviction (a) re-commits all three blocks to flash — the identical-key-matrix rewrite
	// X43BlockCommitCache exists to prevent — and (b) on a narrow apply (e.g. LED-only, where
	// ReportRateHz/ActiveStage are null) writes the DEFAULT report rate and active DPI stage over
	// the user's, because the composer falls back to _lastParam0. Keeping the state across a
	// replug matches the pre-eviction behaviour: the commit record only ever dropped on a failed
	// TryOpen (see _committed.Reset call sites), never on a list change. Called by
	// DeviceRgbServiceFactory right after construction, before the instance is published.
	internal void AdoptStateFrom(EasenMouseService predecessor)
	{
		Buffer.BlockCopy(predecessor._lastBlock0, 0, _lastBlock0, 0, 64);
		Buffer.BlockCopy(predecessor._lastParam0, 0, _lastParam0, 0, 64);
		_cachedLedR = predecessor._cachedLedR;
		_cachedLedG = predecessor._cachedLedG;
		_cachedLedB = predecessor._cachedLedB;
		_cachedColorSlot = predecessor._cachedColorSlot;
		_ledEverApplied = predecessor._ledEverApplied;
		_committed.CopyFrom(predecessor._committed);
	}

	// Safe defaults: effect=0x02, 50% brightness, medium speed, 1 ms rate, factory
	// active DPI gear 3 (index 2, 2400 DPI per the X43 spec sheet 3311).
	// Prevents SetActiveDpiStageAsync from writing effect=0x00 (All Off) on first call
	// before any SetLedColorAsync has populated the cache.
	private static byte[] CreateDefaultParam0()
	{
		var p0 = new byte[64];
		p0[0] = 0x01; // report rate 1 ms (factory default 1000 Hz)
		p0[1] = 0x02; // active DPI gear: 3rd (2400) — factory default per spec sheet
		p0[2] = 0x02; // effect — never All Off on first call
		p0[3] = 0x80; // 50% brightness
		p0[4] = 0x03; // medium speed
		return p0;
	}

	// Block-0 defaults. The 7-gear DPI table MUST match the UI's DpiPresets exactly
	// (800/1200/2400/3600/4800/6000/12000) so the stage index the device reports maps
	// 1:1 to the highlighted preset — otherwise the last stage (12000) is unreachable
	// or highlights the wrong row. Also ensures a settings write issued before any DPI
	// write never persists an empty/short DPI table (an all-zero or short block 0
	// kills hardware DPI cycling — confirmed on BLAZE hardware 2026-07-02).
	private static byte[] CreateDefaultBlock0()
	{
		var b = new byte[64];
		b[0] = 8;                // btn_debounce ms
		b[1] = 8;                // wheel_debounce ms
		b[3] = 2;                // sensor_lift mm
		b[4] = 7;                // dpiStage_Max — 7 gears (matches DpiPresets)
		b[5] = 100; b[6] = 0;    // scale_x = 100 → 1.0
		b[7] = 100; b[8] = 0;    // scale_y = 100 → 1.0
		// The 8th slot is unused (dpiStage_Max = 7) but must still hold a VALID DPI: a zero entry
		// makes the firmware reject the param_1 write and take the LED write down with it. 2000 is
		// the OEM's own value for that slot. See X43DpiTable.UnusedStageDpi.
		ReadOnlySpan<ushort> dpi = [800, 1200, 2400, 3600, 4800, 6000, 12000, X43DpiTable.UnusedStageDpi];
		for (int i = 0; i < 8; i++)
		{
			b[9 + i * 2] = (byte)(dpi[i] & 0xFF);
			b[10 + i * 2] = (byte)(dpi[i] >> 8);
			b[25 + i * 2] = (byte)(dpi[i] & 0xFF);
			b[26 + i * 2] = (byte)(dpi[i] >> 8);
		}
		return b;
	}

	// Every per-aspect entry point below funnels into ApplyConfigurationAsync, so ANY change to this
	// device is written the way the OEM writes it: all three param_1 blocks, complete content, in one
	// coordinated transaction. The aspects the caller did not pass are rebuilt from the caches, so
	// "change the LED" still ships the current DPI table and key matrix rather than a partial image.
	//
	// The old per-aspect single-block writes are gone. They are what left the LED dark after a button
	// remap (block 2 alone) and what corrupted the key matrix in July when issued as a burst.
	public Task<bool> SetLedColorAsync(byte r, byte g, byte b, byte effect, byte brightness, byte speed)
	{
		_logger?.LogInformation("[LED] unit={Key} r={R} g={G} b={B} effect=0x{E:X2} bri={Bri} spd={Sp}",
			_deviceKey, r, g, b, effect, brightness, speed);

		return ApplyConfigurationAsync(new DeviceConfiguration
		{
			Led = new LedConfig(r, g, b, effect, brightness, speed),
		});
	}

	// Per-stage indicator colours (stages[i].R/G/B) are intentionally ignored: the OEM app sets no
	// per-DPI colours and writing them disturbs the LED effect. Only the DPI values are written.
	public Task<bool> SetDpiStagesAsync(DpiStageParams[] stages)
	{
		_logger?.LogInformation("[DPI] {Count} stages", stages.Length);

		return ApplyConfigurationAsync(new DeviceConfiguration { DpiStages = stages });
	}

	// The active stage lives in param_0 byte 1, and writing that block is the only mechanism that
	// moves it on this firmware. 0x0B SetCurrentDPI does nothing on hardware — it was recovered from
	// the OEM exe's symbol table, but the OEM UI never sets the stage itself and it appears
	// unimplemented (docs/mouse/blaze/oem-vs750-protocol.md).
	public Task<bool> SetActiveDpiStageAsync(byte stageIndex)
	{
		_logger?.LogInformation("[DPI] active stage {Stage}", stageIndex);

		return ApplyConfigurationAsync(new DeviceConfiguration { ActiveStage = stageIndex });
	}

	public Task<bool> SetReportRateAsync(int hz)
	{
		_logger?.LogInformation("[ReportRate] {Hz} Hz", hz);

		return ApplyConfigurationAsync(new DeviceConfiguration { ReportRateHz = hz });
	}

	// Asks the device to broadcast its current state. Sending command 0x80 (SyncData)
	// makes the firmware push a 0x05 notification [0x05, profile, reportRate, dpi_stage,
	// ledEffect, 0, 0] on its vendor collection (Col04) — the same report the physical
	// DPI button produces. This is how we learn the active DPI stage at startup: the
	// device returns all zeros to HID feature reads (confirmed from the OEM VS750's own
	// USB traffic), so 0x80 → 0x05-notification is the only readable path. The reply is
	// consumed by DeviceHost's DpiStageMonitor. Confirmed from a cold-start USB capture
	// (2026-07-10): VS750 sends exactly [80 00 00 00 00 00 00 …] and the device answers
	// with the current stage. See docs/mouse/blaze/dpi-stage.md.
	public async Task<bool> RequestCurrentStateAsync()
	{
		await _writeLock.WaitAsync();
		try
		{
			if (!_device.TryOpen(out var stream))
			{
				_logger?.LogWarning("[DPI] state query: HidSharp TryOpen failed");
				// The handle is gone, so what this device holds in flash is no longer known — it may
				// come back re-flashed. See X43BlockCommitCache.Reset.
				_committed.Reset();
				return false;
			}

			using (stream)
			{
				SendCommandViaStream(stream, 0x80);
				// Debug level: this is polled every couple seconds by DpiStageMonitor to
				// catch the polling-rate button (which does not broadcast on change),
				// so Info would flood the log. The resulting [RATE]/[DPI] change lines
				// (logged only on an actual change) stay at Info.
				_logger?.LogDebug("[DPI] state query 0x80 sent — awaiting 0x05 notification");
			}
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[DPI] state query failed: {Msg}", ex.Message);
			return false;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	// Assigns functions to the remappable buttons (slots 1–4). Rebuilds key-matrix
	// block 2 from the factory baseline, overlays the requested slots, and writes block
	// 2 ONLY (status=0x04, times=1) — never an all-blocks write, which is what previously
	// bricked the buttons. Slots outside 1–4 (0 left, 5 DPI, 6 rate, 7 LED) are ignored
	// so their factory functions are always preserved. See docs/mouse/blaze/key-matrix.md.
	// The LED re-assert the old single-block version needed is gone: block 1 travels in the same
	// transaction as the key matrix now, and ApplyConfigurationAsync re-commits it (see the mask).
	public Task<bool> SetButtonBindingsAsync(IReadOnlyList<ButtonBinding> bindings)
	{
		_logger?.LogInformation("[Buttons] {N} bindings", bindings.Count);

		return ApplyConfigurationAsync(new DeviceConfiguration { Buttons = bindings });
	}

	// Applies the full desired configuration in ONE coordinated write: assemble the complete 192-byte
	// param_1 (block 0 DPI table + debounce/lift, block 1 LED colours, block 2 key matrix) and write
	// ALL THREE blocks together (status 0x07, times 3) — the write shape the firmware expects and the
	// OEM uses — preceded by param_0 (report rate + active stage + LED effect), in that order.
	// This replaces the six per-aspect writes with one; the key matrix is always fully rebuilt from
	// the factory baseline, so buttons can never be left partially written. The byte composition
	// itself is X43ConfigComposer (pure, unit-tested) — this method is the I/O around it.
	public async Task<bool> ApplyConfigurationAsync(DeviceConfiguration config)
	{
		await _writeLock.WaitAsync();
		try
		{
			// Inside the lock: the gap has to be measured from when the PREVIOUS transaction ended,
			// and only one transaction may be in flight at a time anyway.
			var sinceLast = DateTime.UtcNow - _lastTransactionEndUtc;
			if (sinceLast < TimeSpan.FromMilliseconds(TransactionSettleMs))
			{
				var wait = TimeSpan.FromMilliseconds(TransactionSettleMs) - sinceLast;
				// Information, not Debug: this is the line that says whether the settle engaged, and
				// the runs that matter are cold starts on a normal build, where Debug is off (the host
				// enables it only in Development). It fires only when two transactions land together,
				// so it costs nothing on the single-apply path.
				_logger?.LogInformation("[Apply] unit={Key} settling {Ms}ms since the last transaction",
					_deviceKey, (int)wait.TotalMilliseconds);
				await Task.Delay(wait);
			}

			if (!_device.TryOpen(out var stream))
			{
				_logger?.LogWarning("[Apply] HidSharp TryOpen failed");
				// Same as the state query above: a lost handle invalidates what we believe the
				// device holds, so the next successful apply commits everything again.
				_committed.Reset();
				return false;
			}

			using (stream)
			{
				var composed = X43ConfigComposer.Compose(
					config, _lastBlock0, _lastParam0, _cachedColorSlot, _cachedLedR, _cachedLedG, _cachedLedB);

				Buffer.BlockCopy(composed.Block0, 0, _lastBlock0, 0, 64);
				if (config.Led is not null)
				{
					_cachedLedR = composed.LedR; _cachedLedG = composed.LedG; _cachedLedB = composed.LedB;
					_cachedColorSlot = composed.ColorSlot;
					_ledEverApplied = true;
				}

				// Upload macro-bound buttons to flash BEFORE the key matrix references their slots.
				foreach (var (slot, macro) in composed.MacroUploads)
					await UploadMacroAsync(stream, slot, macro);

				// One param_1 write carrying all three blocks, with the status bitmask naming the
				// blocks this apply actually CHANGES — the OEM sends 0x02 for an LED-only change.
				//
				// We used to hard-code 0x07 ("all blocks changed") on every apply, which makes the
				// firmware parse block 0. When block 0 holds a zero DPI entry it rejects the write,
				// and block 1 (the LED) goes down with it: colour cycle never applied and the other
				// effects were intermittent. Verified byte-for-byte against the OEM captures — with
				// the vendor's block 0, status 0x07 is harmless, so the status is only a problem
				// because it exposes OUR block 0.
				//
				// All three blocks are still sent in ONE write with times=3: this is the same write
				// SHAPE as before, not a return to per-block writes. A burst of single-block writes
				// is what bricked the buttons in July (docs/mouse/blaze) and must stay avoided.
				var param1 = new byte[192];
				Buffer.BlockCopy(composed.Block0, 0, param1, 0, 64);
				Buffer.BlockCopy(composed.Block1, 0, param1, 64, 64);
				Buffer.BlockCopy(composed.Block2, 0, param1, 128, 64);

				// param_0 FIRST, then the param_1 blocks — the OEM's order (frames 5049/5053 then
				// 5063/5067/5073/5081). We used to send them the other way round.
				await WriteParam0ViaStreamAsync(stream, composed.Param0); // also refreshes _lastParam0
				await Task.Delay(PhasePacingMs);

				var blocks = ChangedBlocksMask(config);

				// Drop any block whose content this device already has. The mask above says which
				// blocks the CALLER supplied; this says which of them actually differ. Every block in
				// a param_1 write is a flash commit, and re-committing an identical key matrix on
				// every colour change is what killed the buttons — see X43BlockCommitCache.
				blocks = _committed.Filter(blocks, composed.Block0, composed.Block1, composed.Block2);

				// A key-matrix write leaves the LED dark on this firmware unless the colour table is
				// committed with it. The old code fixed that by re-asserting the LED in a SECOND
				// write after the matrix; committing block 1 in the SAME transaction does the same
				// job without a burst of writes. Only meaningful once a colour has been applied —
				// before that the cache is black, and committing it would darken the LED itself.
				if ((blocks & 0x04) != 0 && _ledEverApplied) blocks |= 0x02;

				// Nothing in param_1 changed — an active-stage or report-rate change lives entirely in
				// param_0, which is already written above. Sending the block write anyway would commit
				// block 2 built from the FACTORY key matrix (the composer has no block-2 cache) and wipe
				// the user's button remaps, which is exactly what a DPI-stage change used to do.
				if (blocks != 0)
				{
					await WriteAllBlocksAsync(stream, param1, blocks);
					_committed.Remember(blocks, composed.Block0, composed.Block1, composed.Block2);
				}

				// NO 0x0D/0x80 LED drive. The OEM sends neither, and its sequence applies the effect
				// on its own — every byte-for-byte replay confirmed that on hardware. The test that
				// appeared to clear the drive left an 8-second gap before sending it, so it only ever
				// cleared it as a LATE command, never as the zero-gap one we actually sent here.
				// Effects landing only sometimes (neon worst) is what that looked like.

				// Let the firmware finish committing before the handle closes under it.
				await Task.Delay(PhasePacingMs);

				// committed= is the mask that actually reached flash after the unchanged blocks were
				// dropped (bit0 = settings/DPI, bit1 = LED colours, bit2 = key matrix). 00 means the
				// apply cost nothing but a param_0 patch, which is the common case for a repeated
				// edit — and the number to watch when checking that RGB edits stopped rewriting the
				// button table.
				_logger?.LogInformation("[Apply] unit={Key} all-blocks write: settings={S} dpiStages={D} buttons={B} led={L} stage={St} rate={R} committed=0x{Blocks:X2}",
					_deviceKey, config.Settings is not null, config.DpiStages?.Length ?? 0, config.Buttons?.Count ?? 0,
					config.Led is not null, config.ActiveStage, config.ReportRateHz, blocks);
			}
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[Apply] ApplyConfiguration failed: {Msg}", ex.Message);
			return false;
		}
		finally
		{
			// Stamped on EVERY exit, success or not: a failed or abandoned transaction still put
			// frames on the wire, so the next one owes the device the same quiet time.
			_lastTransactionEndUtc = DateTime.UtcNow;
			_writeLock.Release();
		}
	}

	// Gap the OEM leaves between the frames of a param_1 write. See WriteAllBlocksAsync.
	private const int BlockPacingMs = 22;

	// Gap the OEM leaves between the param_0 phase and the param_1 phase (~38 ms in the capture).
	// Also used to let the firmware settle before the HID handle is closed under it.
	private const int PhasePacingMs = 40;

	// Minimum quiet time between two apply TRANSACTIONS on this device.
	//
	// The pacing above spaces frames WITHIN one transaction; this spaces the transactions
	// themselves. Both matter, and for the same reason: this firmware tolerates a paced sequence and
	// not a burst. The documented failure is buttons — "separate writes in a burst brick the
	// buttons" (docs/mouse/blaze, confirmed 2026-07-29) — and it shows up exactly where two applies
	// land together.
	//
	// Startup was that place. The App used to re-apply the saved RGB and then the last preset as two
	// separate calls on the same connect event, so two full transactions ran back to back within
	// milliseconds and the mouse intermittently came up with left/right click dead until any later
	// write straightened it out. _writeLock stopped them interleaving, but serialised is not the same
	// as paced: the second began the instant the first returned.
	//
	// That pair is now a single apply (HomeViewModel.ReapplyOnConnect →
	// DeviceDetailsViewModel.ReapplyOnConnectAsync — the preset configuration already carried the
	// lighting, so the RGB push was sending bytes the device was about to receive anyway). This
	// window therefore no longer has a known caller; it stays as a backstop, because any two applies
	// that do land together — a details-page apply arriving as a device connects, say — meet the same
	// firmware. Removing it would trade a bounded 150 ms on a path nothing uses for a class of bug
	// that took weeks to find.
	//
	// 150 ms is the same order as the intra-transaction pacing rather than a guess at the firmware's
	// recovery time; it costs nothing on the single-apply path, which is every user-initiated edit.
	private const int TransactionSettleMs = 150;

	// When the last transaction finished. Only read and written under _writeLock, so it needs no
	// synchronisation of its own.
	private DateTime _lastTransactionEndUtc = DateTime.MinValue;

	// Writes all three param_1 blocks together: header (cmd 0x03, status 0x07 = all blocks, times 3)
	// then the three 64-byte blocks. This is the full-content write shape the firmware expects (and
	// the OEM uses); single-block writes are only for isolated per-aspect changes.
	// Status bitmask for a param_1 write: bit0 = block 0 (DPI table + debounce/lift-off),
	// bit1 = block 1 (LED colours), bit2 = block 2 (key matrix).
	//
	// Returns 0 when the config changes NOTHING in param_1 — an active-stage or report-rate change
	// lives entirely in param_0. Zero means "skip the block write", and that is load-bearing: the
	// composer builds block 2 from the FACTORY key matrix whenever config.Buttons is absent (there
	// is no block-2 cache to fall back on), so declaring a block we did not change does not merely
	// waste a validation — it commits factory defaults over the user's button remaps. This used to
	// fall back to 0x07, which meant changing the DPI stage silently reset every remapped button.
	internal static byte ChangedBlocksMask(DeviceConfiguration config)
	{
		byte mask = 0;
		if (config.Settings is not null || config.DpiStages is { Length: > 0 }) mask |= 0x01;
		if (config.Led is not null) mask |= 0x02;
		if (config.Buttons is { Count: > 0 }) mask |= 0x04;
		return mask;
	}

	private async Task WriteAllBlocksAsync(HidStream stream, byte[] param192, byte status = 0x07)
	{
		SendCommandViaStream(stream, 0x03, 0x00, 0x00, 0, 0, status, 3);
		await Task.Delay(BlockPacingMs);

		for (int i = 0; i < 3; i++)
		{
			var report = new byte[65];
			Buffer.BlockCopy(param192, i * 64, report, 1, 64);
			await stream.WriteAsync(report);
			_logger?.LogTrace("[frame] OUT block{Index} {Hex}", i, Hex(report.AsSpan(1)));

			// The OEM leaves 20-25 ms between each block (cap-06.08.26-for-blaze*.pcapng, frames
			// 6351/6353/6355). We used to send all three back-to-back, which showed up as effects
			// applying only partially or intermittently — the palette landing but the effect not
			// starting. Pacing is part of the write shape, not an optimisation to remove.
			if (i < 2) await Task.Delay(BlockPacingMs);
		}
	}
	
	// Uploads a macro to device flash slot `index` (command 0x04). Confirmed framing from a
	// VS750 capture: header byte 6 = 0x00 (NOT the spec's times=2), 128-byte data in two
	// 64-byte interrupt-OUT packets. See docs/mouse/blaze/macros.md.
	private async Task UploadMacroAsync(HidStream stream, byte index, MacroDefinition macro)
	{
		var data = X43MacroEncoder.Encode(macro);
		SendCommandViaStream(stream, 0x04, 0x00, index); // [04, profile=0, macroIndex, 0,0,0, 0]
		await WriteMacroPacketAsync(stream, data, 0);
		await WriteMacroPacketAsync(stream, data, 1);
		_logger?.LogInformation("[Macro] uploaded to index {Index} ({N} events)", index, macro.Events.Count);
	}

	private static async Task WriteMacroPacketAsync(HidStream stream, byte[] data, int packet)
	{
		var report = new byte[65];
		Buffer.BlockCopy(data, packet * 64, report, 1, 64);
		await stream.WriteAsync(report);
	}

	// Macro encoding, macro type byte, button→slot mapping and the RGB effect→protocol mapping
	// live in the pure X43MacroEncoder / X43KeyMatrix / X43LedEncoder utilities (unit-tested).

	// Keeps the param_0 cache aligned with the device's own 0x05 vendor report. The
	// physical DPI / polling / LED buttons change device state without going through us;
	// feature reads return zeros, so this notification is the only way to resync. With
	// it, a later param_0 write (e.g. a report-rate change from the UI) preserves the
	// REAL active DPI stage (byte 1) and LED effect (byte 2) instead of overwriting them
	// with a stale cache. Byte assignments are atomic — no lock needed for this hint.
	public void SyncStateFromDevice(byte reportRateRaw, byte dpiStage, byte ledEffect)
	{
		if (reportRateRaw != 0) _lastParam0[0] = reportRateRaw;
		_lastParam0[1] = dpiStage;
		_lastParam0[2] = ledEffect;
	}

	// ── Wire helpers ──────────────────────────────────────────────────────────

	private async Task WriteParam0ViaStreamAsync(HidStream stream, byte[] p0)
	{
		SendCommandViaStream(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
		await Task.Delay(BlockPacingMs);

		var report = new byte[65];
		report[0] = 0;
		Buffer.BlockCopy(p0, 0, report, 1, 64);
		await stream.WriteAsync(report);
		_logger?.LogTrace("[frame] OUT param_0 {Hex}", Hex(report.AsSpan(1, 8)));

		Buffer.BlockCopy(p0, 0, _lastParam0, 0, 64);
		_logger?.LogInformation("[LED] param_0 written effect=0x{E:X2} bri={B} spd={S} slot={Sl}",
			p0[2], p0[3], p0[4], p0[5]);
	}

	private void SendCommandViaStream(HidStream stream, byte command,
		byte p1 = 0, byte p2 = 0, byte p3 = 0, byte p4 = 0, byte p5 = 0, byte p6 = 0)
	{
		var report = new byte[9];
		report[0] = 0;
		report[1] = command;
		report[2] = p1; report[3] = p2; report[4] = p3;
		report[5] = p4; report[6] = p5; report[7] = p6;

		// Checksum confirmed not validated by firmware (doc: 没有使用校验和).
		stream.SetFeature(report);
		_logger?.LogTrace("[frame] SET_FEATURE {Hex}", Hex(report.AsSpan(1)));
	}

	// Frame-level trace. Every LED bug on this device so far has come down to a byte-for-byte
	// diff against an OEM capture, and inferring our own frames from the composer is what made
	// that diff wrong twice. Logged at Trace so it costs nothing unless deliberately enabled.
	private static string Hex(ReadOnlySpan<byte> bytes)
	{
		var parts = new string[bytes.Length];
		for (var i = 0; i < bytes.Length; i++) parts[i] = bytes[i].ToString("X2");
		return string.Join(' ', parts);
	}
}
