using System.Collections.Generic;

namespace ProoveGaming.App.Models;

/// <summary>
/// The Extended Keys mode values — the firmware's advanced-key TYPE byte, which is also what the
/// gRPC contract and the storage tables carry. Constants rather than an enum so a stored or pulled
/// value the App does not know yet still round-trips untouched.
/// </summary>
public static class ExtendedKeyMode
{
    /// <summary>Dynamic Keystroke — never written by the App, only preserved; here so <see cref="IsPointerTypeByte"/> covers it.</summary>
    public const int DynamicKeystroke = 0x80;

    public const int ModTap = 0x81;
    public const int Toggle = 0x82;
    public const int Socd = 0x83;
    public const int RapidSnap = 0x84;

    /// <summary>The advanced-key area terminator — the top of the pointer type range, same reason as <see cref="DynamicKeystroke"/>.</summary>
    public const int End = 0x85;

    /// <summary>The modes that link two physical keys and store <c>slot1 &lt; slot2</c>.</summary>
    public static bool IsPair(int mode) => mode is Socd or RapidSnap;

    /// <summary>The modes built on one physical key with picked output(s) and a hold threshold.</summary>
    public static bool IsSingleKey(int mode) => mode is ModTap or Toggle;

    /// <summary>
    /// True for every advanced-key TYPE byte, managed or not (<c>0x80</c>..<c>0x85</c>) — what a
    /// matrix slot carries when it points at a record rather than holding a key of its own.
    /// </summary>
    public static bool IsPointerTypeByte(int typeByte) => typeByte is >= DynamicKeystroke and <= End;
}

/// <summary>
/// The MT / TGL hold threshold in milliseconds — the vendor slider's range and default, shared by the
/// two single-key editors and by the intake paths that judge a stored value.
/// </summary>
public static class ExtendedKeyTime
{
    public const int MinMs = 0;
    public const int MaxMs = 1000;
    public const int DefaultMs = 100;

    public static bool IsDefined(int timeMs) => timeMs is >= MinMs and <= MaxMs;
}

/// <summary>
/// The Instant Release (SOCD) priorities — the firmware's values, carried in the record's LEN high
/// nibble. <see cref="Key1"/> / <see cref="Key2"/> name the record's <b>positions</b> (slot1 / slot2),
/// which is why <see cref="ExtendedKeyRecord.Normalized"/> swaps them along with the slots.
/// </summary>
public static class SocdPriority
{
    public const int LastInput = 0;
    public const int MutualCancel = 1;
    public const int Key1 = 2;
    public const int Key2 = 3;

    public static bool IsDefined(int priority) => priority is >= LastInput and <= Key2;

    /// <summary>True for the two priorities that name a winning key rather than a rule.</summary>
    public static bool IsAbsolute(int priority) => priority is Key1 or Key2;
}

/// <summary>
/// A key output in matrix-record form — <c>{0x10, modifierMask, usage}</c>, the shape the key
/// assignment target list already speaks. <see cref="None"/> (all zero) is "no output".
/// </summary>
public sealed record ExtendedKeyCodeRecord(int TypeByte, int Code1, int Code2)
{
    public static readonly ExtendedKeyCodeRecord None = new(0, 0, 0);

    public bool IsNone => TypeByte == 0 && Code1 == 0 && Code2 == 0;
}

