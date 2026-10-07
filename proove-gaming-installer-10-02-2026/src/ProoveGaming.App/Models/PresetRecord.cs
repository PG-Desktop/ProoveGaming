namespace ProoveGaming.App.Models;

// A named snapshot of the current device configuration (per owner, per device type — VID/PID;
// shared across every physical unit of that model the owner has).
// The label pair (name / description) plus the IsDefault flag lives on this record; the
// bundled settings (RGB / RGB effect / button bindings) live in the preset_* snapshot
// tables and are copied over via LocalStorageService.SavePresetSnapshot / LoadPresetSnapshotToDevice.
//
// Owner scope (OwnerKind / OwnerId) is populated on read by LocalStorageService.GetPresets.
// On insert the scope is taken from method parameters — the record's fields are ignored so
// the caller can't accidentally cross-post between owners by mutating the object. Guest
// sessions carry a UUID in OwnerId; signed-in users carry their customer id (see
// MigrateGuestToUserAsync for the rekey step that flips OwnerKind from 'guest' to 'user').
public sealed class PresetRecord
{
	public int Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	// Every (owner, device type) tuple has exactly one row with IsDefault=true. The default
	// preset cannot be deleted by the user; it is auto-created when a device is opened
	// with an empty preset list. See LocalStorageService.PromotePresetToDefault.
	public bool IsDefault { get; set; }
	// Owner scope. Populated on read; ignored on insert (the InsertPreset parameters win).
	public string OwnerKind { get; set; } = string.Empty;
	public string OwnerId { get; set; } = string.Empty;
	// Client-generated stable identity for server-side sync (API v2.0): a lowercase 36-char
	// GUID assigned at insert and never changed afterwards — the guest → user rekey only
	// rewrites owner columns. Inserts honour a non-empty value on the record (tests) and
	// generate one otherwise. See docs/plans/add-api-v2-sync.
	public string Uid { get; set; } = string.Empty;
	// The server's updated_at echoed verbatim as an opaque string (the contract's timestamp
	// format carries no timezone, so it must never be parsed); null = never synced. Written
	// only by sync code via LocalStorageService.SetPresetServerUpdatedAt.
	public string? ServerUpdatedAt { get; set; }
	// Live-sync tracking, read-side only (docs/plans/add-api-v2-live-sync): IsDirty marks
	// content the server has not confirmed; LocalRev increments on every mutation so a push
	// can clear the flag only if the row was not re-edited mid-flight. Written exclusively
	// by LocalStorageService's mutators and ClearPresetDirty.
	public bool IsDirty { get; set; }
	public long LocalRev { get; set; }
}
