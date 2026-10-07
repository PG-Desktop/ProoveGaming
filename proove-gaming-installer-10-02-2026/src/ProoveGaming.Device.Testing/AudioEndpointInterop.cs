using System.Runtime.InteropServices;

namespace ProoveGaming.Device.Testing;

// Core Audio enumeration shared by the audio probes: every endpoint with its friendly name, its
// DEVPKEY_Device_ContainerId, its state and its current level.
//
// Container ID is the identity that matters here. `IAudioEndpointService` addresses endpoints by it
// on every method — never by name — so any question of the form "can the App drive this device's
// volume?" reduces to "does an endpoint report the container we know the device by?".
//
// StarlightAudioProbe still carries its own private copy of these declarations. It was left alone
// deliberately rather than refactored blind: it is a working diagnostic for hardware not on this
// desk. Fold it into this file the next time that probe is touched with the device present.
internal static class AudioEndpointInterop
{
	internal const int DataFlowRender = 0;
	internal const int DataFlowCapture = 1;

	// Every state, not just ACTIVE: an endpoint that has gone UNPLUGGED or DISABLED is often exactly
	// what a probe is looking for, and filtering it out hides the answer.
	private const int StateAll = 0x0F;
	private const uint ClsCtxAll = 23;
	private const uint StgmRead = 0;
	private const ushort VtLpwstr = 31;
	private const ushort VtClsid = 72;

	internal sealed record Endpoint(
		string Direction,
		string Name,
		Guid? ContainerId,
		string State,
		string EndpointId,
		bool? Muted,
		int? VolumePercent);

	// Reads render and capture separately so each endpoint carries its direction — a headset exposes
	// both, and which profile owns the microphone is one of the things a probe has to answer.
	internal static List<Endpoint> ReadAll()
	{
		var all = new List<Endpoint>();
		all.AddRange(Read(DataFlowRender, "render"));
		all.AddRange(Read(DataFlowCapture, "capture"));
		return all;
	}

	private static List<Endpoint> Read(int dataFlow, string direction)
	{
		var results = new List<Endpoint>();
		IMMDeviceEnumerator? enumerator = null;
		IMMDeviceCollection? collection = null;

		try
		{
			var clsid = ClsidMMDeviceEnumerator;
			var iid = typeof(IMMDeviceEnumerator).GUID;
			if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var raw) != 0 || raw is null)
				return results;

			enumerator = (IMMDeviceEnumerator)raw;
			if (enumerator.EnumAudioEndpoints(dataFlow, StateAll, out collection) != 0 || collection is null)
				return results;
			if (collection.GetCount(out var count) != 0)
				return results;

			for (uint i = 0; i < count; i++)
			{
				if (collection.Item(i, out var device) != 0 || device is null) continue;

				try
				{
					var name = ReadFriendlyName(device) ?? "(no name)";
					device.GetState(out var state);
					device.GetId(out var endpointId);

					bool? muted = null;
					int? volume = null;
					var volIid = typeof(IAudioEndpointVolume).GUID;
					if (device.Activate(ref volIid, ClsCtxAll, IntPtr.Zero, out var volRaw) == 0 &&
						volRaw is IAudioEndpointVolume vol)
					{
						if (vol.GetMute(out var m) == 0) muted = m;
						if (vol.GetMasterVolumeLevelScalar(out var scalar) == 0)
							volume = (int)Math.Round(scalar * 100);
						Release(vol);
					}

					results.Add(new Endpoint(direction, name, ReadContainerId(device), StateName(state),
						endpointId ?? string.Empty, muted, volume));
				}
				finally
				{
					Release(device);
				}
			}
		}
		catch
		{
			// An endpoint disappearing mid-enumeration is ordinary while a device powers down.
		}
		finally
		{
			Release(collection);
			Release(enumerator);
		}

		return results;
	}

	// DEVPKEY_Device_ContainerId is a VT_CLSID, whose PROPVARIANT holds a POINTER to the GUID rather
	// than the GUID inline. Mirrors AudioEndpointService.ReadContainerId.
	private static Guid? ReadContainerId(IMMDevice device)
	{
		IPropertyStore? store = null;
		try
		{
			if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null) return null;

			var key = DevpkeyDeviceContainerId;
			if (store.GetValue(ref key, out var value) != 0) return null;

			try
			{
				if (value.Vt != VtClsid || value.Data == IntPtr.Zero) return null;
				return Marshal.PtrToStructure<Guid>(value.Data);
			}
			finally
			{
				PropVariantClear(ref value);
			}
		}
		catch { return null; }
		finally { Release(store); }
	}

	private static string? ReadFriendlyName(IMMDevice device)
	{
		IPropertyStore? store = null;
		try
		{
			if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null) return null;

			var key = PkeyDeviceFriendlyName;
			if (store.GetValue(ref key, out var value) != 0) return null;

			try
			{
				return value.Vt == VtLpwstr && value.Data != IntPtr.Zero
					? Marshal.PtrToStringUni(value.Data)
					: null;
			}
			finally
			{
				PropVariantClear(ref value);
			}
		}
		catch { return null; }
		finally { Release(store); }
	}

	private static string StateName(uint state) => state switch
	{
		0x1 => "ACTIVE",
		0x2 => "DISABLED",
		0x4 => "NOTPRESENT",
		0x8 => "UNPLUGGED",
		_ => $"0x{state:X}",
	};

	private static void Release(object? com)
	{
		if (com is not null && OperatingSystem.IsWindows())
			Marshal.ReleaseComObject(com);
	}

	private static Guid ClsidMMDeviceEnumerator => new("BCDE0395-E52F-467C-8E3D-C4579291692E");

	private static PropertyKey DevpkeyDeviceContainerId => new()
	{
		Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
		Pid = 2,
	};

	private static PropertyKey PkeyDeviceFriendlyName => new()
	{
		Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
		Pid = 14,
	};

	[StructLayout(LayoutKind.Sequential)]
	private struct PropertyKey
	{
		public Guid Fmtid;
		public uint Pid;
	}

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
	private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
		ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PropVariant pvar);

	// PreserveSig so failures return HRESULTs; vtable order must stay exact, and each interface is
	// truncated after the last method used.
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
		[PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
		[PreserveSig] int GetState(out uint state);
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
		// Never called, but they occupy vtable slots 8-11; GetMute must land on slot 13.
		[PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
		[PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
		[PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
		[PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
		[PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
		[PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
	}
}
