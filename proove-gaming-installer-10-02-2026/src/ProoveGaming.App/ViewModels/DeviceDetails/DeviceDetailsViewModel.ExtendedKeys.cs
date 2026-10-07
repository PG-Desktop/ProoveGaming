using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// ── Extended Keys backing fields (all UI-thread-only) ─────────────────────
	// The board's advanced-key records (Rapid Snap, SOCD, Mod-Tap and Toggle — each plugs into the same
	// state through BuildExtendedKeyRow and the Is*ModeAvailable flags). Read from the board on
	// device open; every accepted write is mirrored into device_extended_keys so a preset carries
	// the set like every other keyboard setting — which is also what stops the preset key push from
	// erasing the records (see the plan's Options considered).
	//
	// Tri-state probe, as for actuation: null = probe pending or the RPC failed (the nav entry stays
	// and the section shows its loading line — an old DeviceHost's UNIMPLEMENTED lands here), false =
	// a hard "no" from the device (entry hidden, never written), true = editable.
	private bool? _extendedKeysSupported;
	private ExtendedKeysInfo? _extendedKeysInfo;
	private bool _extendedKeysWriteFailed;
	private bool _extendedKeysWriteInFlight;
	// What the board is believed to hold: the device read on open, then every accepted write. The
	// live rows are exactly this set once persisted, so a preset push that would write it is skipped.
	// Normalised (pair slots ascending) and ordered so a plain SequenceEqual is a valid comparison.
	private List<ExtendedKeyRecord>? _extendedKeysOnDevice;
	// Slots owned by records the host preserves but does not manage — they cannot join a record.
	private HashSet<int> _extendedKeysBlockedSlots = [];
	// True when the set above came from a read the host had to REPAIR to answer at all: it found the
	// pointers and the storage area disagreeing (a torn two-region write) and reset the board's
	// advanced keys first. What it then reports is the wreckage of that reset — usually nothing —
	// and saving it as the owner's rows turns one lost packet into permanent data loss (review round
	// 4, m3). The section still shows it, because it IS what the board holds now; only the persist
	// is refused. Cleared by an accepted write, whose set the App chose rather than found.
	private bool _extendedKeysReadWasRepaired;
	// Bumped per load so a slow read cannot land into a later device's state.
	private int _extendedKeysLoadGeneration;

	// ── Editor state (the add-record flow) ───────────────────────────────────
	private int? _extendedKeyEditorMode;
	private KeyAssignmentOptionViewModel? _extendedKeyEditorKey1;
	private KeyAssignmentOptionViewModel? _extendedKeyEditorKey2;
	private bool _extendedKeyEditorTargetIsKey2;
	private string? _extendedKeyEditorMessage;
	// SOCD only: the priority the Confirm will write, mirrored by the four option cards.
	private int _socdPriority = Models.SocdPriority.LastInput;
	// Single-key modes (MT, TGL): the physical key rides _extendedKeyEditorKey1; the outputs come from
	// the Key Assignment dialog's pick purpose as matrix-record triples, and the threshold from the
	// slider. Hold (MT) / the toggled key (TGL) is pre-filled with the physical key (the vendor does the
	// same for MT; for TGL it makes "toggle the key itself" a one-click record) until the user picks
	// one — the flag keeps that pre-fill following a physical-key clear / re-pick.
	private ExtendedKeyCodeRecord? _extendedKeyEditorHold;
	private ExtendedKeyCodeRecord? _extendedKeyEditorTap;
	private bool _extendedKeyEditorHoldIsPrefill;
	private int _extendedKeyEditorTimeMs = ExtendedKeyTime.DefaultMs;

	private bool _isResetExtendedKeysOpen;

	// Key Assignments / Macros: the message shown when a key inside a record is clicked there.
	private string? _extendedKeyBlockedMessage;
	private CancellationTokenSource? _extendedKeyBlockedCts;
	private const int ExtendedKeyBlockedAutoHideMs = 5000;

	/// <summary>The configured records, one row each, in the set's canonical order.</summary>
	public ObservableCollection<ExtendedKeyRowViewModel> ExtendedKeys { get; } = [];

	/// <summary>The SOCD editor's 2x2 "Priority Settings" cards, in firmware priority order (0..3).</summary>
	public IReadOnlyList<SocdPriorityOptionViewModel> SocdPriorityOptions { get; private set; } = [];

	// ── Section state ─────────────────────────────────────────────────────────
	public bool IsExtendedKeysSupported => _extendedKeysSupported == true;
	public bool IsExtendedKeysProbePending => _extendedKeysSupported == null;
	public bool HasExtendedKeys => ExtendedKeys.Count > 0;
	public bool HasNoExtendedKeys => ExtendedKeys.Count == 0;

	/// <summary>
	/// True when the last write did not reach (or verify on) the keyboard — the list has been re-read
	/// from the device, so without this the failure reads as "my record just vanished".
	/// </summary>
	public bool ExtendedKeysWriteFailed
	{
		get => _extendedKeysWriteFailed;
		private set => SetProperty(ref _extendedKeysWriteFailed, value);
	}

	// Per-mode availability — the extension point the mode plans flipped; all four modes ship.
	public bool IsRapidSnapModeAvailable => true;
	public bool IsSocdModeAvailable => true;
	public bool IsModTapModeAvailable => true;
	public bool IsToggleModeAvailable => true;

	/// <summary>The area cannot take another 8-byte record; all four mode rows are disabled with a hint.</summary>
	public bool IsExtendedKeyStorageFull =>
		_extendedKeysInfo is { } info && info.FreeBytes < ExtendedKeyRecord.RapidSnapRecordBytes;

	public bool CanAddRapidSnap => IsExtendedKeysSupported && !IsExtendedKeyStorageFull && !_extendedKeysWriteInFlight;

	/// <summary>Same gate as Rapid Snap — a SOCD record is the same 8 bytes.</summary>
	public bool CanAddSocd => CanAddRapidSnap;

	/// <summary>Same gate again — a Mod-Tap record is 8 bytes too.</summary>
	public bool CanAddModTap => CanAddRapidSnap;

	/// <summary>
	/// Same gate once more. A Toggle record is 6 bytes, so strictly it still fits 6–7 free bytes, but one
	/// "storage full" note and one rule for all four rows beats a row that stays enabled under it.
	/// </summary>
	public bool CanAddToggle => CanAddRapidSnap;

	// ── Editor properties ─────────────────────────────────────────────────────
	public bool IsExtendedKeyEditorOpen => _extendedKeyEditorMode is not null;
	public bool IsExtendedKeyModeListVisible => !IsExtendedKeyEditorOpen;

	/// <summary>The editor is building a SOCD pair — shows the "Priority Settings" block under the chips.</summary>
	public bool IsExtendedKeyEditorSocd => _extendedKeyEditorMode == ExtendedKeyMode.Socd;

	/// <summary>Pair editor (Key 1 / Key 2 chips) vs. single-key editor (physical key, outputs, time).</summary>
	public bool IsExtendedKeyEditorPair => _extendedKeyEditorMode is int pair && !ExtendedKeyMode.IsSingleKey(pair);
	public bool IsExtendedKeyEditorSingleKey => _extendedKeyEditorMode is int single && ExtendedKeyMode.IsSingleKey(single);

	/// <summary>Mod-Tap has two outputs; Toggle shows the one output chip alone, stretched over both columns.</summary>
	public bool ShowExtendedKeyEditorTapChip => _extendedKeyEditorMode == ExtendedKeyMode.ModTap;
	public int ExtendedKeyEditorOutputChipSpan => ShowExtendedKeyEditorTapChip ? 1 : 3;

	/// <summary>The caption of the first output chip: "Hold" for Mod-Tap, "Toggled key" for Toggle.</summary>
	public string ExtendedKeyEditorOutputCaption =>
		LocalizationManager.GetString(_extendedKeyEditorMode == ExtendedKeyMode.Toggle ? "ExtendedKeys_ToggledKey" : "ExtendedKeys_Hold");

	/// <summary>The physical chip takes the next overlay click only while empty — filled, it is cleared with its ✕ first.</summary>
	public bool IsExtendedKeyEditorPhysicalActive => IsExtendedKeyEditorSingleKey && _extendedKeyEditorKey1 is null;

	public string? ExtendedKeyEditorHoldLabel => _extendedKeyEditorHold is { } hold ? ExtendedKeyOutputLabel(hold) : null;
	public string? ExtendedKeyEditorTapLabel => _extendedKeyEditorTap is { } tap ? ExtendedKeyOutputLabel(tap) : null;
	public bool HasExtendedKeyEditorHold => _extendedKeyEditorHold is not null;
	public bool HasExtendedKeyEditorTap => _extendedKeyEditorTap is not null;

	/// <summary>The MT / TGL threshold the Confirm will write, in milliseconds (0..1000, default 100).</summary>
	public int ExtendedKeyEditorTimeMs
	{
		get => _extendedKeyEditorTimeMs;
		set
		{
			int clamped = Math.Clamp(value, ExtendedKeyTime.MinMs, ExtendedKeyTime.MaxMs);
			if (SetProperty(ref _extendedKeyEditorTimeMs, clamped))
				RaiseExtendedKeyEditorTimeChanged();
		}
	}

	/// <summary>The slider position (0..100) for <see cref="ExtendedKeyEditorTimeMs"/>.</summary>
	public double ExtendedKeyEditorTimePct => _extendedKeyEditorTimeMs * 100.0 / ExtendedKeyTime.MaxMs;

	/// <summary>"150 ms", right-aligned beside the Time (ms) heading.</summary>
	public string ExtendedKeyEditorTimeText => FormatTimeMs(_extendedKeyEditorTimeMs);

	/// <summary>"Hold time ≥ 150 ms counts as hold; otherwise tap." — or the Toggle wording of the same rule.</summary>
	public string ExtendedKeyEditorTimeHint =>
		string.Format(
			LocalizationManager.GetString(_extendedKeyEditorMode == ExtendedKeyMode.Toggle ? "ExtendedKeys_Toggle_TimeHint_Format" : "ExtendedKeys_ModTap_TimeHint_Format"),
			_extendedKeyEditorTimeMs);

	/// <summary>The slider's pointer position as 0..1 of the track, snapped to whole milliseconds. UI-only until Confirm.</summary>
	public void SetExtendedKeyEditorTimeFromFraction(double fraction) =>
		ExtendedKeyEditorTimeMs = (int)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * ExtendedKeyTime.MaxMs);

	/// <summary>The SOCD priority Confirm will write, as picked on the cards (default: last input).</summary>
	public int SocdPriority => _socdPriority;

	public string ExtendedKeyEditorTitle =>
		_extendedKeyEditorMode is int mode ? ExtendedKeyModeLabel(mode) : string.Empty;

	public string ExtendedKeyEditorDescription =>
		_extendedKeyEditorMode is int mode ? ExtendedKeyModeDescription(mode) : string.Empty;

	public string? ExtendedKeyEditorKey1Label => _extendedKeyEditorKey1?.KeyLabel;
	public string? ExtendedKeyEditorKey2Label => _extendedKeyEditorKey2?.KeyLabel;
	public bool HasExtendedKeyEditorKey1 => _extendedKeyEditorKey1 is not null;
	public bool HasExtendedKeyEditorKey2 => _extendedKeyEditorKey2 is not null;
	public bool IsExtendedKeyEditorKey1Active => IsExtendedKeyEditorOpen && !_extendedKeyEditorTargetIsKey2;
	public bool IsExtendedKeyEditorKey2Active => IsExtendedKeyEditorOpen && _extendedKeyEditorTargetIsKey2;

	public bool CanConfirmExtendedKey =>
		IsExtendedKeyEditorOpen && !_extendedKeysWriteInFlight && _extendedKeyEditorKey1 is not null
		&& (IsExtendedKeyEditorSingleKey
			? _extendedKeyEditorHold is not null && (!ShowExtendedKeyEditorTapChip || _extendedKeyEditorTap is not null)
			: _extendedKeyEditorKey2 is not null);

	/// <summary>The refusal message of the last rejected pick; null clears it.</summary>
	public string? ExtendedKeyEditorMessage => _extendedKeyEditorMessage;
	public bool HasExtendedKeyEditorMessage => _extendedKeyEditorMessage is not null;

	/// <summary>
	/// "Click a key on the keyboard to set Key 1" — follows the active chip; once both chips are filled
	/// it points at Confirm. The single-key editor walks physical key, outputs, Confirm the same way.
	/// </summary>
	public string ExtendedKeyEditorHint
	{
		get
		{
			if (IsExtendedKeyEditorSingleKey)
			{
				if (_extendedKeyEditorKey1 is null)
					return string.Format(
						LocalizationManager.GetString("ExtendedKeys_PickHint_Format"),
						LocalizationManager.GetString("ExtendedKeys_PhysicalKey"));
				return CanConfirmExtendedKey || _extendedKeysWriteInFlight
					? LocalizationManager.GetString("ExtendedKeys_ConfirmHint_Single")
					: LocalizationManager.GetString("ExtendedKeys_OutputsHint");
			}

			return _extendedKeyEditorKey1 is not null && _extendedKeyEditorKey2 is not null
				? LocalizationManager.GetString("ExtendedKeys_ConfirmHint")
				: string.Format(
					LocalizationManager.GetString("ExtendedKeys_PickHint_Format"),
					LocalizationManager.GetString(_extendedKeyEditorTargetIsKey2 ? "ExtendedKeys_Key2" : "ExtendedKeys_Key1"));
		}
	}

	public bool IsResetExtendedKeysOpen
	{
		get => _isResetExtendedKeysOpen;
		private set => SetProperty(ref _isResetExtendedKeysOpen, value);
	}

	/// <summary>Key Assignments / Macros: "A is part of an extended key (Rapid Snap · D) …", or null.</summary>
	public string? ExtendedKeyBlockedMessage
	{
		get => _extendedKeyBlockedMessage;
		private set
		{
			if (SetProperty(ref _extendedKeyBlockedMessage, value))
				OnPropertyChanged(nameof(HasExtendedKeyBlockedMessage));
		}
	}

	public bool HasExtendedKeyBlockedMessage => _extendedKeyBlockedMessage is not null;

	// ── Mode vocabulary (shared by the list rows, the editor and the tooltips) ─
	private static string ExtendedKeyModeLabel(int mode) => LocalizationManager.GetString(mode switch
	{
		ExtendedKeyMode.Socd => "ExtendedKeys_Mode_Socd",
		ExtendedKeyMode.ModTap => "ExtendedKeys_Mode_ModTap",
		ExtendedKeyMode.Toggle => "ExtendedKeys_Mode_Toggle",
		_ => "ExtendedKeys_Mode_RapidSnap",
	});

	private static string ExtendedKeyModeDescription(int mode) => LocalizationManager.GetString(mode switch
	{
		ExtendedKeyMode.Socd => "ExtendedKeys_Mode_Socd_Description",
		ExtendedKeyMode.ModTap => "ExtendedKeys_Mode_ModTap_Description",
		ExtendedKeyMode.Toggle => "ExtendedKeys_Mode_Toggle_Description",
		_ => "ExtendedKeys_Mode_RapidSnap_Description",
	});

	private static string ExtendedKeyModeBadge(int mode) => LocalizationManager.GetString(mode switch
	{
		ExtendedKeyMode.Socd => "ExtendedKeys_Badge_Socd",
		ExtendedKeyMode.ModTap => "ExtendedKeys_Badge_ModTap",
		ExtendedKeyMode.Toggle => "ExtendedKeys_Badge_Toggle",
		_ => "ExtendedKeys_Badge_RapidSnap",
	});

	// Bytes one record of the mode occupies in the storage area, for the local free-bytes update
	// after a write (the host reports the exact figure on the next read).
	private static int ExtendedKeyRecordBytes(int mode) => mode switch
	{
		ExtendedKeyMode.Toggle => ExtendedKeyRecord.ToggleRecordBytes,
		ExtendedKeyMode.Socd => ExtendedKeyRecord.SocdRecordBytes,
		ExtendedKeyMode.ModTap => ExtendedKeyRecord.ModTapRecordBytes,
		_ => ExtendedKeyRecord.RapidSnapRecordBytes,
	};

	private static string FormatTimeMs(int timeMs) =>
		string.Format(LocalizationManager.GetString("ExtendedKeys_TimeMs_Format"), timeMs);

	/// <summary>
	/// The label of an MT / TGL output — the target list's row with the same (type, bitmask, scancode),
	/// which is how "CTRL (L)" and "ESC" come out localized like every other key label; a code with no
	/// row (a preset pulled from another layout) falls back to its hex.
	/// </summary>
	private string ExtendedKeyOutputLabel(ExtendedKeyCodeRecord code)
	{
		foreach (var target in AssignmentTargets)
			if (!target.IsMacroTarget && target.TypeByte == code.TypeByte && target.Bitmask == code.Code1 && target.ScanCode == code.Code2)
				return target.KeyLabel;
		return $"0x{code.Code1:X2}{code.Code2:X2}";
	}

	private static bool IsExtendedKeyTypeByte(byte type) => type >= 0x80 && type <= 0x85;

	// SOCD priority vocabulary: the rule-based priorities read as row text, the absolute ones as a
	// marker under the winning chip (BuildExtendedKeyRow).
	private static string SocdPriorityLabel(int priority) => LocalizationManager.GetString(priority switch
	{
		Models.SocdPriority.MutualCancel => "ExtendedKeys_Socd_MutualCancel",
		Models.SocdPriority.Key1 or Models.SocdPriority.Key2 => "ExtendedKeys_Socd_Absolute",
		_ => "ExtendedKeys_Socd_LastInput",
	});

	// ── Row building (extension point: one case per mode) ─────────────────────
	private ExtendedKeyRowViewModel BuildExtendedKeyRow(ExtendedKeyRecord record)
	{
		// Rapid Snap carries no parameter; SOCD reads its rule priority, MT and TGL their threshold.
		string? parameter = null;
		string? key1Badge = null;
		string? key2Badge = null;
		string? holdLabel = null;
		string? tapLabel = null;
		if (record.Mode == ExtendedKeyMode.ModTap)
		{
			// "CAPS [MT] CTRL (L) ESC  150 ms" — at a zero threshold every press meets the hold rule ("always
			// hold"), and the plan shows no parameter text for it.
			holdLabel = ExtendedKeyOutputLabel(record.HoldKey);
			tapLabel = ExtendedKeyOutputLabel(record.TapKey);
			parameter = record.TimeMs > 0 ? FormatTimeMs(record.TimeMs) : null;
		}
		else if (record.Mode == ExtendedKeyMode.Toggle)
		{
			// "T [TGL] W  100 ms" — the toggled output rides HoldKey; the zero-threshold rule is MT's.
			holdLabel = ExtendedKeyOutputLabel(record.HoldKey);
			parameter = record.TimeMs > 0 ? FormatTimeMs(record.TimeMs) : null;
		}
		else if (record.Mode == ExtendedKeyMode.Socd)
		{
			if (Models.SocdPriority.IsAbsolute(record.Priority))
			{
				var badge = SocdPriorityLabel(record.Priority);
				key1Badge = record.SocdWinningSlot == record.Slot1 ? badge : null;
				key2Badge = record.SocdWinningSlot == record.Slot2 ? badge : null;
			}
			else
			{
				parameter = SocdPriorityLabel(record.Priority);
			}
		}

		return new ExtendedKeyRowViewModel
		{
			Record = record,
			Key1Label = KeyLabelForSlot(record.Slot1),
			Key2Label = record.IsPair ? KeyLabelForSlot(record.Slot2) : null,
			BadgeText = ExtendedKeyModeBadge(record.Mode),
			BadgeBrush = ExtendedKeyRowViewModel.BadgeBrushFor(record.Mode),
			ParameterText = parameter,
			Key1BadgeText = key1Badge,
			Key2BadgeText = key2Badge,
			HoldLabel = holdLabel,
			TapLabel = tapLabel,
			DeleteCommand = DeleteExtendedKeyCommand,
		};
	}

	private string KeyLabelForSlot(int slot)
	{
		foreach (var key in PhysicalKeys)
			if (ResolveMatrixSlot(key) == slot)
				return key.KeyLabel;
		return $"#{slot}";
	}

	private void RebuildExtendedKeyRows()
	{
		ExtendedKeys.Clear();
		foreach (var record in _extendedKeysOnDevice ?? [])
			ExtendedKeys.Add(BuildExtendedKeyRow(record));
		RefreshExtendedKeyHighlights();
		OnPropertyChanged(nameof(HasExtendedKeys));
		OnPropertyChanged(nameof(HasNoExtendedKeys));
	}

	/// <summary>
	/// Re-derives the rows and the overlay highlights against a rebuilt key layout — the keys are new
	/// view models with every flag at its default (same reason RefreshMacroListSelection runs there).
	/// </summary>
	private void RefreshExtendedKeysAfterLayoutRebuild()
	{
		CancelExtendedKeyEditor();
		RebuildExtendedKeyRows();
	}

	private void RefreshExtendedKeyHighlights()
	{
		var slots = ExtendedKeySlotsInUse();
		foreach (var key in PhysicalKeys)
			key.IsExtendedKeyHighlighted = slots.Contains(ResolveMatrixSlot(key));
	}

	// ── Slot queries (Key Assignments / Macros consult these) ────────────────
	/// <summary>
	/// Every slot holding an advanced-key pointer — the configured records from the live rows plus
	/// the slots the host reports as blocked (records it preserves but does not manage). The skip set
	/// for the key and macro pushes: writing a key record into any of them orphans the record.
	/// </summary>
	private HashSet<int> ExtendedKeySlotsInUse()
	{
		var slots = new HashSet<int>(_extendedKeysBlockedSlots);
		foreach (var record in _extendedKeysOnDevice ?? [])
		{
			slots.Add(record.Slot1);
			if (record.Slot2 != ExtendedKeyRecord.NoSlot) slots.Add(record.Slot2);
		}
		return slots;
	}

	/// <summary>
	/// True when the board's own key matrix says this slot points at an advanced-key record — its
	/// mirrored record carries a <c>0x80</c>..<c>0x85</c> TYPE byte rather than a key.
	/// </summary>
	// The second, independent half of the key/macro push skip set, and the one that needs no probe.
	// ExtendedKeySlotsInUse answers "what do the owner's rows and the host's blocked list claim",
	// which is empty for the whole probe window and forever after a probe that failed — so on its
	// own it lets a debounced push write a key record over a pointer nobody has read yet, orphaning
	// the record (and, for DKS/END, losing it for good: the App does not store unmanaged records and
	// the next rebuild discards the orphaned area bytes). This asks the other question — what does
	// the board actually hold right now — off the mirror the push already reads for its diff, so it
	// holds regardless of the probe (review round 4, M2).
	//
	// Safe unconditionally: taking a record OFF a slot is the extended-keys push's own job, and it
	// restores the freed slots' factory triplets before re-running the key push
	// (ApplyCurrentStateToDeviceCoreAsync), by which point the mirror no longer carries the pointer.
	private bool BoardMirrorHoldsExtendedKeyPointer(byte slot) =>
		_assignments is { } assignments
		&& assignments.TryGetValue(slot, out var record)
		&& ExtendedKeyMode.IsPointerTypeByte(record.Type);

	/// <summary>
	/// The given set minus every record this board cannot take: one naming a slot the keyboard has no
	/// key on, and one landing on a slot the host reports as owned by a record it does not manage
	/// (DKS / END / a managed type this build cannot re-encode). See the call site in
	/// <see cref="ApplyStoredExtendedKeysToDeviceAsync"/> for why one such row costs the user every
	/// other record. Returned untouched while the key layout has not been built yet, so an empty
	/// catalog can never read as "this board has no slots" and drop the whole set.
	/// </summary>
	private List<ExtendedKeyRecord> DropRowsOffThisBoard(List<ExtendedKeyRecord> rows)
	{
		if (rows.Count == 0 || PhysicalKeys.Count == 0)
			return rows;

		var onBoard = new HashSet<int>();
		foreach (var key in PhysicalKeys)
			onBoard.Add(ResolveMatrixSlot(key));

		// A blocked slot already carries somebody else's record, which the host preserves and refuses
		// to write over — so an owner row landing there is refused, and SetExtendedKeys being a
		// WHOLE-SET request takes every other record down with it, on every open, invisibly (review
		// round 4, m1 — the board-side twin of the off-catalog row above). The row stays in storage:
		// the vendor app can free the slot again, and the next open would then push it as normal.
		bool Takes(int slot) => onBoard.Contains(slot) && !_extendedKeysBlockedSlots.Contains(slot);

		var kept = rows.FindAll(row =>
			Takes(row.Slot1)
			&& (row.Slot2 == ExtendedKeyRecord.NoSlot || Takes(row.Slot2)));
		if (kept.Count == rows.Count)
			return rows;

		// The drop is otherwise silent by construction — the row stays in storage, the section shows
		// the board, and nothing in the UI can name a key that does not exist.
		_logger?.LogWarning(
			"[EXTKEY] Dropped {Dropped} of {Total} stored record(s) naming slots outside this board's key catalog or already owned by an unmanaged record",
			rows.Count - kept.Count, rows.Count);
		return kept;
	}

	internal bool IsExtendedKeySlot(int slot) => FindExtendedKeyRecord(slot) is not null;

	/// <summary>A slot Key Assignments / Macros must leave alone: in a configured record, or owned by an unmanaged one.</summary>
	internal bool IsExtendedKeyProtectedSlot(int slot) => IsExtendedKeySlot(slot) || _extendedKeysBlockedSlots.Contains(slot);

	/// <summary>
	/// The board claims advanced keys (catalog flag) but this open's probe has not answered yet, so
	/// <see cref="IsExtendedKeyProtectedSlot"/> cannot be believed for any slot.
	/// </summary>
	// It answers "not protected" for every key while the set is null, which is the one wrong answer
	// that loses data: a remap accepted in this window writes a key record over a record pointer the
	// App has not read. So the click paths refuse outright here rather than read null as "free"
	// (review round 3, S8). It stays true when the probe RPC itself failed — that is a transport
	// fact, not a verdict, and an unknown board is exactly as unsafe to write as an unprobed one; the
	// Extended Keys section sits in the same loading state for the same reason, and a reopen retries.
	internal bool IsExtendedKeyProtectionUnknown =>
		Device?.SupportsExtendedKeys == true && _extendedKeysSupported is null;

	/// <summary>
	/// The two refusals above as one question the hover tip can ask about a key: true when a write
	/// of this key's own record into its slot would be refused, either because the slot is inside a
	/// record or because this open's probe has not said yet.
	/// </summary>
	// The tip's unassign ✕ is the only affordance left on a key that carries BOTH a macro binding
	// and a record — the click path is already refused — so without this it advertises the one
	// action it will bounce (review round 3, S7). UnassignMacroFromKey asks the two halves
	// separately rather than calling this, because each names a different reason to the user; this
	// collapses them because the ✕ has only one thing to do with the answer: not appear.
	internal bool IsExtendedKeyWriteBlocked(KeyAssignmentOptionViewModel key) =>
		IsExtendedKeyProtectionUnknown || IsExtendedKeyProtectedSlot(ResolveMatrixSlot(key));

	private ExtendedKeyRecord? FindExtendedKeyRecord(int slot)
	{
		foreach (var record in _extendedKeysOnDevice ?? [])
			if (record.ContainsSlot(slot))
				return record;
		return null;
	}

	/// <summary>
	/// The hover-tip line for a key inside a record — "Rapid Snap · D" (mode, partner) — or null for
	/// a key in no record, which lets the remap / macro name and then the key's own label through.
	/// </summary>
	internal string? GetExtendedKeyTooltip(KeyAssignmentOptionViewModel key)
	{
		int slot = ResolveMatrixSlot(key);
		var record = FindExtendedKeyRecord(slot);
		if (record is null) return null;

		var mode = ExtendedKeyModeLabel(record.Mode);
		int partner = record.PartnerOf(slot);
		return partner == ExtendedKeyRecord.NoSlot
			? mode
			: string.Format(LocalizationManager.GetString("ExtendedKeys_Tooltip_Format"), mode, KeyLabelForSlot(partner));
	}

	/// <summary>
	/// Key Assignments / Macros: the key belongs to a record, so the dialog stays shut and this says
	/// why — naming the record the way the tooltip does, or, for a slot owned by a record this build
	/// does not manage, the same wording the editor refuses it with. Auto-hides; a section change
	/// clears it.
	/// </summary>
	private void ShowExtendedKeyBlockedMessage(KeyAssignmentOptionViewModel key) =>
		ShowExtendedKeyBannerMessage(GetExtendedKeyTooltip(key) is { } record
			? string.Format(LocalizationManager.GetString("ExtendedKeys_Blocked_Format"), key.KeyLabel, record)
			: LocalizationManager.GetString("ExtendedKeys_Refuse_Blocked"));

	/// <summary>
	/// Key Assignments / Macros: the click came in before this open's advanced-key probe answered, so
	/// the refusal cannot name a record — it says the keyboard is still being read, because the very
	/// next click may well be allowed.
	/// </summary>
	private void ShowExtendedKeyProbePendingMessage() =>
		ShowExtendedKeyBannerMessage(LocalizationManager.GetString("ExtendedKeys_Blocked_ProbePending"));

	private void ShowExtendedKeyBannerMessage(string message)
	{
		ExtendedKeyBlockedMessage = message;

		_extendedKeyBlockedCts?.Cancel();
		var cts = new CancellationTokenSource();
		_extendedKeyBlockedCts = cts;
		_ = AutoHideExtendedKeyBlockedMessageAsync(cts.Token);
	}

	private async Task AutoHideExtendedKeyBlockedMessageAsync(CancellationToken token)
	{
		try { await Task.Delay(ExtendedKeyBlockedAutoHideMs, token); }
		catch (TaskCanceledException) { return; }

		ExtendedKeyBlockedMessage = null;
	}

	private void HideExtendedKeyBlockedMessage()
	{
		_extendedKeyBlockedCts?.Cancel();
		ExtendedKeyBlockedMessage = null;
	}

	// ── Editor flow ───────────────────────────────────────────────────────────
	[RelayCommand]
	private void StartRapidSnapEditor() => BeginExtendedKeyEditor(ExtendedKeyMode.RapidSnap);

	[RelayCommand]
	private void StartSocdEditor() => BeginExtendedKeyEditor(ExtendedKeyMode.Socd);

	[RelayCommand]
	private void StartModTapEditor() => BeginExtendedKeyEditor(ExtendedKeyMode.ModTap);

	[RelayCommand]
	private void StartToggleEditor() => BeginExtendedKeyEditor(ExtendedKeyMode.Toggle);

	private void BeginExtendedKeyEditor(int mode)
	{
		bool canAdd = mode switch
		{
			ExtendedKeyMode.Socd => CanAddSocd,
			ExtendedKeyMode.ModTap => CanAddModTap,
			ExtendedKeyMode.Toggle => CanAddToggle,
			_ => CanAddRapidSnap,
		};
		if (!canAdd) return;
		// The editor's picks ride IsSelected; nothing else on this section may hold the flag.
		foreach (var key in PhysicalKeys)
			key.IsSelected = false;
		_extendedKeyEditorMode = mode;
		_extendedKeyEditorKey1 = null;
		_extendedKeyEditorKey2 = null;
		_extendedKeyEditorTargetIsKey2 = false;
		_extendedKeyEditorMessage = null;
		ResetExtendedKeyEditorOutputs();
		if (mode == ExtendedKeyMode.Socd)
			ResetSocdPriority();
		RaiseExtendedKeyEditorChanged();
	}

	// Every editor open starts the single-key state at the vendor's defaults: no outputs, 100 ms.
	private void ResetExtendedKeyEditorOutputs()
	{
		_extendedKeyEditorHold = null;
		_extendedKeyEditorTap = null;
		_extendedKeyEditorHoldIsPrefill = false;
		ExtendedKeyEditorTimeMs = ExtendedKeyTime.DefaultMs;
	}

	// The four cards exist from the first SOCD editor on; every open starts at the vendor's default.
	private void ResetSocdPriority()
	{
		if (SocdPriorityOptions.Count == 0)
		{
			SocdPriorityOptions =
			[
				new SocdPriorityOptionViewModel(Models.SocdPriority.LastInput, OnSocdPrioritySelected),
				new SocdPriorityOptionViewModel(Models.SocdPriority.MutualCancel, OnSocdPrioritySelected),
				new SocdPriorityOptionViewModel(Models.SocdPriority.Key1, OnSocdPrioritySelected),
				new SocdPriorityOptionViewModel(Models.SocdPriority.Key2, OnSocdPrioritySelected),
			];
			OnPropertyChanged(nameof(SocdPriorityOptions));
		}

		_socdPriority = Models.SocdPriority.LastInput;
		foreach (var option in SocdPriorityOptions)
			option.IsSelected = option.Priority == _socdPriority;
	}

	// A card's RadioButton went checked: it becomes the priority and the other cards let go. Setting
	// IsSelected = false never re-enters here (the callback fires on true only).
	private void OnSocdPrioritySelected(SocdPriorityOptionViewModel selected)
	{
		_socdPriority = selected.Priority;
		foreach (var option in SocdPriorityOptions)
			if (!ReferenceEquals(option, selected))
				option.IsSelected = false;
		OnPropertyChanged(nameof(SocdPriority));
	}

	// The absolute cards name the key they favour: the picked chip's label, or "Key 1" / "Key 2".
	private void RefreshSocdPriorityOptions()
	{
		foreach (var option in SocdPriorityOptions)
		{
			if (option.IsAbsolute)
			{
				bool isKey1 = option.Priority == Models.SocdPriority.Key1;
				var key = isKey1 ? _extendedKeyEditorKey1 : _extendedKeyEditorKey2;
				option.KeyLabel = key?.KeyLabel ?? LocalizationManager.GetString(isKey1 ? "ExtendedKeys_Key1" : "ExtendedKeys_Key2");
			}
			option.RefreshText();
		}
	}

	[RelayCommand]
	private void CancelExtendedKeyEditor()
	{
		if (!IsExtendedKeyEditorOpen) return;
		// An output pick still open belongs to this editor: closing it drops its callback, so a late
		// pick can never land in the next editor (review focus: null-safety).
		if (IsKeyDialogPickPurpose)
			IsKeyAssignmentDialogOpen = false;
		if (_extendedKeyEditorKey1 is not null) _extendedKeyEditorKey1.IsSelected = false;
		if (_extendedKeyEditorKey2 is not null) _extendedKeyEditorKey2.IsSelected = false;
		_extendedKeyEditorMode = null;
		_extendedKeyEditorKey1 = null;
		_extendedKeyEditorKey2 = null;
		_extendedKeyEditorTargetIsKey2 = false;
		_extendedKeyEditorMessage = null;
		ResetExtendedKeyEditorOutputs();
		RaiseExtendedKeyEditorChanged();
	}

	[RelayCommand]
	private void ActivateExtendedKeyEditorKey1() => SetExtendedKeyEditorTarget(isKey2: false);

	[RelayCommand]
	private void ActivateExtendedKeyEditorKey2() => SetExtendedKeyEditorTarget(isKey2: true);

	private void SetExtendedKeyEditorTarget(bool isKey2)
	{
		if (!IsExtendedKeyEditorOpen) return;
		_extendedKeyEditorTargetIsKey2 = isKey2;
		_extendedKeyEditorMessage = null;
		RaiseExtendedKeyEditorChanged();
	}

	[RelayCommand]
	private void ClearExtendedKeyEditorKey1() => ClearExtendedKeyEditorKey(isKey2: false);

	[RelayCommand]
	private void ClearExtendedKeyEditorKey2() => ClearExtendedKeyEditorKey(isKey2: true);

	private void ClearExtendedKeyEditorKey(bool isKey2)
	{
		if (!IsExtendedKeyEditorOpen) return;
		ref var target = ref isKey2 ? ref _extendedKeyEditorKey2 : ref _extendedKeyEditorKey1;
		if (target is not null) target.IsSelected = false;
		target = null;
		// The cleared chip is the one to fill next.
		_extendedKeyEditorTargetIsKey2 = isKey2;
		_extendedKeyEditorMessage = null;
		// The Hold pre-fill was the physical key itself; with that key gone it means nothing.
		if (!isKey2 && _extendedKeyEditorHoldIsPrefill)
		{
			_extendedKeyEditorHold = null;
			_extendedKeyEditorHoldIsPrefill = false;
		}
		RaiseExtendedKeyEditorChanged();
	}

	/// <summary>
	/// A click on the overlay while the editor is open: fills the active chip, or refuses with a
	/// message. Refused: the other chip's key, FN, a key already in a record, a slot owned by an
	/// unmanaged record, a macro-bound key, a remapped key (vendor silently drops the remap; we say
	/// so instead). Clicking the active chip's own key clears it. In the single-key editor the one
	/// chip is the physical key: filled, it is disabled until cleared, and filling it pre-fills the
	/// first output (MT Hold / TGL toggled key) with the same key. No write happens here.
	/// </summary>
	internal void SelectExtendedKey(KeyAssignmentOptionViewModel option)
	{
		if (!IsExtendedKeyEditorOpen) return;

		var active = _extendedKeyEditorTargetIsKey2 ? _extendedKeyEditorKey2 : _extendedKeyEditorKey1;
		var other = _extendedKeyEditorTargetIsKey2 ? _extendedKeyEditorKey1 : _extendedKeyEditorKey2;
		if (ReferenceEquals(active, option))
		{
			ClearExtendedKeyEditorKey(_extendedKeyEditorTargetIsKey2);
			return;
		}

		if (IsExtendedKeyEditorSingleKey && active is not null)
		{
			_extendedKeyEditorMessage = LocalizationManager.GetString("ExtendedKeys_Refuse_PhysicalKeySet");
			RaiseExtendedKeyEditorChanged();
			return;
		}

		int slot = ResolveMatrixSlot(option);
		string? refusal =
			ReferenceEquals(other, option) ? "ExtendedKeys_Refuse_SameKey"
			: option.TypeByte != KeyboardTypeByte || (option.ScanCode == 0 && option.Bitmask == 0) ? "ExtendedKeys_Refuse_Fn"
			: IsExtendedKeySlot(slot) ? "ExtendedKeys_Refuse_AlreadyUsed"
			: _extendedKeysBlockedSlots.Contains(slot) ? "ExtendedKeys_Refuse_Blocked"
			: BoundMacroForKey(option) is not null ? "ExtendedKeys_Refuse_MacroBound"
			: IsKeyRemapped(option, slot) ? "ExtendedKeys_Refuse_Remapped"
			: null;
		if (refusal is not null)
		{
			_extendedKeyEditorMessage = LocalizationManager.GetString(refusal);
			RaiseExtendedKeyEditorChanged();
			return;
		}

		if (active is not null) active.IsSelected = false;
		option.IsSelected = true;
		if (_extendedKeyEditorTargetIsKey2) _extendedKeyEditorKey2 = option;
		else _extendedKeyEditorKey1 = option;

		if (IsExtendedKeyEditorSingleKey)
		{
			if (_extendedKeyEditorHold is null || _extendedKeyEditorHoldIsPrefill)
			{
				_extendedKeyEditorHold = new ExtendedKeyCodeRecord(option.TypeByte, option.Bitmask, option.ScanCode);
				_extendedKeyEditorHoldIsPrefill = true;
			}
		}
		// Move on to the empty chip, if there is one.
		else if (!_extendedKeyEditorTargetIsKey2 && _extendedKeyEditorKey2 is null)
			_extendedKeyEditorTargetIsKey2 = true;
		else if (_extendedKeyEditorTargetIsKey2 && _extendedKeyEditorKey1 is null)
			_extendedKeyEditorTargetIsKey2 = false;

		_extendedKeyEditorMessage = null;
		RaiseExtendedKeyEditorChanged();
	}

	// A persisted override row, or a device record that is neither the key's own nor a pointer — a
	// key the user has remapped. The vendor silently drops such a remap when the key joins a pair;
	// refusing is the honest alternative (plan Open question 2).
	private bool IsKeyRemapped(KeyAssignmentOptionViewModel option, int slot)
	{
		if (_slotKeyOverrides is not null && _slotKeyOverrides.ContainsKey((byte)slot))
			return true;
		if (_assignments is not null && _assignments.TryGetValue((byte)slot, out var live))
			return live != (option.TypeByte, option.Bitmask, option.ScanCode) && !IsExtendedKeyTypeByte(live.Type);
		return false;
	}

	// ── Output pickers (single-key modes) ─────────────────────────────────────
	[RelayCommand]
	private void PickExtendedKeyHold() => OpenExtendedKeyOutputPicker(isTap: false);

	[RelayCommand]
	private void PickExtendedKeyTap() => OpenExtendedKeyOutputPicker(isTap: true);

	/// <summary>
	/// Opens the Key Assignment dialog in its pick purpose for the Hold / Toggled or Tap chip. The picked
	/// triple lands in the chip on the UI thread (the dialog's own command); nothing is staged or written.
	/// A pick of the FN / placeholder kind the firmware cannot encode is refused like an overlay click.
	/// </summary>
	private void OpenExtendedKeyOutputPicker(bool isTap)
	{
		if (!IsExtendedKeyEditorSingleKey || (isTap && !ShowExtendedKeyEditorTapChip)) return;

		var current = isTap ? _extendedKeyEditorTap : _extendedKeyEditorHold;
		var role = LocalizationManager.GetString(
			isTap ? "ExtendedKeys_Tap"
			: _extendedKeyEditorMode == ExtendedKeyMode.Toggle ? "ExtendedKeys_Toggled"
			: "ExtendedKeys_Hold");
		_extendedKeyEditorMessage = null;
		RaiseExtendedKeyEditorChanged();

		OpenKeyDialogForPick(
			role,
			current is { } c ? ((byte)c.TypeByte, (byte)c.Code1, (byte)c.Code2) : null,
			picked =>
			{
				// Cancelling the editor closes the dialog and drops this callback; the check is for a
				// dialog that outlived the editor some other way (a device switch mid-pick).
				if (!IsExtendedKeyEditorSingleKey) return;

				if (picked.TypeByte != KeyboardTypeByte || (picked.ScanCode == 0 && picked.Bitmask == 0))
				{
					_extendedKeyEditorMessage = LocalizationManager.GetString("ExtendedKeys_Refuse_Fn");
					RaiseExtendedKeyEditorChanged();
					return;
				}

				var code = new ExtendedKeyCodeRecord(picked.TypeByte, picked.Bitmask, picked.ScanCode);
				if (isTap)
				{
					_extendedKeyEditorTap = code;
				}
				else
				{
					_extendedKeyEditorHold = code;
					_extendedKeyEditorHoldIsPrefill = false;
				}
				_extendedKeyEditorMessage = null;
				RaiseExtendedKeyEditorChanged();
			});
	}

	/// <summary>
	/// Runs one user-initiated extended-keys write under <c>_presetApplyGate</c>, so it is ORDERED
	/// against a preset apply instead of racing it.
	/// </summary>
	// Without it the two are unordered and a preset switch during a 1-2 s write loses both ways
	// (review round 4, m2): the preset's own push gets `false` from the in-flight guard — the same
	// answer as "the board already matches", so no banner and no re-run of the key push — and then
	// the user's write finishes and persists the PRE-switch set into the live rows on top of the
	// snapshot just loaded for the incoming preset. The board and the rows end up carrying the old
	// preset's records while the new one counts as applied and reads dirty.
	//
	// This gate rather than making the preset push wait for the in-flight write: waiting fixes only
	// the first half. The preset apply overwrites the live rows (LoadPresetSnapshotToDevice) well
	// before it reaches the extended-keys push, so a write that lands after that point still
	// persists the old set over the new snapshot. Holding the gate puts the whole apply — snapshot
	// load included — behind the write, which is the only ordering with a correct end state.
	//
	// Nothing blocks the dispatcher: WaitAsync is awaited, not waited on, so the window keeps
	// pumping and the only visible effect of a contended gate is that the command's own button
	// stays disabled (AsyncRelayCommand refuses concurrent execution) until the apply ahead of it
	// finishes. Lock order is unchanged — this gate, then _applyGate, never the reverse — and the
	// gate is never re-entered from inside itself: the preset push calls WriteExtendedKeysAsync
	// directly, not through here.
	private async Task UnderPresetApplyGateAsync(Func<Task> write)
	{
		await _presetApplyGate.WaitAsync();
		try
		{
			await write();
		}
		finally
		{
			_presetApplyGate.Release();
		}
	}

	[RelayCommand]
	private Task ConfirmExtendedKey() => UnderPresetApplyGateAsync(ConfirmExtendedKeyCoreAsync);

	private async Task ConfirmExtendedKeyCoreAsync()
	{
		// Re-checked here, i.e. AFTER the gate wait: a preset apply queued ahead of this reloads the
		// key layout, and ResetKeyLayoutState cancels the editor these picks would be read from.
		if (!CanConfirmExtendedKey) return;

		int slot1 = ResolveMatrixSlot(_extendedKeyEditorKey1!);
		int slot2 = _extendedKeyEditorKey2 is { } key2 ? ResolveMatrixSlot(key2) : ExtendedKeyRecord.NoSlot;
		// The modes' extension point: Key 1 / Key 2 are the chips as picked and the record factories
		// normalise (SOCD carries the absolute priority across the swap); the single-key modes take the
		// physical key, the picked outputs and the slider's threshold.
		ExtendedKeyRecord? record = _extendedKeyEditorMode switch
		{
			ExtendedKeyMode.RapidSnap => ExtendedKeyRecord.RapidSnap(slot1, slot2),
			ExtendedKeyMode.Socd => ExtendedKeyRecord.Socd(slot1, slot2, _socdPriority),
			ExtendedKeyMode.ModTap when _extendedKeyEditorHold is { } hold && _extendedKeyEditorTap is { } tap =>
				ExtendedKeyRecord.ModTap(slot1, _extendedKeyEditorTimeMs, hold, tap),
			ExtendedKeyMode.Toggle when _extendedKeyEditorHold is { } output =>
				ExtendedKeyRecord.Toggle(slot1, _extendedKeyEditorTimeMs, output),
			_ => null,
		};
		if (record is null) return;

		var desired = new List<ExtendedKeyRecord>(_extendedKeysOnDevice ?? []) { record };

		if (await WriteExtendedKeysAsync(desired, persist: true))
			CancelExtendedKeyEditor();
	}

	[RelayCommand]
	private Task DeleteExtendedKey(ExtendedKeyRowViewModel? row) =>
		UnderPresetApplyGateAsync(() => DeleteExtendedKeyCoreAsync(row));

	private async Task DeleteExtendedKeyCoreAsync(ExtendedKeyRowViewModel? row)
	{
		// Read after the gate wait: an apply that ran ahead replaced the device-confirmed set, and
		// the row the user clicked may no longer be in it.
		if (row is null || _extendedKeysOnDevice is null) return;

		var desired = _extendedKeysOnDevice.Where(r => r != row.Record).ToList();
		await WriteExtendedKeysAsync(desired, persist: true);
	}

	// ── Default Settings (remove every managed record, after confirmation) ────
	[RelayCommand]
	private void RestoreExtendedKeysDefaults()
	{
		if (!IsExtendedKeysSupported || _extendedKeysWriteInFlight) return;
		IsResetExtendedKeysOpen = true;
	}

	[RelayCommand]
	private void CancelResetExtendedKeys() => IsResetExtendedKeysOpen = false;

	[RelayCommand]
	private Task ConfirmResetExtendedKeys()
	{
		// Both before the gate wait: the dialog must close on the click, not when the gate frees up.
		IsResetExtendedKeysOpen = false;
		CancelExtendedKeyEditor();
		return UnderPresetApplyGateAsync(() => WriteExtendedKeysAsync([], persist: true));
	}

	private void RaiseExtendedKeyEditorChanged()
	{
		OnPropertyChanged(nameof(IsExtendedKeyEditorOpen));
		OnPropertyChanged(nameof(IsExtendedKeyModeListVisible));
		OnPropertyChanged(nameof(ExtendedKeyEditorTitle));
		OnPropertyChanged(nameof(ExtendedKeyEditorDescription));
		OnPropertyChanged(nameof(ExtendedKeyEditorKey1Label));
		OnPropertyChanged(nameof(ExtendedKeyEditorKey2Label));
		OnPropertyChanged(nameof(HasExtendedKeyEditorKey1));
		OnPropertyChanged(nameof(HasExtendedKeyEditorKey2));
		OnPropertyChanged(nameof(IsExtendedKeyEditorKey1Active));
		OnPropertyChanged(nameof(IsExtendedKeyEditorKey2Active));
		OnPropertyChanged(nameof(CanConfirmExtendedKey));
		OnPropertyChanged(nameof(ExtendedKeyEditorMessage));
		OnPropertyChanged(nameof(HasExtendedKeyEditorMessage));
		OnPropertyChanged(nameof(ExtendedKeyEditorHint));
		OnPropertyChanged(nameof(IsExtendedKeyEditorSocd));
		OnPropertyChanged(nameof(SocdPriority));
		OnPropertyChanged(nameof(IsExtendedKeyEditorPair));
		OnPropertyChanged(nameof(IsExtendedKeyEditorSingleKey));
		OnPropertyChanged(nameof(ShowExtendedKeyEditorTapChip));
		OnPropertyChanged(nameof(ExtendedKeyEditorOutputChipSpan));
		OnPropertyChanged(nameof(ExtendedKeyEditorOutputCaption));
		OnPropertyChanged(nameof(ExtendedKeyEditorTimeHint));
		OnPropertyChanged(nameof(IsExtendedKeyEditorPhysicalActive));
		OnPropertyChanged(nameof(ExtendedKeyEditorHoldLabel));
		OnPropertyChanged(nameof(ExtendedKeyEditorTapLabel));
		OnPropertyChanged(nameof(HasExtendedKeyEditorHold));
		OnPropertyChanged(nameof(HasExtendedKeyEditorTap));
		RefreshSocdPriorityOptions();
	}

	// The slider moves often; only the three time-derived values re-read (the setter's SetProperty
	// already raised ExtendedKeyEditorTimeMs itself), not the whole editor.
	private void RaiseExtendedKeyEditorTimeChanged()
	{
		OnPropertyChanged(nameof(ExtendedKeyEditorTimePct));
		OnPropertyChanged(nameof(ExtendedKeyEditorTimeText));
		OnPropertyChanged(nameof(ExtendedKeyEditorTimeHint));
	}

	private void RaiseExtendedKeysStateChanged()
	{
		OnPropertyChanged(nameof(IsExtendedKeysSupported));
		OnPropertyChanged(nameof(IsExtendedKeysProbePending));
		OnPropertyChanged(nameof(IsExtendedKeysNavVisible));
		OnPropertyChanged(nameof(KeyboardNavButtonsCount));
		OnPropertyChanged(nameof(KeyboardActionsMenuHeight));
		OnPropertyChanged(nameof(IsExtendedKeyStorageFull));
		OnPropertyChanged(nameof(CanAddRapidSnap));
		OnPropertyChanged(nameof(CanAddSocd));
		OnPropertyChanged(nameof(CanAddModTap));
		OnPropertyChanged(nameof(CanAddToggle));
		OnPropertyChanged(nameof(CanConfirmExtendedKey));
		OnPropertyChanged(nameof(ExtendedKeyEditorHint));
	}

	// ── Load (device open) ────────────────────────────────────────────────────
	/// <summary>
	/// Everything this view model believes about one board's advanced keys, dropped as a unit: the
	/// probe verdict, the record set, the blocked slots, the rows and the overlay highlights derived
	/// from them. Called by <c>SetDeviceAsync</c>'s prologue beside the sibling device-truth caches
	/// (<c>_appliedMacroSlots</c>, <c>_appliedSlotOverrides</c>) and again by the probe below.
	/// </summary>
	// It must run in the prologue and not only here: the probe sits late in the follow-up chain,
	// after the page is visible and after PopulateKeyLayout has re-derived these rows onto the NEW
	// keyboard's keys — so between the two the previous board's records are highlighted on a board
	// that does not have them, and, worse, a slot the new board HAS a record on reads as free
	// (ExtendedKeySlotsInUse answers from the old set, not "unknown"), which is enough for a remap
	// to write a key record over a pointer nobody has read yet (review round 3, S8).
	internal void ResetExtendedKeysState()
	{
		_extendedKeysSupported = null;
		_extendedKeysInfo = null;
		_extendedKeysReadWasRepaired = false;
		ExtendedKeysWriteFailed = false;
		_extendedKeysOnDevice = null;
		_extendedKeysBlockedSlots = [];
		CancelExtendedKeyEditor();
		HideExtendedKeyBlockedMessage();
		IsResetExtendedKeysOpen = false;
		RebuildExtendedKeyRows();
		RaiseExtendedKeysStateChanged();
	}

	// Awaited (failure-swallowed) in SetDeviceAsync's follow-up chain, after the key layout is built
	// (the rows take their labels from PhysicalKeys) and before the default-preset push (which needs
	// the device-confirmed set to know whether it has anything to write). Internal so tests can await
	// it directly against a Device set via the plain property.
	internal Task LoadExtendedKeysFromDeviceAsync() => LoadExtendedKeysFromDeviceAsync(clearWriteFailure: true);

	private async Task LoadExtendedKeysFromDeviceAsync(bool clearWriteFailure)
	{
		var device = Device;
		int generation = ++_extendedKeysLoadGeneration;
		// A device switch starts clean — idempotently, since the prologue already cleared this for a
		// device open; the reload after a failed write instead keeps the banner and the record set
		// up, because the list it re-reads is what explains the banner.
		if (clearWriteFailure)
		{
			ResetExtendedKeysState();
		}
		else
		{
			_extendedKeysSupported = null;
			_extendedKeysInfo = null;
			_extendedKeysReadWasRepaired = false;
			RaiseExtendedKeysStateChanged();
		}

		if (device is null || !device.SupportsExtendedKeys || _usbHidDeviceService is null)
			return;

		var info = await _usbHidDeviceService.GetExtendedKeysAsync(device.VendorId, device.ProductId);
		if (!ReferenceEquals(Device, device) || generation != _extendedKeysLoadGeneration)
			return;

		if (info is null)
		{
			// The RPC itself failed (an old DeviceHost's UNIMPLEMENTED included) — a transport fact,
			// not a device verdict. The section stays in its loading state; a reopen retries.
			return;
		}

		if (!info.Supported)
		{
			// A hard "no" from the probe: hide the section rather than offer an editor that would
			// write to a board the safety gate refused.
			_extendedKeysSupported = false;
			if (ActiveSection == NavSection.ExtendedKeys)
				ActiveSection = NavSection.Presets;
			RaiseExtendedKeysStateChanged();
			return;
		}

		_extendedKeysSupported = true;
		_extendedKeysInfo = info;
		// Carried on the read, not derived: only the host can tell that it reset the board to answer.
		_extendedKeysReadWasRepaired = info.Repaired;
		_extendedKeysBlockedSlots = new HashSet<int>(info.BlockedSlots);
		// The read IS the board's state — the belief a preset push compares its rows against. Not
		// persisted: seeding live rows from the board would light Save Preset on a page the user
		// never touched (see PersistActuationState for the same rule).
		_extendedKeysOnDevice = ExtendedKeyRecord.NormalizeSet(info.Entries);
		RebuildExtendedKeyRows();
		RaiseExtendedKeysStateChanged();
	}

	// ── Write path (shared by add / delete / Default Settings / preset push) ──
	/// <summary>
	/// One SetExtendedKeys call carrying the full desired set. On success the device-confirmed set,
	/// the rows, the highlights and (when <paramref name="persist"/>) the live storage rows follow;
	/// on failure the banner goes up and the list is re-read from the device, with the truth written
	/// back to the live rows so a preset can never be called clean over a refused set.
	/// </summary>
	private async Task<bool> WriteExtendedKeysAsync(List<ExtendedKeyRecord> desired, bool persist)
	{
		var device = Device;
		if (device is null || _usbHidDeviceService is null || !IsDeviceConfigurable
			|| _extendedKeysSupported != true || _extendedKeysWriteInFlight)
			return false;

		var normalized = ExtendedKeyRecord.NormalizeSet(desired);
		_extendedKeysWriteInFlight = true;
		RaiseExtendedKeysStateChanged();
		try
		{
			var ok = await _usbHidDeviceService.SetExtendedKeysAsync(device.VendorId, device.ProductId, normalized);
			if (!ReferenceEquals(Device, device))
				return false;

			if (!ok)
			{
				ExtendedKeysWriteFailed = true;
				await LoadExtendedKeysFromDeviceAsync(clearWriteFailure: false);
				// The re-read is unconditional: whatever else is true, the section must show what the
				// board actually holds. Persisting it is NOT — that is what `persist` decides. A
				// user-initiated edit that failed leaves the owner's rows mirroring the board, which
				// is right. A preset-driven push that failed must not, because its source of truth is
				// the owner's rows and the board may be wearing ANOTHER account's records: copying
				// them into this owner's rows (and marking their preset dirty) is one Save Preset away
				// from syncing someone else's configuration into this account.
				if (persist && ReferenceEquals(Device, device))
					await PersistExtendedKeysStateAsync();
				return false;
			}

			ExtendedKeysWriteFailed = false;
			if (_extendedKeysInfo is { } info)
			{
				int before = (_extendedKeysOnDevice ?? []).Sum(r => ExtendedKeyRecordBytes(r.Mode));
				int after = normalized.Sum(r => ExtendedKeyRecordBytes(r.Mode));
				_extendedKeysInfo = info with { Entries = normalized, FreeBytes = info.FreeBytes + before - after };
			}
			_extendedKeysOnDevice = normalized;
			// The set is now one the App chose and the board confirmed, whatever an earlier read had
			// to do to be answerable — so it is the owner's state again and may be persisted.
			_extendedKeysReadWasRepaired = false;
			// The matrix changed under the key-assignment mirror (pointers written, factory triplets
			// restored): the next key push must re-read rather than diff against stale records.
			_assignments = null;
			RebuildExtendedKeyRows();
			if (persist)
				await PersistExtendedKeysStateAsync();
			return true;
		}
		finally
		{
			_extendedKeysWriteInFlight = false;
			RaiseExtendedKeysStateChanged();
		}
	}

	// ── Preset persistence ────────────────────────────────────────────────────
	// Only what the board confirmed reaches the live rows, so SQLite can never claim a record the
	// hardware refused. StagePresetDirtyOnly, not NotifyPresetChanged: the rows are already persisted
	// and this write path has no debounce to schedule — the recompute alone turns Save Preset on or
	// back off (add then delete the same pair returns the preset to clean).
	private async Task PersistExtendedKeysStateAsync()
	{
		var device = Device;
		var owner = _ownerProvider?.GetCurrent();
		var localStorage = _localStorage;
		if (device is null || localStorage is null || owner is null
			|| _extendedKeysSupported != true || _extendedKeysOnDevice is not { } onDevice)
			return;

		// Never the wreckage of a repair. The path that reaches here with one is the re-read after a
		// REFUSED write (WriteExtendedKeysAsync's persist branch): one lost or NAK'd packet during a
		// write leaves the board torn, the next read repairs it by resetting every advanced key, and
		// persisting that empty result would copy the loss into the owner's rows, where the next
		// Save Preset syncs it to the server and no re-read can undo it. Refusing leaves the rows as
		// they were — still the user's configuration, and still what the next open pushes back onto
		// the board (review round 4, m3). The section meanwhile shows the repaired board, which is
		// the truth about the hardware; the two disagreeing is exactly what the banner is for.
		if (_extendedKeysReadWasRepaired)
		{
			_logger?.LogWarning(
				"[EXTKEY] Refusing to persist a read the host had to repair — the owner's rows keep their previous set");
			return;
		}

		// On the storage executor, never inline. ReplaceDeviceExtendedKeys takes the storage
		// semaphore with a NON-async Wait() on the calling thread and then runs a DELETE plus one
		// INSERT per record inside a transaction — and every caller of this method is on the
		// Avalonia dispatcher, so Confirm / trash / DEFAULT SETTINGS parked the window behind
		// whatever the executor happened to be in the middle of (a sign-in pull inserting macros, a
		// preset snapshot save). Write twin of the read in ApplyStoredExtendedKeysToDeviceAsync
		// (review round 3, S9). Captured into locals first: the delegate runs later, off this
		// thread, and must write the pair this call decided on rather than whatever Device holds by
		// then.
		await localStorage.RunOnStorageAsync(() => localStorage.ReplaceDeviceExtendedKeys(
			owner.Value.Kind, owner.Value.Id,
			device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey,
			onDevice));

		// The queued write above is this method's only yield point, and the dirty flag below belongs
		// to whatever page is open NOW: a device or account switch landing while it queued would
		// otherwise light Save Preset on the new board's preset for a write that belongs to the old
		// one. The rows themselves are addressed to the captured pair, so that write still stands.
		if (!ReferenceEquals(Device, device) || !Nullable.Equals(owner, _ownerProvider?.GetCurrent()))
			return;

		StagePresetDirtyOnly();
	}

	/// <summary>
	/// Pushes the stored set to the board as part of a preset apply (switch, save, Discard, the
	/// default-preset push on first connect). An empty set is a valid opinion ("none") — unlike
	/// actuation there is no "unset" state — so a preset without records removes the board's managed
	/// records, which is also what overwrites a vendor-app configuration on the first connect after
	/// this ships (accepted in the plan). Skipped when the board already holds exactly this set.
	/// Returns true when the board was written, so the caller re-runs the key and macro pushes: the
	/// slots that just left a record went back to their factory triplets and may now need their remap.
	/// </summary>
	internal async Task<bool> ApplyStoredExtendedKeysToDeviceAsync()
	{
		var device = Device;
		var owner = _ownerProvider?.GetCurrent();
		var localStorage = _localStorage;
		if (device is null || !device.SupportsExtendedKeys || _usbHidDeviceService is null
			|| localStorage is null || owner is null)
			return false;
		// Probe pending or a hard "no": nothing is written in either case.
		if (_extendedKeysSupported != true)
			return false;

		// No probe result means the board's state is unknown, not that it is empty. Guessing here
		// would push this owner's set — or clear the board — over records nobody has read. Checked
		// BEFORE the storage read below, so the unknown-board case costs nothing: there is no
		// baseline to compare against, so the rows would be read only to be thrown away.
		//
		// Belt-and-braces, and deliberately so: today the gate above already covers it, because the
		// probe sets _extendedKeysSupported and _extendedKeysOnDevice together, so "supported" cannot
		// be true while this is null. No test pins this line for that reason. It is here because the
		// method became a self-heal entry point that runs on EVERY open, and the cost of the two
		// fields ever drifting apart is a write over a board nobody read.
		if (_extendedKeysOnDevice is null)
			return false;

		// On the storage executor, never inline. LocalStorageService serialises its callers behind
		// a NON-async Wait() taken on the calling thread, and this method's callers run on the
		// Avalonia dispatcher — so a direct read parks the UI behind whatever long write the
		// executor happens to be in the middle of (a sign-in pull inserting macros, a preset
		// snapshot save). That was tolerable while only preset actions came through here; the
		// self-heal in SetDeviceAsync's chain makes it every open of an extended-keys keyboard.
		// Queuing also puts the read behind the writes this open already enqueued, the same FIFO
		// reason RecomputeUnsavedPresetChangesAsync gives.
		var rows = await localStorage.RunOnStorageAsync<List<ExtendedKeyRecord>?>(
			() => ExtendedKeyRecord.NormalizeSet(localStorage.GetDeviceExtendedKeys(
				owner.Value.Kind, owner.Value.Id,
				device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey)));
		// Storage torn down mid-shutdown yields null without running the delegate (IsShutDown's
		// contract). Nothing was read, so nothing is known to differ — and a board write on the way
		// out is the last thing wanted here.
		if (rows is null)
			return false;

		// The read above is the method's first yield point, so re-take the pair it decides on: a
		// device switch or an account switch landing while it was queued would otherwise compare
		// the device/owner captured at entry against a baseline the new open's probe has since
		// replaced. SetDeviceAsync fences its own call with Stale(); the preset push paths have no
		// such fence, and this covers them too.
		if (!ReferenceEquals(Device, device)
			|| !Nullable.Equals(owner, _ownerProvider?.GetCurrent())
			|| _extendedKeysOnDevice is not { } onDevice)
			return false;

		// Drop rows this board has no key for, BEFORE the comparison below so a set that differs only
		// by such a row is correctly judged "already on the board" and writes nothing.
		//
		// SetExtendedKeys is a WHOLE-SET request, so a single record on a slot the board's matrix does
		// not have makes the host refuse every record the user really configured — permanently and
		// invisibly, since preset pushes use persist:false and so keep the poisoned row, the section
		// renders the board rather than the rows, and the user sees only a write-failure banner
		// (review round 3, S10). The API intake cannot catch this one: it validates the shapes it can
		// judge without knowing which board the preset will land on. Here the App does know — the
		// per-model key catalog behind PhysicalKeys is the same factory-table slot numbering the
		// pointers use (see ResolveMatrixSlot, which is why this family is asked for nothing else),
		// and it is stricter than the host's range check: an unused placeholder inside the matrix is
		// dropped too, and a record there could never have fired.
		rows = DropRowsOffThisBoard(rows);

		// The board already holds exactly this — flash wear for no effect.
		if (rows.SequenceEqual(onDevice))
			return false;

		// An apply supersedes an edit in progress: it belongs to the preset being left, and the set
		// about to land is not the one the open editor was composed against.
		//
		// Here rather than at the top of the method, which is where it used to be. This method became
		// the self-heal and now runs at the END of EVERY open of an extended-keys board, while the
		// editor is usable as soon as the probe lands — so a cancel before the guards closed whatever
		// the user had started, on every open, in the overwhelmingly common case where the heal agrees
		// with the board and writes nothing at all (review round 4, m5). The preset path is unaffected
		// either way: ApplyPresetToDeviceCoreAsync reloads the key layout first, and
		// ResetKeyLayoutState cancels the editor there before this is ever reached.
		CancelExtendedKeyEditor();

		return await WriteExtendedKeysAsync(rows, persist: false);
	}
}
