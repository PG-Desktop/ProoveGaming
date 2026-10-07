namespace ProoveGaming.App.Models;

/// <summary>
/// One stored actuation row: the press/release depths, Rapid Trigger state and its quick-press /
/// quick-release travel for a single key slot.
/// </summary>
/// <param name="Slot">
/// Matrix slot the values belong to, or <see cref="ActuationRecord.AllKeysSlot"/> when the board was
/// written uniformly and reports no per-key map.
/// </param>
/// <remarks>
/// Travel values are hundredths of a millimetre, the same unit the device protocol and
/// <c>KeyActuationKeyValues</c> use — there is no conversion anywhere in the storage path, so a
/// value read back is byte-identical to the one written to the board.
/// </remarks>
public sealed record ActuationRecord(
    int Slot,
    int PressCentiMm,
    int ReleaseCentiMm,
    bool RapidTrigger,
    int QuickPressCentiMm,
    int QuickReleaseCentiMm)
{
    /// <summary>
    /// Slot value standing for "every key", used by boards whose firmware reports no per-key map.
    /// Negative so it can never collide with a real matrix slot.
    /// </summary>
    public const int AllKeysSlot = -1;
}
