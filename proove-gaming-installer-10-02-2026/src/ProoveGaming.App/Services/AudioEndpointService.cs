using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ProoveGaming.App.Services;

// Core Audio (MMDevice) interop, hand-rolled to match the existing CfgMgr32 P/Invoke convention in
// Infrastructure/Services/DeviceContainerId.cs rather than take a NuGet dependency for three
// interfaces.
//
// Two rules shape this file:
//
//  * Endpoints are matched by CONTAINER ID and never by name, and a failed match returns null
//    rather than the default endpoint. Falling back to the default would quietly move the user's
//    speakers when their headset is unplugged, which is worse than doing nothing.
//  * Every COM object is released on every path, including failures. A leaked IAudioEndpointVolume
//    pins an endpoint for the lifetime of the process, so each call resolves, uses and releases
//    instead of caching across calls.
//
// The App multi-targets net10.0 as well as net10.0-windows, so every entry point is guarded by
// OperatingSystem.IsWindows() and returns a no-op elsewhere.
public sealed class AudioEndpointService : IAudioEndpointService
{
	private readonly ILogger<AudioEndpointService>? _logger;

	// Container IDs already reported as unresolvable. Endpoint resolution runs on a poll, so an
	// unmatched device would otherwise log the same warning several times a second forever; the
	// first miss is worth shouting about, the thousandth is not.
	private readonly ConcurrentDictionary<string, byte> _reportedMisses = new(StringComparer.OrdinalIgnoreCase);

	public AudioEndpointService(ILogger<AudioEndpointService>? logger = null) => _logger = logger;

	private const int DataFlowRender = 0;
	private const int DataFlowCapture = 1;
	private const int DeviceStateActive = 0x1;
	private const uint ClsCtxAll = 23;
	private const ushort VtClsid = 72;

	public Task<int?> GetOutputVolumeAsync(string containerId, CancellationToken ct = default)
		=> Task.Run(() => GetVolume(containerId, DataFlowRender), ct);

	public Task<int?> GetMicrophoneVolumeAsync(string containerId, CancellationToken ct = default)
		=> Task.Run(() => GetVolume(containerId, DataFlowCapture), ct);

	public Task SetOutputVolumeAsync(string containerId, int percent, CancellationToken ct = default)
		=> Task.Run(() => SetVolume(containerId, DataFlowRender, percent), ct);

	public Task SetMicrophoneVolumeAsync(string containerId, int percent, CancellationToken ct = default)
		=> Task.Run(() => SetVolume(containerId, DataFlowCapture, percent), ct);

	public Task<bool?> GetOutputMutedAsync(string containerId, CancellationToken ct = default)
		=> Task.Run(() => GetMuted(containerId, DataFlowRender), ct);

	public Task<bool?> GetMicrophoneMutedAsync(string containerId, CancellationToken ct = default)
		=> Task.Run(() => GetMuted(containerId, DataFlowCapture), ct);

	public Task<AudioEndpointState> GetOutputStateAsync(string containerId, CancellationToken ct = default)
		=> Task.Run(() => GetState(containerId, DataFlowRender), ct);

	public Task<AudioEndpointState> GetMicrophoneStateAsync(string containerId, CancellationToken ct = default)
		=> Task.Run(() => GetState(containerId, DataFlowCapture), ct);

	// One resolution, both values — see the interface comment.
	private AudioEndpointState GetState(string containerId, int dataFlow)
	{
		var volume = TryResolveEndpointVolume(containerId, dataFlow);
		if (volume is null) return new AudioEndpointState(null, null);

		try
		{
			int? percent = volume.GetMasterVolumeLevelScalar(out var scalar) == 0
				? (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f)
				: null;
			bool? muted = volume.GetMute(out var isMuted) == 0 ? isMuted : null;
			return new AudioEndpointState(percent, muted);
		}
		catch (COMException ex)
		{
			_logger?.LogDebug(ex, "[Audio] endpoint vanished while reading state for {ContainerId}", containerId);
			return new AudioEndpointState(null, null);
		}
		finally
		{
			Release(volume);
		}
	}

	private int? GetVolume(string containerId, int dataFlow)
	{
		var volume = TryResolveEndpointVolume(containerId, dataFlow);
		if (volume is null) return null;

		try
		{
			return volume.GetMasterVolumeLevelScalar(out var scalar) == 0
				? (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f)
				: null;
		}
		catch (COMException)
		{
			// The endpoint can disappear between resolving it and reading it (the user unplugged
			// the headset); that is a no-value, not an error worth surfacing.
			return null;
		}
		finally
		{
			Release(volume);
		}
	}

