namespace ProoveGaming.Infrastructure.Classes;

// Patches the TAIPAN (Telink) MsConfig block — command 0x01 write / 0x41 read, 19 (0x13) bytes.
//
// MsConfig holds the polling rate, the active DPI stage and the profile state together, and the
// write's first (0xD1) packet ERASES the sector — so a single-setting change must be a
// read-modify-write of the whole block. These helpers patch a block read back from the device and
// deliberately touch nothing else; TelinkMouseService never fabricates a block.
//
// Offsets come from the vendor's worked full write
// (01 01 01 01 00 00 00 01 03 01 FF 01 02 04 08 01 00 00 00): the polling bytes are the
// 0x0B..0x0F run (per profile) and the active DPI stage sits at 0x11. Both offsets are
// hardware-confirmed — 0x11 was proved by watching the byte move on a physical DPI-button press.
// Only profile 1's polling slot is written; see PollingRateSlots.
public static class TaipanMsConfig
{
	public const byte ConfigCommand = 0x01;
	public const byte ReadCommand = 0x41;

	// The vendor's own worked full-write example (see the header comment above). Used ONLY as a
	// last-resort base for a write that has no real read AND no cached block to patch — e.g. the
	// very first MsConfig write of a session over the 2.4G dongle, which never answers reads at
	// all (see TelinkMouseService.PatchMsConfigAsync). A vendor-confirmed-valid block is safer to
	// build on than inventing values, even though every field this session hasn't explicitly set
	// (wheel speed, DPI-LED behaviour, profile switch/index) ends up at the vendor's example
	// value rather than whatever the user actually had — the alternative is not writing anything
	// at all, which is what "TAIPAN doesn't apply until the DPI button is pressed" was.
	//
	// Private + clone-on-read: a `public static readonly byte[]` only stops the FIELD being
	// reassigned, not its contents being mutated in place — an easy mistake to make since it reads
	// like a constant. GetFactoryDefaultBlock() is the only way to get a copy, so misuse is a
	// compile error (no field to reach into) instead of silently corrupting every subsequent
	// cold-start fallback for the rest of the process.
	private static readonly byte[] _factoryDefaultBlock =
		[0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x03, 0x01, 0xFF, 0x01, 0x02, 0x04, 0x08, 0x01, 0x00, 0x00, 0x00];

	public static byte[] GetFactoryDefaultBlock() => (byte[])_factoryDefaultBlock.Clone();

	public const int Length = 0x13; // 19

	// Full layout, from the vendor address table (Telink_USB协议_地址表v1.3.pdf):
	//   0x00-0x04  Wheel Speed, one per profile
	//   0x05       Sensor_LiftValue      — lift-off distance
	//   0x06       ProfileIndex
	//   0x07-0x0A  DPI-indicator LED behaviour (mode / lit time / blink count / colour select)
	//   0x0B-0x0F  PollingRate, one per profile
	//   0x10       ProfileSwitch         — bitmask enabling profiles 1-5
	//   0x11       DpiSwitch             — active DPI stage
	//   0x12       LedModeSwitch         — current LED mode (NOT a stage count)
	//
	// Only profile 1's polling slot is written: the app has no profile UI, so writing the whole
	// 0x0B..0x0F run would silently retune every profile.
	public const int PollingRateOffset = 0x0B;
	public const int PollingRateSlots = 1;
	public const int DpiSwitchOffset = 0x11;
	public const int SensorLiftValueOffset = 0x05;

	public const int MaxDpiStage = 7;

	// Raw polling period in ms, identical to the BLAZE encoding (X43Settings.ReportRateByte).
	public static byte PollingRateByte(int hz) => hz switch
	{
		1000 => 0x01, // 1 ms
		500 => 0x02,  // 2 ms
		250 => 0x04,  // 4 ms
		125 => 0x08,  // 8 ms
		_ => 0x01,
	};

	// Inverse of PollingRateByte. 0 = unknown, so callers can tell an absent field from a rate.
	public static int HzFromPollingRateByte(byte raw) => raw switch
	{
		0x01 => 1000,
		0x02 => 500,
		0x04 => 250,
		0x08 => 125,
		_ => 0,
	};

	// Sets every polling slot to `hz`, leaving the rest of the block untouched.
	public static void PatchPollingRate(byte[] block, int hz)
	{
		ArgumentNullException.ThrowIfNull(block);
		if (block.Length < Length)
			throw new ArgumentException($"MsConfig block must be at least {Length} bytes", nameof(block));

		var raw = PollingRateByte(hz);
		for (var i = 0; i < PollingRateSlots; i++)
			block[PollingRateOffset + i] = raw;
	}

	// Sets the active DPI stage (DpiSwitch), leaving the rest of the block untouched.
	public static void PatchActiveDpiStage(byte[] block, int stageIndex)
	{
		ArgumentNullException.ThrowIfNull(block);
		if (block.Length < Length)
			throw new ArgumentException($"MsConfig block must be at least {Length} bytes", nameof(block));

		block[DpiSwitchOffset] = (byte)Math.Clamp(stageIndex, 0, MaxDpiStage);
	}

	// Reads the raw stored stage byte (0 when out of range). This is what the block literally holds
	// — for the stage the mouse is really on, use LiveDpiStage.
	public static int ActiveDpiStage(ReadOnlySpan<byte> block) =>
		block.Length > DpiSwitchOffset && block[DpiSwitchOffset] <= MaxDpiStage ? block[DpiSwitchOffset] : 0;

