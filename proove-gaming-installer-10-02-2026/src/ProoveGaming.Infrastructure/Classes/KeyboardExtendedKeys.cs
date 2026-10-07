namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// The four managed Extended Keys modes, valued as the firmware's advanced-key TYPE byte so a mode
/// travels through gRPC, storage and the record encoder without a mapping table. DKS (<c>0x80</c>)
/// and END (<c>0x85</c>) are deliberately absent: the App never manages them, only preserves them.
/// </summary>
public enum ExtendedKeyMode : byte
{
	ModTap = SmartInfiniteProtocol.TypeExtendedModTap,
	Toggle = SmartInfiniteProtocol.TypeExtendedToggle,
	Socd = SmartInfiniteProtocol.TypeExtendedSocd,
	RapidSnap = SmartInfiniteProtocol.TypeExtendedRapidSnap,
}

/// <summary>
/// A key output in matrix-record form — <c>{0x10, modifierMask, usage}</c>, the shape the App's
/// target list already speaks. The firmware's <c>(codeType, code)</c> pair is derived from it by
/// <see cref="SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord"/>. <see cref="None"/> (all zero) means
/// "no output" — the pair modes carry none, their codes come from the factory table.
/// </summary>
public readonly record struct ExtendedKeyCode(byte TypeByte, byte Code1, byte Code2)
{
	public static ExtendedKeyCode None => default;

	public bool IsNone => TypeByte == 0 && Code1 == 0 && Code2 == 0;

	public KeyMatrixRecord ToMatrixRecord() => new(TypeByte, Code1, Code2);

	public static ExtendedKeyCode FromMatrixRecord(KeyMatrixRecord record) =>
		new(record.Type, record.CodeLow, record.CodeHigh);
}

/// <summary>
/// One Extended Keys record as the App and the DeviceHost exchange it — the shared model of
/// <c>docs/plans/add-cyrex-extended-keys/</c>, wide enough for all four modes so the follow-up mode
/// plans add no fields.
/// </summary>
/// <param name="Mode">The firmware type byte, see <see cref="ExtendedKeyMode"/>.</param>
/// <param name="Slot1">The physical key (MT / TGL) or the first key of the pair (RS / SOCD) — a matrix slot.</param>
/// <param name="Slot2">The second key of a pair (RS / SOCD); <see cref="NoSlot"/> otherwise.</param>
/// <param name="Priority">SOCD priority (0 last input, 1 mutual cancel, 2 key 1, 3 key 2); 0 otherwise.</param>
/// <param name="TimeMs">MT / TGL threshold in milliseconds; 0 otherwise.</param>
/// <param name="HoldKey">MT hold output / TGL toggled output; <see cref="ExtendedKeyCode.None"/> otherwise.</param>
/// <param name="TapKey">MT tap output; <see cref="ExtendedKeyCode.None"/> otherwise.</param>
public sealed record ExtendedKeyEntry(
	ExtendedKeyMode Mode,
	int Slot1,
	int Slot2,
	int Priority,
	int TimeMs,
	ExtendedKeyCode HoldKey,
	ExtendedKeyCode TapKey)
{
	/// <summary>Slot value for "no second key" on the single-key modes.</summary>
	public const int NoSlot = -1;

	/// <summary>True for the modes that link two physical keys (RS, SOCD).</summary>
	public bool IsPair => Mode is ExtendedKeyMode.RapidSnap or ExtendedKeyMode.Socd;

	/// <summary>A Rapid Snap pair.</summary>
	public static ExtendedKeyEntry RapidSnap(int slot1, int slot2) =>
		new(ExtendedKeyMode.RapidSnap, slot1, slot2, 0, 0, ExtendedKeyCode.None, ExtendedKeyCode.None);

	/// <summary>An Instant Release (SOCD) pair; <paramref name="priority"/> as <see cref="SmartInfiniteAdvancedKeyArea.SocdPriorityLastInput"/> and siblings.</summary>
	public static ExtendedKeyEntry Socd(int slot1, int slot2, int priority) =>
		new(ExtendedKeyMode.Socd, slot1, slot2, priority, 0, ExtendedKeyCode.None, ExtendedKeyCode.None);

	/// <summary>A Mod-Tap key: <paramref name="slot"/> outputs <paramref name="hold"/> when held at least <paramref name="timeMs"/>, <paramref name="tap"/> when released sooner.</summary>
	public static ExtendedKeyEntry ModTap(int slot, int timeMs, ExtendedKeyCode hold, ExtendedKeyCode tap) =>
		new(ExtendedKeyMode.ModTap, slot, NoSlot, 0, timeMs, hold, tap);

	/// <summary>A Toggle key: a tap of <paramref name="slot"/> shorter than <paramref name="timeMs"/> latches <paramref name="output"/> on / off; a longer hold is a normal press. The output rides <see cref="HoldKey"/>.</summary>
	public static ExtendedKeyEntry Toggle(int slot, int timeMs, ExtendedKeyCode output) =>
		new(ExtendedKeyMode.Toggle, slot, NoSlot, 0, timeMs, output, ExtendedKeyCode.None);
}

/// <summary>
/// The read-path result: the managed records the board holds, plus what the App needs to gate
/// editing — the area size, how much of it is free, and the slots owned by records the App does not
/// manage (DKS, END, and a managed-type record this build cannot re-encode).
/// </summary>
/// <param name="MaxRecords">How many Rapid Snap-sized (8-byte) records the whole area could hold.</param>
/// <param name="Repaired">
/// True when this read found the board incoherent and reset its advanced keys before answering — the
/// records it reports are what is left after the reset, not what the user configured. A caller must
/// never persist such a read as the user's state: the empty set it describes is the repair's doing,
/// not a choice anybody made.
/// </param>
public sealed record ExtendedKeysState(
	IReadOnlyList<ExtendedKeyEntry> Entries,
	int MaxRecords,
	IReadOnlyList<int> BlockedSlots,
	int AreaBytes,
	int FreeBytes,
	bool Repaired);