	private void SetVolume(string containerId, int dataFlow, int percent)
	{
		var volume = TryResolveEndpointVolume(containerId, dataFlow);
		if (volume is null) return;

		try
		{
			var guid = Guid.Empty;
			volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref guid);
		}
		catch (COMException)
		{
			// Same as above: an endpoint that vanished mid-call is not a failure the user can act on.
		}
		finally
		{
			Release(volume);
		}
	}

	private bool? GetMuted(string containerId, int dataFlow)
	{
		var volume = TryResolveEndpointVolume(containerId, dataFlow);
		if (volume is null) return null;

		try
		{
			return volume.GetMute(out var muted) == 0 ? muted : null;
		}
		catch (COMException)
		{
			return null;
		}
		finally
		{
			Release(volume);
		}
	}

	// Every COM release goes through here. The OperatingSystem.IsWindows() check is not decoration:
	// the App also targets plain net10.0, where Marshal.ReleaseComObject is not a supported call.
	private static void Release(object? com)
	{
		if (com is not null && OperatingSystem.IsWindows())
			Marshal.ReleaseComObject(com);
	}

	// Walks the active endpoints of one direction and returns the volume interface of the first
	// whose container matches. Null when nothing matches — deliberately NOT the default endpoint.
	private IAudioEndpointVolume? TryResolveEndpointVolume(string containerId, int dataFlow)
	{
		var direction = dataFlow == DataFlowRender ? "render" : "capture";

		if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(containerId))
			return null;

		if (!Guid.TryParse(containerId, out var wanted))
		{
			ReportMiss(containerId, direction, "the device key is not a GUID, so no endpoint can be matched");
			return null;
		}

		IMMDeviceEnumerator? enumerator = null;
		IMMDeviceCollection? collection = null;
		try
		{
			var clsid = ClsidMMDeviceEnumerator;
			var iid = typeof(IMMDeviceEnumerator).GUID;
			if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var raw) != 0 || raw is null)
			{
				ReportMiss(containerId, direction, "CoCreateInstance(MMDeviceEnumerator) failed");
				return null;
			}

			enumerator = (IMMDeviceEnumerator)raw;
			if (enumerator.EnumAudioEndpoints(dataFlow, DeviceStateActive, out collection) != 0 || collection is null)
			{
				ReportMiss(containerId, direction, "EnumAudioEndpoints failed");
				return null;
			}

			if (collection.GetCount(out var count) != 0)
			{
				ReportMiss(containerId, direction, "IMMDeviceCollection.GetCount failed");
				return null;
			}

			// Counted so a miss can say whether NO endpoint reported a container (the property is
			// absent on this configuration) or whether they all reported a DIFFERENT one (the device
			// key is wrong). Those need completely different fixes and are indistinguishable without it.
			var withContainer = 0;

			for (uint i = 0; i < count; i++)
			{
				if (collection.Item(i, out var device) != 0 || device is null)
					continue;

				try
				{
					var container = ReadContainerId(device);
					if (container is not null) withContainer++;
					if (container != wanted)
						continue;

					var volumeIid = typeof(IAudioEndpointVolume).GUID;
					if (device.Activate(ref volumeIid, ClsCtxAll, IntPtr.Zero, out var activated) != 0 || activated is null)
					{
						ReportMiss(containerId, direction, "the matching endpoint would not activate IAudioEndpointVolume");
						continue;
					}

					_logger?.LogDebug("[Audio] resolved {Direction} endpoint for {ContainerId}", direction, containerId);
					_reportedMisses.TryRemove(MissKey(containerId, direction), out _);
					return (IAudioEndpointVolume)activated;
				}
				finally
				{
					Release(device);
				}
			}

			ReportMiss(containerId, direction,
				$"none of {count} active {direction} endpoints matched; {withContainer} reported a container id at all");
			return null;
		}
		catch (COMException ex)
		{
			_logger?.LogDebug(ex, "[Audio] COM failure resolving the {Direction} endpoint for {ContainerId}",
				direction, containerId);
			return null;
		}
		finally
		{
			Release(collection);
			Release(enumerator);
		}
	}

	// Warn once per (container, direction), then stay quiet: this runs on a poll, so an unmatched
	// device would otherwise fill the log several times a second. The first miss is the one that
	// tells QA the Audio section is inert rather than the volume simply not moving.
	private void ReportMiss(string containerId, string direction, string reason)
	{
		if (_reportedMisses.TryAdd(MissKey(containerId, direction), 0))
		{
			_logger?.LogWarning(
				"[Audio] no {Direction} endpoint for device {ContainerId}: {Reason}. " +
				"Volume changes for this device will do nothing until it resolves.",
				direction, containerId, reason);
		}
		else
		{
			_logger?.LogDebug("[Audio] still no {Direction} endpoint for {ContainerId}: {Reason}",
				direction, containerId, reason);
		}
	}

	private static string MissKey(string containerId, string direction) => $"{containerId}|{direction}";

	// DEVPKEY_Device_ContainerId comes back as VT_CLSID: a PROPVARIANT whose data pointer addresses
	// the GUID. Anything else means this endpoint does not report a container and cannot be matched.
	private Guid? ReadContainerId(IMMDevice device)
	{
		IPropertyStore? store = null;
		var variant = default(PropVariant);
		try
		{
			if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null)
				return null;

			var key = DevpkeyDeviceContainerId;
			if (store.GetValue(ref key, out variant) != 0)
				return null;

			if (variant.Vt != VtClsid || variant.Data == IntPtr.Zero)
				return null;

			return Marshal.PtrToStructure<Guid>(variant.Data);
		}
		catch (COMException ex)
		{
			_logger?.LogDebug(ex, "[Audio] could not read DEVPKEY_Device_ContainerId from an endpoint");
			return null;
		}
		finally
		{
			PropVariantClear(ref variant);
			Release(store);
		}
	}

	private const uint StgmRead = 0;

	private static Guid ClsidMMDeviceEnumerator => new("BCDE0395-E52F-467C-8E3D-C4579291692E");

	// Same key DeviceContainerId reads through CfgMgr32; the MMDevice property store exposes the
	// DEVPKEY_* set alongside its own PKEY_*, so one property read is enough here.
	private static PropertyKey DevpkeyDeviceContainerId => new()
	{
		Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
		Pid = 2,
	};

	[StructLayout(LayoutKind.Sequential)]
	private struct PropertyKey
	{
		public Guid Fmtid;
		public uint Pid;
	}

	// Only the leading fields are named: everything this reads is a VT_CLSID, whose GUID pointer
	// sits in the first data slot. PropVariantClear is still called so any other type that turns up
	// (a string, an allocated blob) is freed rather than leaked.
	[StructLayout(LayoutKind.Sequential)]
	private struct PropVariant
	{
		public ushort Vt;
		public ushort Reserved1;
		public ushort Reserved2;
		public ushort Reserved3;
		public IntPtr Data;
		public IntPtr Data2;
	}

	[DllImport("ole32.dll")]
	private static extern int CoCreateInstance(
		ref Guid rclsid,
		IntPtr pUnkOuter,
		uint dwClsContext,
		ref Guid riid,
		[MarshalAs(UnmanagedType.Interface)] out object? ppv);

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PropVariant pvar);

	// ── COM interfaces ────────────────────────────────────────────────────────
	// Declared with PreserveSig so failures come back as HRESULTs rather than exceptions — an
	// endpoint disappearing mid-enumeration is ordinary here, not exceptional. Each declaration is
	// truncated after the last method used; the leading vtable order is what must stay exact.

	[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
	 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		[PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
	}

	[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
	 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceCollection
	{
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int Item(uint index, out IMMDevice? device);
	}

	[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
	 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		[PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
			[MarshalAs(UnmanagedType.IUnknown)] out object? instance);
		[PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore? properties);
	}

	[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
	 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore
	{
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int GetAt(uint index, out PropertyKey key);
		[PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
	}

	[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
	 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioEndpointVolume
	{
		[PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
		[PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
		[PreserveSig] int GetChannelCount(out uint count);
		[PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
		[PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
		[PreserveSig] int GetMasterVolumeLevel(out float levelDb);
		[PreserveSig] int GetMasterVolumeLevelScalar(out float level);
		// The four channel methods are never called, but they occupy vtable slots 8-11 and must be
		// declared for GetMute below to land on slot 13 rather than on SetChannelVolumeLevel.
		[PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
		[PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
		[PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
		[PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
		[PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
		[PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
	}
}
