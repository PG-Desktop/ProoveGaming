using ProoveGaming.App.Models;
using ProoveGaming.GrpcDependencies.Device;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

public interface IUsbHidDeviceService
{
    List<HidDeviceResponse> GetDevices();
    Task StartCommunication();
    bool CheckIfAllDevicesAreDisconnected();
    void StopCommunication();
    Task<(int batteryValue, bool isCharging)> GetBatteryLevelAsync(int vendorId, int productId, string deviceKey);

    // deviceKey selects a specific physical unit among identical (VID,PID) devices; pass "" for
    // the legacy single-device behaviour (the DeviceHost then targets the first matching unit).
    Task<bool> SetDeviceLedColorAsync(int vendorId, int productId, string deviceKey, string deviceType, byte r, byte g, byte b, byte effect, byte brightness, byte speed);

    Task<bool> SetDpiStagesAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int dpi, byte r, byte g, byte b)> stages);

    Task<bool> SetActiveDpiStageAsync(int vendorId, int productId, string deviceKey, byte stageIndex);

    Task<bool> SetReportRateAsync(int vendorId, int productId, string deviceKey, int hz);

    // Assign functions to the remappable buttons (full desired set); host writes to the unit.
    // macro is set only when action == Macro (its definition rides inline for upload).
    Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings);

    // Apply a mouse's full desired configuration in ONE coordinated device write. Each aspect is
    // optional (null → left untouched). Used by the preset-apply path instead of the per-aspect calls.
    Task<bool> ApplyDeviceConfigurationAsync(
        int vendorId, int productId, string deviceKey,
        (byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
        IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
        (byte debounce, byte liftOff)? mouseSettings,
        IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
        int? activeStage,
        int? reportRateHz);

    // Upload the full keyboard macro library (App SQLite is the source of truth). Called on
    // device connect and on any add/edit/delete of a library macro. Unlike a mouse — where a
    // macro rides inline on its ButtonBinding — a keyboard stores an indexed library that key
    // slots point into, so it has to be uploaded as a unit. Bindings are done separately via
    // SetKeyMappingAsync with the family's macro type byte (Raster 0x60/0x61, Smart Infinite
    // 0x50/0x51, T800 0x09). RepeatCount matters on T800 only — that family stores the count
    // in the macro blob; the other two carry it in the binding's code2 and ignore this field.
    Task<bool> SetMacrosAsync(
        int vendorId, int productId, string deviceKey,
        IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries);

    // Last active DPI stage the DeviceHost reported for a device, if any. Lets a
    // device-details view that opens AFTER the stage was pushed still show the correct
    // stage (the push is a one-shot on subscribe, before the user navigates in).
    bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex);

    // Last report (polling) rate in Hz the DeviceHost reported for a device, if any.
    bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz);

    Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2);

    Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?>
        ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128);

    // Slot-keyed read of the device's key matrix. Unlike ProbeKeyMatrixAsync this keeps the whole
    // record, which is the only way to represent modifier keys — they carry their identity in the
    // modifier bitmask with a 0x00 scancode. Null means the read failed; never treat it as an empty
    // matrix. Used by the Smart Infinite family, whose base matrix IS the live state.
    Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?>
        ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128);

    // Magnetic-keyboard actuation (key press force). Null means the RPC failed; Supported false
    // means the board has no actuation (mechanical or a failed probe) and SetKeyActuationAsync
    // must not be called for it. All travel values in hundredths of a millimetre (2.00 mm = 200).
    Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId);

    // Applies one set of actuation values to every key; the keyboard's flash persists them.
    Task<bool> SetKeyActuationAsync(
        int vendorId, int productId,
        int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm);

    // Applies one set of actuation values to the listed key slots only (same slot space as
    // KeyActuationInfo.PerKeyValues), leaving every other key untouched. Only callable when the
    // host reported per-key support (a non-empty PerKeyValues map).
    Task<bool> SetPerKeyActuationAsync(
        int vendorId, int productId, IReadOnlyCollection<int> slots,
        int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm);

    // Extended Keys (Rapid Snap / Instant Release / Mod-Tap / Toggle) of a Smart Infinite keyboard.
    // Null means the RPC failed (including UNIMPLEMENTED from an older DeviceHost) — the section
    // stays in its loading state; Supported false is the board's own "no" and SetExtendedKeysAsync
    // must not be called for it.
    Task<ExtendedKeysInfo?> GetExtendedKeysAsync(int vendorId, int productId);

    // Replaces the board's managed Extended Keys with the FULL desired set (empty = none). The host
    // rebuilds the storage area and the key matrix, writes both and verifies by read-back.
    Task<bool> SetExtendedKeysAsync(int vendorId, int productId, IReadOnlyList<ExtendedKeyRecord> entries);

    event EventHandler<SubscribeOnDevicesResponse> DeviceFound;
    event EventHandler<DpiStageChange> DpiStageChanged;
    event EventHandler<ReportRateChange> ReportRateChanged;

    // Battery pushed by the DeviceHost the moment it sees it, rather than on the App's own poll.
    event EventHandler<BatteryChange> BatteryChanged;
    event EventHandler<string> NotifyError;

    // Whether the DeviceHost is answering at all, raised on the transition only. False means the
    // service was stopped, crashed or uninstalled, so every push the App is still showing is stale;
    // true means a fresh subscription is up and the host has re-sent the current device lists.
    event EventHandler<bool> HostAvailabilityChanged;
}

