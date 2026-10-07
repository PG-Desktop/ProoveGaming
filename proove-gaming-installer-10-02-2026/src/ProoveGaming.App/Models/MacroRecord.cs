using System.Collections.Generic;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.App.Models;

// A user macro in the App library (SQLite). This is the source of truth; when a macro is
// assigned to a button it is converted to a gRPC MacroDefinition and sent inline to the host.
public sealed class MacroRecord
{
	public int Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public int RepeatCount { get; set; } = 1;
	public MacroRepeatMode Mode { get; set; } // proto enum; default = play N times
	public List<MacroEventRecord> Events { get; set; } = [];
	// Client-generated stable identity for server-side sync (API v2.0); same semantics as
	// PresetRecord.Uid — assigned at insert, immutable, survives the guest → user rekey.
	public string Uid { get; set; } = string.Empty;
	// The server's updated_at echoed verbatim as an opaque string; null = never synced.
	public string? ServerUpdatedAt { get; set; }
	// Live-sync tracking, read-side only (docs/plans/add-api-v2-live-sync): IsDirty marks
	// content the server has not confirmed; LocalRev increments on every mutation so a push
	// can clear the flag only if the row was not re-edited mid-flight. Written exclusively
	// by LocalStorageService's mutators and ClearMacroDirty.
	public bool IsDirty { get; set; }
	public long LocalRev { get; set; }
}

// One recorded step. KeyCode = USB HID keyboard code, or mouse-button code F0-F4.
public sealed class MacroEventRecord
{
	public byte KeyCode { get; set; }
	public bool IsPress { get; set; }
	public int DelayMs { get; set; }
}
