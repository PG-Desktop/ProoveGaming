using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

// Reads and writes the master volume of the Windows audio endpoints belonging to ONE physical
// device, identified by its Container ID (the same key the rest of the app uses for a device).
//
// This lives in the App, not the DeviceHost, on purpose: the DeviceHost is registered with
// AddWindowsService and runs in session 0, which cannot reach the interactive user's audio
// endpoints. Routing volume through it would either fail or address the wrong session.
//
// Every method returns null / does nothing when the device has no matching endpoint — a headset
// that is unplugged, or a device with no audio interfaces. It must never fall back to the DEFAULT
// endpoint: that would move the user's speakers instead of their headset.
public interface IAudioEndpointService
{
	// 0-100, or null when this device has no matching render (playback) endpoint.
	Task<int?> GetOutputVolumeAsync(string containerId, CancellationToken ct = default);

	// 0-100, or null when this device has no matching capture (microphone) endpoint.
	Task<int?> GetMicrophoneVolumeAsync(string containerId, CancellationToken ct = default);

	Task SetOutputVolumeAsync(string containerId, int percent, CancellationToken ct = default);

	Task SetMicrophoneVolumeAsync(string containerId, int percent, CancellationToken ct = default);

	// Mute is separate from a zero volume: Windows tracks the two independently, and the headset's
	// own mute button moves this without touching the level. Null when no endpoint matched.
	Task<bool?> GetOutputMutedAsync(string containerId, CancellationToken ct = default);

	Task<bool?> GetMicrophoneMutedAsync(string containerId, CancellationToken ct = default);

	// Level AND mute from ONE endpoint resolution. Every other getter here costs a full
	// CoCreateInstance + EnumAudioEndpoints + a property-store read per enumerated endpoint, so a
	// poller that wants both was paying for two enumerations to learn one endpoint's state.
	Task<AudioEndpointState> GetOutputStateAsync(string containerId, CancellationToken ct = default);

	Task<AudioEndpointState> GetMicrophoneStateAsync(string containerId, CancellationToken ct = default);
}

// Null members mean "no endpoint resolved" — unknown, not zero and not unmuted. Callers must treat
// that as "leave it alone" rather than as a value.
public readonly record struct AudioEndpointState(int? VolumePercent, bool? Muted);
