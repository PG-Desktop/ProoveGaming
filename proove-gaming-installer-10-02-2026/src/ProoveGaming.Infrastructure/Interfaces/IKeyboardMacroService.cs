using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Interfaces;

/// <summary>
/// One instance per physical keyboard. Implementations pack a macro library into their
/// family's wire format and upload it — Raster: <c>CMD_SET_MACRODATA (0x2D)</c> chunked
/// across <c>mi_01</c>; Smart Infinite: the <c>0x1C</c> region write; T800: per-slot
/// <c>0x0B</c> blobs. Assignment of macros to physical keys is done separately via
/// <see cref="IKeyboardKeyMappingService.SetKeyMappingAsync"/> with the family's macro
/// type byte and operand order (Raster <c>0x60</c>/<c>0x61</c> and Smart Infinite
/// <c>0x50</c>/<c>0x51</c> put the slot in code1; T800 <c>0x09</c> puts the play type in
/// code1 and the slot in code2) — see "Macro type byte (key record)" in
/// <c>docs/CONTEXT.md</c> for the per-family truth and its hardware evidence.
/// </summary>
public interface IKeyboardMacroService
{
	/// <summary>
	/// Rewrites the whole macro library on the device in one operation. The App SQLite
	/// library is the source of truth; the device buffer is stateless and only holds the
	/// event sequences. Callers pass one entry per used macro slot (up to the family's
	/// slot cap — <see cref="RasterMacroEncoder.MaxMacros"/> /
	/// <see cref="SmartInfiniteMacroEncoder.SlotCount"/> /
	/// <see cref="Ry5088MacroEncoder.LibrarySlots"/>); unused slots do not need entries.
	/// </summary>
	Task<bool> UploadMacrosAsync(IReadOnlyList<MacroLibraryEntry> entries, CancellationToken cancellationToken = default);
}