	// A BOOTSTRAP GUESS at the stage the mouse is on — never a fact, and never valid input to a
	// write. Use ResolveStageForWrite for that.
	//
	// Hardware-observed: after a physical button press the stored byte trails the live stage by
	// exactly one — the LED shows green while the block still reads purple. The firmware only
	// flushes this byte on a press, and the press advances one stage, so the stored value is the
	// stage that was left behind and advancing by one recovers the live one.
	//
	// The correction only holds when the FIRMWARE last wrote the byte. When the app wrote it
	// (PatchActiveDpiStage / ResolveStageForWrite) the value is already exact and advancing it
	// overshoots by one. The two cases are indistinguishable from flash alone: our own write of
	// stage S and a single button press landing on S+1 both leave the byte reading S. That is why
	// this may only seed a first-run display, never a device write — feeding it back into a
	// read-modify-write is what silently moved the user's DPI.
	//
	// `stageCount` must come from the DPI table's own count byte so the top stage wraps to the
	// first. An earlier version wrapped on MsConfig 0x12, which the vendor address table shows is
	// LedModeSwitch, not a stage count — it only looked right because it happened to read 0x06.
	public static int LiveDpiStage(ReadOnlySpan<byte> block, int stageCount)
	{
		var stored = ActiveDpiStage(block);
		if (stageCount < 1 || stageCount > MaxDpiStage + 1) stageCount = MaxDpiStage + 1;
		return (stored + 1) % stageCount;
	}

	// No trustworthy active stage. Distinct from stage 0, which is a real stage.
	public const int UnknownStage = -1;

	// Decides what the DPI-stage byte of an outgoing write should hold.
	//
	// The 0xD1 write erases the sector, so every MsConfig write commits SOME value to 0x11 — there
	// is no way to change the polling rate while leaving the stage alone. The rule is therefore:
	// write a stage we actually know (from the device's 0x49 push or from our own earlier write),
	// and otherwise leave the byte exactly as it was read back. A guess must never be committed;
	// doing that is what moved the user's DPI on an unrelated setting change.
	public static void ResolveStageForWrite(byte[] block, int knownStage)
	{
		ArgumentNullException.ThrowIfNull(block);
		if (block.Length < Length)
			throw new ArgumentException($"MsConfig block must be at least {Length} bytes", nameof(block));

		if (knownStage == UnknownStage) return;

		PatchActiveDpiStage(block, knownStage);
	}

	// Picks the block a write is built from: the fresh read when it is usable, otherwise the last
	// block we committed. Returns null when neither is available, which tells the caller to skip
	// the write rather than fabricate a block — an erased sector patched and written back would
	// reset every setting at once.
	public static byte[]? ResolveSourceBlock(byte[]? read, byte[]? lastCommitted) =>
		read is not null && IsPlausible(read) ? read : lastCommitted;

	// True when it is safe to fall back to FactoryDefaultBlock for a write that has neither a real
	// read nor a cached block (e.g. the first MsConfig write of a session over the 2.4G dongle,
	// which never answers reads). FactoryDefaultBlock carries the vendor's own example DPI-stage
	// byte, which has nothing to do with this device's real stage — and ResolveStageForWrite leaves
	// that byte untouched whenever the live stage is unknown. So the fallback is only safe when
	// EITHER this write is about to overwrite the stage itself (`patchSetsStage`) — in which case
	// whatever the fallback block held gets replaced anyway — OR the host already has a live-known
	// stage (`liveDpiStage != UnknownStage`) to write instead of the fallback's own value. If
	// neither holds, using the fallback would silently commit a fabricated stage to the device —
	// the caller should skip the write instead (mirrors the pre-fallback behaviour).
	public static bool IsFactoryDefaultFallbackSafe(bool patchSetsStage, int liveDpiStage) =>
		patchSetsStage || liveDpiStage != UnknownStage;

	// Sets the lift-off distance (Sensor_LiftValue), leaving the rest of the block untouched.
	// The app's slider is in millimetres; the byte's own units are not documented, so the value is
	// passed through unchanged and confirmed on hardware.
	public static void PatchLiftOffDistance(byte[] block, byte liftValue)
	{
		ArgumentNullException.ThrowIfNull(block);
		if (block.Length < Length)
			throw new ArgumentException($"MsConfig block must be at least {Length} bytes", nameof(block));

		block[SensorLiftValueOffset] = liftValue;
	}

	// Reads the lift-off distance back out of a block.
	public static byte LiftOffDistance(ReadOnlySpan<byte> block) =>
		block.Length > SensorLiftValueOffset ? block[SensorLiftValueOffset] : (byte)0;

	// Reads the polling rate back out of a block, in Hz (0 when unknown).
	public static int PollingRateHz(ReadOnlySpan<byte> block) =>
		block.Length > PollingRateOffset ? HzFromPollingRateByte(block[PollingRateOffset]) : 0;

	// True when a block read back from the device looks like real config rather than an erased or
	// half-committed sector. A read taken too soon after a write returns all-0x00 or all-0xFF (the
	// flash erase signatures); patching THAT and writing it back resets every setting at once —
	// the DPI stage to an extreme and the rate to its default. Callers must fall back to their last
	// known-good block instead of committing an implausible one.
	public static bool IsPlausible(ReadOnlySpan<byte> block)
	{
		if (block.Length < Length) return false;

		var allZero = true;
		var allOnes = true;
		for (var i = 0; i < Length; i++)
		{
			if (block[i] != 0x00) allZero = false;
			if (block[i] != 0xFF) allOnes = false;
			if (!allZero && !allOnes) return true;
		}

		return false;
	}
}