// A magnetic keyboard's current actuation values plus the ranges they may move in, as reported by
// the DeviceHost. Travel fields are in hundredths of a millimetre; StepMicrometres is the value
// granularity (10 = 0.01 mm). The Default* fields are the board's factory values.
public sealed record KeyActuationInfo(
    bool Supported,
    int PressCentiMm,
    int ReleaseCentiMm,
    bool RapidTrigger,
    int QuickPressCentiMm,
    int QuickReleaseCentiMm,
    int PressMinCentiMm,
    int PressMaxCentiMm,
    int RtMinCentiMm,
    int RtMaxCentiMm,
    int StepMicrometres,
    int DefaultPressCentiMm,
    int DefaultReleaseCentiMm,
    bool DefaultRapidTrigger,
    int DefaultQuickPressCentiMm,
    int DefaultQuickReleaseCentiMm)
{
    // Current values per populated key slot. An EMPTY map means the DeviceHost predates per-key
    // actuation — the App hides per-key editing then, never treats it as "no keys".
    public IReadOnlyDictionary<int, KeyActuationKeyValues> PerKeyValues { get; init; } =
        ReadOnlyDictionary<int, KeyActuationKeyValues>.Empty;
}

// One key slot's actuation values, in hundredths of a millimetre.
public sealed record KeyActuationKeyValues(
    int PressCentiMm,
    int ReleaseCentiMm,
    bool RapidTrigger,
    int QuickPressCentiMm,
    int QuickReleaseCentiMm);

// A keyboard's Extended Keys as reported by the DeviceHost: the managed records on the board, the
// storage-area size and free bytes (an RS record costs 8), and the slots owned by records the host
// preserves but does not manage — those cannot join a new record.
public sealed record ExtendedKeysInfo(
    bool Supported,
    IReadOnlyList<ExtendedKeyRecord> Entries,
    int AreaBytes,
    int FreeBytes,
    IReadOnlyList<int> BlockedSlots)
{
    /// <summary>
    /// True when the host found the board incoherent and RESET its advanced keys before it could
    /// answer (<c>GetExtendedKeysResponse.repaired</c>) — what a torn two-region write leaves behind.
    /// <see cref="Entries"/> is then what survived that reset, not a configuration anybody chose, so
    /// it must be shown but never saved as the owner's state.
    /// </summary>
    // An init property rather than a sixth positional parameter: it defaults to false, which is the
    // right reading for a DeviceHost that predates the field and for every existing construction.
    public bool Repaired { get; init; }
}