/// <summary>
/// One stored Extended Keys record — the shared model of <c>docs/plans/add-cyrex-extended-keys/</c>,
/// wide enough for all four modes (the mode plans add no columns). Value equality, so a live set and
/// a preset snapshot compare with <c>SequenceEqual</c> once both are normalised.
/// </summary>
/// <param name="Mode">A value of <see cref="ExtendedKeyMode"/>.</param>
/// <param name="Slot1">The physical key (MT / TGL) or the lower slot of the pair (RS / SOCD).</param>
/// <param name="Slot2">The higher slot of the pair (RS / SOCD); <see cref="NoSlot"/> otherwise.</param>
/// <param name="Priority">SOCD priority; 0 otherwise.</param>
/// <param name="TimeMs">MT / TGL threshold in milliseconds; 0 otherwise.</param>
/// <param name="HoldKey">MT hold output / TGL toggled output; <see cref="ExtendedKeyCodeRecord.None"/> otherwise.</param>
/// <param name="TapKey">MT tap output; <see cref="ExtendedKeyCodeRecord.None"/> otherwise.</param>
public sealed record ExtendedKeyRecord(
    int Mode,
    int Slot1,
    int Slot2,
    int Priority,
    int TimeMs,
    ExtendedKeyCodeRecord HoldKey,
    ExtendedKeyCodeRecord TapKey)
{
    /// <summary>Slot value for "no second key" on the single-key modes.</summary>
    public const int NoSlot = -1;

    /// <summary>Bytes a Rapid Snap record costs in the storage area — the "storage full" threshold.</summary>
    public const int RapidSnapRecordBytes = 8;

    /// <summary>Bytes an Instant Release (SOCD) record costs — the same 8-byte shape as Rapid Snap.</summary>
    public const int SocdRecordBytes = 8;

    /// <summary>Bytes a Mod-Tap record costs — TYPE, LEN, a 16-bit time and two outputs.</summary>
    public const int ModTapRecordBytes = 8;

    /// <summary>Bytes a Toggle record costs — TYPE, LEN, a 16-bit time and one output.</summary>
    public const int ToggleRecordBytes = 6;

    public bool IsPair => ExtendedKeyMode.IsPair(Mode);

    public bool ContainsSlot(int slot) => Slot1 == slot || (Slot2 != NoSlot && Slot2 == slot);

    /// <summary>The other key of a pair, or <see cref="NoSlot"/> when <paramref name="slot"/> is not in it or the mode is single-key.</summary>
    public int PartnerOf(int slot) =>
        !IsPair ? NoSlot : slot == Slot1 ? Slot2 : slot == Slot2 ? Slot1 : NoSlot;

    /// <summary>
    /// The canonical form: a pair stores its lower slot first, so the same two keys picked in either
    /// order are one record and one row in the tables. A SOCD absolute priority names a position
    /// (Key 1 = slot1, Key 2 = slot2), so it follows the swap — the same physical key keeps winning.
    /// </summary>
    public ExtendedKeyRecord Normalized()
    {
        if (!IsPair || Slot2 == NoSlot || Slot2 >= Slot1) return this;

        int priority = Mode == ExtendedKeyMode.Socd && SocdPriority.IsAbsolute(Priority)
            ? (Priority == SocdPriority.Key1 ? SocdPriority.Key2 : SocdPriority.Key1)
            : Priority;
        return this with { Slot1 = Slot2, Slot2 = Slot1, Priority = priority };
    }

    /// <summary>The slot an absolute SOCD priority favours, or <see cref="NoSlot"/> for the rule-based priorities and the other modes.</summary>
    public int SocdWinningSlot =>
        Mode != ExtendedKeyMode.Socd ? NoSlot
        : Priority == SocdPriority.Key1 ? Slot1
        : Priority == SocdPriority.Key2 ? Slot2
        : NoSlot;

    public static ExtendedKeyRecord RapidSnap(int slotA, int slotB) =>
        new ExtendedKeyRecord(ExtendedKeyMode.RapidSnap, slotA, slotB, 0, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None).Normalized();

    /// <summary><paramref name="priority"/> refers to the keys as passed (Key 1 = <paramref name="slotA"/>); the result is normalised.</summary>
    public static ExtendedKeyRecord Socd(int slotA, int slotB, int priority) =>
        new ExtendedKeyRecord(ExtendedKeyMode.Socd, slotA, slotB, priority, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None).Normalized();

    /// <summary>A Mod-Tap key on <paramref name="slot"/>: <paramref name="hold"/> when held at least <paramref name="timeMs"/>, <paramref name="tap"/> when released sooner.</summary>
    public static ExtendedKeyRecord ModTap(int slot, int timeMs, ExtendedKeyCodeRecord hold, ExtendedKeyCodeRecord tap) =>
        new(ExtendedKeyMode.ModTap, slot, NoSlot, 0, timeMs, hold, tap);

    /// <summary>A Toggle key on <paramref name="slot"/>: a tap shorter than <paramref name="timeMs"/> latches <paramref name="output"/> on / off; a longer hold is a normal press. The output rides <see cref="HoldKey"/>.</summary>
    public static ExtendedKeyRecord Toggle(int slot, int timeMs, ExtendedKeyCodeRecord output) =>
        new(ExtendedKeyMode.Toggle, slot, NoSlot, 0, timeMs, output, ExtendedKeyCodeRecord.None);

    /// <summary>
    /// Every record normalised and in a fixed order (mode, slot1, slot2) — the shape both the
    /// device-confirmed set and the stored rows are kept in, so the preset push can compare them
    /// with a plain <c>SequenceEqual</c>.
    /// </summary>
    public static List<ExtendedKeyRecord> NormalizeSet(IEnumerable<ExtendedKeyRecord> records)
    {
        var list = new List<ExtendedKeyRecord>();
        foreach (var record in records)
            list.Add(record.Normalized());
        list.Sort(static (a, b) =>
        {
            int byMode = a.Mode.CompareTo(b.Mode);
            if (byMode != 0) return byMode;
            int bySlot1 = a.Slot1.CompareTo(b.Slot1);
            return bySlot1 != 0 ? bySlot1 : a.Slot2.CompareTo(b.Slot2);
        });
        return list;
    }
}
