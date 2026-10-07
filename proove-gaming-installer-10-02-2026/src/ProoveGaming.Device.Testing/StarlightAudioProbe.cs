using System.Runtime.InteropServices;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — what does WINDOWS see, when the HID layer sees nothing? ─────────────────────
//
// The HID layer is exhausted: report 0xFD reads all zeros in every state, the device stays
// enumerated whether the headset is on or off, and only the play/pause button reports at all. So
// three questions are still open, and all three live on a different layer — Core Audio, which is
// the one part of this device that demonstrably works:
//
//   1. POWER    does the audio endpoint change state when the headset is switched off?
//               (ACTIVE -> UNPLUGGED / NOTPRESENT would give us power state for free.)
//   2. MIC MUTE does the long-press on Button 2 mute the capture endpoint as Windows sees it?
//   3. WHEEL    does the mechanical volume wheel move the render endpoint's level, or is it a
//               hardware attenuator the host never learns about?
//
// Every one of these is already reachable from the app: IAudioEndpointService reads endpoints by
// Container ID, and AudioMuteMonitor polls mute state. So a "yes" to any of them is a feature that
// costs no new protocol work at all.
//
// THE KEY THIS PROBE MATCHES ON, AND WHY IT MATTERS
//
// This probe finds endpoints by friendly name, because a name is what a human can check against
// Windows' own UI. The APP does not: AudioEndpointService.TryResolveEndpointVolume parses the
// device key as a GUID, reads DEVPKEY_Device_ContainerId from every active endpoint, and matches on
// that — returning null rather than falling back to the default device when nothing matches.
//
// Those are different keys, so finding an endpoint here does NOT prove the app can find it. If the
// audio function on MI_00 carries a different Container ID from the HID interface on MI_03, the
// Audio sliders render and silently do nothing: no error, no UI feedback, just a debug-log miss.
// That is the risk the plan records, and the whole user-facing value of this device rides on it.
//
// So the Container ID of every endpoint is read and printed alongside the name, and compared with
// the HID Container ID. The comparison is the point; the levels are incidental.
//
// READ-ONLY. Enumerates endpoints and reads state, mute and volume. Sets nothing.
internal static class StarlightAudioProbe
{
	private const string MatchName = "Starlight";

	// The HID interfaces' Container ID, measured 2026-08-07 by `starlight` / `identity`. This is the
	// value the app uses as its per-device key, so it is what the audio endpoints must carry for
	// the Audio section to bind to anything.
	private const string HidContainerId = "99e3b024-79e0-5516-ace7-150b21eb9dd0";

	private static readonly (string Label, int Seconds, string Instruction)[] Phases =
	[
		("baseline", 8,
			"Headset ON, microphone UNMUTED. Do not touch it — this is the reference."),
		("mic-mute", 14,
			"HOLD BUTTON 2 to MUTE the microphone. Leave it muted for the rest of this phase."),
		("mic-unmute", 14,
			"HOLD BUTTON 2 again to UNMUTE the microphone."),
		("wheel", 14,
			"Turn the volume wheel all the way DOWN, then back UP."),
		("power-off", 18,
			"HOLD BUTTON 1 until the headset switches OFF. Leave it off."),
		("power-on", 18,
			"Switch the headset back ON."),
	];

	private sealed record Sample(string Phase, string Endpoint, Guid? ContainerId, string State, bool? Muted, int? Volume);

	public static void Run()
	{
		Console.WriteLine("PG Starlight — Core Audio endpoint watch (READ-ONLY)");
		Console.WriteLine();

		if (!OperatingSystem.IsWindows())
		{
			Console.WriteLine("Windows only.");
			return;
		}

		var endpoints = FindEndpoints();
		if (endpoints.Count == 0)
		{
			Console.WriteLine($"No audio endpoint whose name contains \"{MatchName}\" was found.");
			Console.WriteLine("Is the dongle plugged in?");
			return;
		}

		Console.WriteLine($"{endpoints.Count} matching endpoint(s):");

		var expected = Guid.Parse(HidContainerId);
		var allMatch = true;

		foreach (var (name, container) in endpoints)
		{
			var verdict = container is null ? "NO CONTAINER ID"
				: container == expected ? "matches the HID device"
				: "DIFFERENT from the HID device";

			if (container != expected) allMatch = false;
			Console.WriteLine($"  {name}");
			Console.WriteLine($"      container {container?.ToString() ?? "(none)"}   {verdict}");
		}

		Console.WriteLine();
		Console.WriteLine($"  HID interfaces report container {expected}");
		Console.WriteLine();
		Console.WriteLine(allMatch
			? "  => The app CAN resolve these endpoints. AudioEndpointService matches on exactly this\n" +
			  "     value, so the Audio sliders will bind to the right device."
			: "  => The app CANNOT resolve these endpoints. AudioEndpointService matches on Container ID\n" +
			  "     and deliberately does not fall back to the default device, so the Audio sliders would\n" +
			  "     render and silently do nothing. This must be fixed before the Audio section ships.");
		Console.WriteLine();

		var samples = new List<Sample>();
		var phase = "startup";
		var gate = new object();

		using var cts = new CancellationTokenSource();

		var sampler = Task.Run(() =>
		{
			while (!cts.IsCancellationRequested)
			{
				foreach (var s in ReadAll())
				{
					lock (gate) samples.Add(s with { Phase = phase });
				}

				try { Task.Delay(1000, cts.Token).Wait(cts.Token); }
				catch { break; }
			}
		}, cts.Token);

		foreach (var (label, seconds, instruction) in Phases)
		{
			lock (gate) phase = label;
			Console.WriteLine(new string('-', 96));
			Console.WriteLine($"PHASE: {label}   ({seconds}s)");
			Console.WriteLine($"  {instruction}");
			Thread.Sleep(seconds * 1000);
		}

		cts.Cancel();
		sampler.Wait(TimeSpan.FromSeconds(3));

		Report(samples);
	}

	private static void Report(List<Sample> samples)
	{
		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("RESULT — distinct (state, muted, volume) per endpoint per phase");
		Console.WriteLine();

		foreach (var endpoint in samples.Select(s => s.Endpoint).Distinct())
		{
			Console.WriteLine(endpoint);

			foreach (var (label, _, _) in Phases)
			{
				var distinct = samples
					.Where(s => s.Endpoint == endpoint && s.Phase == label)
					.Select(s => $"{s.State,-10} muted={Show(s.Muted),-5} vol={Show(s.Volume)}")
					.Distinct()
					.ToList();

				Console.WriteLine($"  {label,-11} {(distinct.Count == 0 ? "(no samples)" : string.Join("  |  ", distinct))}");
			}

			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 96));
		Console.WriteLine("How to read this:");
		Console.WriteLine();
		Console.WriteLine("  POWER    state changing between power-off and power-on (ACTIVE -> UNPLUGGED or");
		Console.WriteLine("           NOTPRESENT) means the app CAN know the headset is switched off, through");
		Console.WriteLine("           Core Audio rather than HID. Nothing new to decode.");
		Console.WriteLine("  MIC MUTE muted=True on the capture endpoint during mic-mute means the card's");
		Console.WriteLine("           microphone indicator already works — AudioMuteMonitor reads exactly this.");
		Console.WriteLine("  WHEEL    vol moving during the wheel phase means our Output Volume slider and the");
		Console.WriteLine("           wheel share one control, and the slider already follows it once a second.");
		Console.WriteLine("           vol NOT moving means the wheel attenuates in hardware and the two are");
		Console.WriteLine("           independent — worth knowing before a user reports it as a bug.");
	}

	private static string Show(bool? value) => value?.ToString() ?? "?";
	private static string Show(int? value) => value?.ToString() ?? "?";

	// ── Core Audio ────────────────────────────────────────────────────────────

	private static List<(string Name, Guid? ContainerId)> FindEndpoints() =>
		[.. ReadAll().Select(s => (s.Endpoint, s.ContainerId))];

	private static List<Sample> ReadAll()
	{
		var results = new List<Sample>();
		IMMDeviceEnumerator? enumerator = null;
		IMMDeviceCollection? collection = null;

		try
		{
			var clsid = ClsidMMDeviceEnumerator;
			var iid = typeof(IMMDeviceEnumerator).GUID;
			if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var raw) != 0 || raw is null)
				return results;

			enumerator = (IMMDeviceEnumerator)raw;

			// eAll data flow, every state — an endpoint that has gone UNPLUGGED is exactly what we
			// are looking for, so filtering to ACTIVE would hide the answer.
			if (enumerator.EnumAudioEndpoints(DataFlowAll, StateAll, out collection) != 0 || collection is null)
				return results;

			if (collection.GetCount(out var count) != 0) return results;

			for (uint i = 0; i < count; i++)
			{
				if (collection.Item(i, out var device) != 0 || device is null) continue;

				try
				{
					var name = ReadFriendlyName(device);
					if (name is null || !name.Contains(MatchName, StringComparison.OrdinalIgnoreCase))
						continue;

					device.GetState(out var state);

					bool? muted = null;
					int? volume = null;

					var volIid = typeof(IAudioEndpointVolume).GUID;
					if (device.Activate(ref volIid, ClsCtxAll, IntPtr.Zero, out var volRaw) == 0 && volRaw is IAudioEndpointVolume vol)
					{
						if (vol.GetMute(out var m) == 0) muted = m;
						if (vol.GetMasterVolumeLevelScalar(out var scalar) == 0)
							volume = (int)Math.Round(scalar * 100);
						Release(vol);
					}

					results.Add(new Sample("", name, ReadContainerId(device), StateName(state), muted, volume));
				}
				finally
				{
					Release(device);
				}
			}
		}
		catch
		{
			// An endpoint disappearing mid-enumeration is ordinary while a device is powering down.
		}
		finally
		{
			Release(collection);
			Release(enumerator);
		}

		return results;
	}

	// Mirrors AudioEndpointService.ReadContainerId. DEVPKEY_Device_ContainerId is a VT_CLSID, whose
	// GUID pointer sits in the first data slot; PropVariantClear still runs so any other type that
	// turns up is freed rather than leaked.
	private static Guid? ReadContainerId(IMMDevice device)
	{
		IPropertyStore? store = null;
		var variant = default(PropVariant);
		try
		{
			if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null) return null;

			var key = DevpkeyDeviceContainerId;
			if (store.GetValue(ref key, out variant) != 0) return null;

			if (variant.Vt != VtClsid || variant.Data == IntPtr.Zero) return null;

			return Marshal.PtrToStructure<Guid>(variant.Data);
		}
		catch
		{
			return null;
		}
		finally
		{
			PropVariantClear(ref variant);
			Release(store);
		}
	}

	private static string? ReadFriendlyName(IMMDevice device)
	{
		IPropertyStore? store = null;
		var variant = default(PropVariant);
		try
		{
			if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null) return null;

			var key = PkeyDeviceFriendlyName;
			if (store.GetValue(ref key, out variant) != 0) return null;

			return variant.Vt == VtLpwstr ? Marshal.PtrToStringUni(variant.Data) : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			PropVariantClear(ref variant);
			Release(store);
		}
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
		if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
	}

	private const int DataFlowAll = 2;
	private const int StateAll = 0x0F;
	private const uint ClsCtxAll = 23;
	private const uint StgmRead = 0;
	private const ushort VtLpwstr = 31;
	private const ushort VtClsid = 72;

	private static Guid ClsidMMDeviceEnumerator => new("BCDE0395-E52F-467C-8E3D-C4579291692E");

	// DEVPKEY_Device_ContainerId — the same key DeviceContainerId reads through CfgMgr32, and the
	// one AudioEndpointService matches endpoints on.
	private static PropertyKey DevpkeyDeviceContainerId => new()
	{
		Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
		Pid = 2,
	};

	// PKEY_Device_FriendlyName
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

	// Declared with PreserveSig so failures return HRESULTs; vtable order must stay exact, and each
	// interface is truncated after the last method used.
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
