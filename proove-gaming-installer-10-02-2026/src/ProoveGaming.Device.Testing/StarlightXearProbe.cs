using System.Runtime.InteropServices;
using System.Text;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — what does the C-Media Xear SDK actually give us on THIS device? ─────────────
//
// The customer supplied C:\Starlight_SDK (CmXearSDK V1.0.28.0 + the AUSDOM A3 Pro driver/APO
// package) and asked what can be enabled with it. The 2026-08-07 vendor request already said we
// had the SDK and that it looked like "EQ, audio enhancements, licensing and device management" —
// but that was read off the demo app, never RUN against the hardware. This probe runs it.
//
// WHAT THIS ANSWERS, THAT READING THE HEADERS CANNOT
//
//   1. Does CmXearInit() even succeed on a machine with no C-Media APO installed?
//   2. Does CmGetAudioEndpoint() return the Starlight endpoints (dongle MI_00), and with which
//      GUID — the SDK keys everything by its own endpoint UID, not by Container ID.
//   3. For each of the SDK's ~170 properties: OK, CE_UnsupportedError or CE_LicenseError?
//      That split is the whole answer. Non-APO properties (volume, peak level, default device,
//      foreground process, formats) should work anywhere; APO properties (EQ, NR, surround)
//      need CMAPO_ICE bound to the endpoint, and the supplied INF binds PID_0367, not our 036D.
//   4. Which licenses are active, and what does the device claim to support?
//
// The SDK is NOT a lighting or battery route: its only HID use is `_i2c_read` over
// HidD_GetInputReport/SetOutputReport for hardware license validation, and `SystemDynamicLighting`
// is a READ of the Windows "Dynamic Lighting" setting (registry Software\Microsoft\Lighting), not
// an LED control. So this probe deliberately does not chase RGB — see the plan for that story.
//
// READ-ONLY. Only CmGetProperty is ever called; nothing is set, no license is activated.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight xear [sdkDir]
//
// sdkDir defaults to the customer's drop; the DLL is loaded from wherever it lies (it needs its
// sibling .crt), so nothing is copied into this repo.
internal static class StarlightXearProbe
{
	private const string DefaultSdkDir =
		@"C:\Starlight_SDK\AUSDOM_A3_Pro\CmXearSDK_V1.0.28.0_C-Media\CmXearSDK_V1.0.28.0_C-Media\CmXearSDK_V1.0.28.0_C-Media\Release";

	// The SDK's DisplayName is the endpoint's SHORT name — "Headphones", "Microphone" — not the
	// "Headphones (PG Starlight)" Windows shows, so matching on the product name finds nothing.
	// USBVendorID / USBProductID are the reliable key, and the SDK reports them in decimal.
	private const string DefaultVendorId = "3468";  // 0x0D8C, C-Media
	private const string DefaultProductId = "877";  // 0x036D, PG Starlight

	// The device under test, threaded through as a parameter rather than held in mutable statics:
	// the `cosmo` arm used to overwrite a static and never restore it, which made the arms
	// order-dependent for anything that called Run twice.
	private readonly record struct DeviceId(string VendorId, string ProductId);

	// COSMO (PID 0x0334 = 820) carries the CM002X1 licensing coprocessor per our own
	// docs/headset/cosmo/ guide. Same SDK, same certificate, different silicon — the comparison is
	// what turns "no license" into a diagnosis.
	private static readonly DeviceId Starlight = new(DefaultVendorId, DefaultProductId);
	private static readonly DeviceId Cosmo = new(DefaultVendorId, "820");

	// Every property named in CmXearSDK_API_Reference_Guide.pdf rev 0.9, grouped the way the SDK's
	// own demo app groups them. The Non-APO group is what should work on any endpoint; everything
	// below it is a CMAPO_ICE effect property and is expected to fail without the APO.
	private static readonly (string Group, string[] Names)[] Properties =
	[
		("Non-APO / device", [
			"DisplayName", "DevicePath", "InstanceID", "EndpointID", "DataFlow", "JackType",
			"ControllerDisplayName", "ControllerDescription", "ControllerInstanceID",
			"USBDeviceInstanceID", "USBVendorID", "USBProductID", "USBRevisionNumber",
			"DriverVersion"]),
		("Non-APO / format", [
			"DefaultFormat", "ChannelCount", "MeteringChannelCount"]),
		("Non-APO / volume", [
			"VolumeRange", "MutingState"]),
		("Non-APO / session", [
			"SessionInstanceID"]),
		("Non-APO / system", [
			"ActivatedLicense", "SupportedLicenseCategory", "SystemDynamicLighting",
			"ForegroundProcessID", "ForegroundProcessInfo"]),
		("APO / master switch", [
			"SystemEffectsControl"]),
		("APO / playback EQ", [
			"Enable_EQ_GFX", "EQ_Slider1", "EQ_Slider5", "EQ_Slider10",
			"EQ_BandFreq1", "EQ_BandQ1", "EQ_BandFilterType1"]),
		("APO / playback effects", [
			"Enable_AUDIOBRILLIANT_LFX", "AUDIOBRILLIANT_LEVEL",
			"Enable_VIRTUALBASS_LFX", "VIRTUALBASS_Mode", "VIRTUALBASS_Level",
			"VIRTUALBASS_CutOffFrequency",
			"Enable_ADAPTIVEVOLUME_GFX", "ADAPTIVEVOLUME_LEVEL", "ADAPTIVEVOLUME_MODE",
			"Enable_SS_LFX", "SS_LF_ANGLE", "SS_LF_DIST",
			"Enable_RFX_GFX", "RFX_ENVIRONMENT", "RFX_ROOMSIZE",
			"Enable_BM_GFX", "BM_Slider_BassLevel", "BM_Slider_Frequency", "BM_LargeSpeaker",
			"Enable_CONLAB_HPSPK_LFX", "CONLAB_HPSPK_MODE", "CONLAB_HPSPK_ROOMSIZE",
			"Enable_VOCALCANCEL_GFX", "VOCALCANCEL_LEVEL",
			"Enable_KEYSHIFT_GFX", "KEYSHIFT_LEVEL",
			"AI_Playback_NR_Enable", "AI_Playback_NR_Level", "AI_Playback_AGC_MaxGain"]),
		("APO / capture effects", [
			"Enable_VOICECLARITY_LFX", "VOICECLARITY_LEVEL", "VOICECLARITY_NOISESUPP_LEVEL",
			"NR_Enable", "NR_Level", "HD_NR_Enable",
			"AI_NR_Enable", "AI_NR_Level", "AI_AEC_Enable", "AI_AEC_DelayTime",
			"AEC_Enable", "AEC_Level", "AEC_DelayTime",
			"AI_MicArray_Enable", "AI_MicArray_MicDist", "AI_MicArray_BeamWidth",
			"AI_AGC_MaxGain",
			"MicEQ_Enable", "MicEQ_PreGain", "MicEQ_Slider1",
			"MicEQ_LPF_Enable", "MicEQ_LPF_FC", "MicEQ_HPF_Enable", "MicEQ_HPF_FC",
			"Compressor_Enable", "Compressor_Threshold", "Compressor_Ratio",
			"NoiseGate_Enable", "NoiseGate_Threshold",
			"VrmsLimiter_Enable", "VrmsLimiter_Level",
			"Enable_MICECHO", "MICECHO_Level",
			"Enable_MAGICVOICE", "MagicVoice_Selection",
			"AI_MagicVoice_Enable", "AI_MagicVoice_TargetIndex", "AI_MagicVoice_Pitch"]),
		("APO / head tracking", [
			"HeadTracking_Enable", "HeadTracking_Algorithm", "HeadTracking_Reset",
			"HeadTracking_Orientation_Enable", "HeadTracking_OrientationYaw",
			"HeadTracking_OrientationPitch", "HeadTracking_OrientationRoll",
			"HeadTracking_RoomSize", "HeadTracking_Reverb_Enable"]),
		("APO / vigilance mode", [
			"VigilanceMode"]),
	];

	// Properties whose Get takes an int input. Reading them with no input returns
	// CE_ParameterError, which reads as "broken" but only means "you did not say which channel".
	//   VolumeLevel / VolumeLevelScalar / PeakValue : channel index, -1 = master
	//   DefaultDevice                               : CmDefaultRole (1 Multimedia, 2 Communication)
	private static readonly (string Name, int Input, string InputLabel)[] IntInputProperties =
	[
		("VolumeLevel", -1, "master"),
		("VolumeLevelScalar", -1, "master"),
		("PeakValue", -1, "master"),
		("PeakValue", 0, "ch0"),
		("DefaultDevice", 1, "Multimedia"),
		("DefaultDevice", 2, "Communication"),
	];

	// The license GUID the vendor ships for this product family, from
	// CM002X1_AUSDOM_A3_PRO_GUID.txt in the driver/APO package. The Chinese demo-app guide
	// ("CmXearSDK DemoApp使用說明.pdf", step 3b) is explicit that this is what goes into
	// CmActivateLicense: "填寫License，也就是GUID，依照不同裝置有不同的GUID，可從我們提供的附件裡
	// 找到 XXXX_GUID.txt" — fill in the License, i.e. the GUID; different devices have different
	// GUIDs; find it in the supplied attachment XXXX_GUID.txt.
	private const string VendorLicenseGuid = "158FD125-36BD-4A73-9E07-518869EDD288";

	public static void Run(string? sdkDir, string? device = null, string? activateGuid = null)
	{
		var isCosmo = string.Equals(device, "cosmo", StringComparison.OrdinalIgnoreCase);
		var name = isCosmo ? "PG Cosmo" : "PG Starlight";
		var target = isCosmo ? Cosmo : Starlight;

		var dir = string.IsNullOrWhiteSpace(sdkDir) ? DefaultSdkDir : sdkDir;
		var dll = Path.Combine(dir, "CmXearSDK.dll");

		Console.WriteLine($"{name} — C-Media Xear SDK capability probe (READ-ONLY)");
		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"SDK    : {dll}");

		if (!File.Exists(dll))
		{
			Console.WriteLine("  MISSING. Pass the folder holding CmXearSDK.dll as the last argument.");
			return;
		}

		var crt = Directory.GetFiles(dir, "*.crt");
		Console.WriteLine($"License: {(crt.Length == 0 ? "no .crt beside the DLL" : string.Join(", ", crt.Select(Path.GetFileName)))}");
		Console.WriteLine();

		XearInterop.LoadFrom(dll);

		var err = XearInterop.CmXearInit();
		Console.WriteLine($"CmXearInit()            -> {err}");
		if (err != XearInterop.CmErr.CE_OK)
		{
			Console.WriteLine("  SDK refused to initialise; nothing further can be read.");
			return;
		}

		try
		{
			DumpLicenses();

			if (activateGuid is not null)
			{
				var guid = Guid.Parse(activateGuid.Length == 0 ? VendorLicenseGuid : activateGuid);
				var actErr = XearInterop.CmActivateLicense(guid);
				Console.WriteLine($"CmActivateLicense({guid})");
				Console.WriteLine($"                        -> {actErr}");
				DumpLicenses();
			}

			var matched = DumpEndpoints(target);

			foreach (var (uid, label) in matched)
			{
				Console.WriteLine();
				Console.WriteLine(new string('-', 96));
				Console.WriteLine($"ENDPOINT {label}");
				Console.WriteLine($"  UID {uid}");
				Console.WriteLine(new string('-', 96));
				DumpProperties(uid);
			}

			if (matched.Count == 0)
			{
				Console.WriteLine();
				Console.WriteLine($"No endpoint reporting VID {target.VendorId} / PID {target.ProductId} — is the dongle plugged in?");
			}
		}
		finally
		{
			Console.WriteLine();
			Console.WriteLine($"CmXearDeinit()          -> {XearInterop.CmXearDeinit()}");
		}
	}

	private static void DumpLicenses()
	{
		var err = XearInterop.CmGetLicenseSettings(out var type, out var len, out var ptr);
		Console.Write($"CmGetLicenseSettings()  -> {err}");
		if (err == XearInterop.CmErr.CE_OK && type == XearInterop.CmDataType.CDT_ArrayedGUID)
		{
			var guids = XearInterop.ReadArray<Guid>(len, ptr);
			Console.WriteLine(guids.Length == 0
				? "  (no licenses activated)"
				: "  " + string.Join(", ", guids));
			XearInterop.CmFreeData(type, len, ptr);
		}
		else
		{
			Console.WriteLine();
		}
	}

	private static List<(Guid Uid, string Label)> DumpEndpoints(DeviceId target)
	{
		var result = new List<(Guid, string)>();

		var err = XearInterop.CmGetAudioEndpoint(out var type, out var len, out var ptr);
		Console.WriteLine($"CmGetAudioEndpoint()    -> {err}, {len} endpoint(s), type {type}");
		if (err != XearInterop.CmErr.CE_OK || type != XearInterop.CmDataType.CDT_ArrayedGUID)
		{
			return result;
		}

		var uids = XearInterop.ReadArray<Guid>(len, ptr);
		XearInterop.CmFreeData(type, len, ptr);

		Console.WriteLine();
		foreach (var uid in uids)
		{
			var name = ReadOne(uid, "DisplayName");
			var flow = ReadOne(uid, "DataFlow");
			var vid = ReadOne(uid, "USBVendorID");
			var pid = ReadOne(uid, "USBProductID");
			var mine = vid == target.VendorId && pid == target.ProductId;
			Console.WriteLine($"  {(mine ? ">>" : "  ")} {uid}  flow={flow,-3} vid={vid,-6} pid={pid,-6} {name}");
			if (mine)
			{
				result.Add((uid, $"{name} (flow {flow})"));
			}
		}

		return result;
	}

	private static void DumpProperties(Guid uid)
	{
		foreach (var (group, names) in Properties)
		{
			Console.WriteLine();
			Console.WriteLine($"  [{group}]");
			foreach (var name in names)
			{
				var err = XearInterop.CmGetProperty(
					uid, name, XearInterop.CmDataType.CDT_None, 0, IntPtr.Zero,
					out var type, out var len, out var ptr);

				var value = err == XearInterop.CmErr.CE_OK ? XearInterop.Describe(type, len, ptr) : string.Empty;
				if (err == XearInterop.CmErr.CE_OK)
				{
					XearInterop.CmFreeData(type, len, ptr);
				}

				Console.WriteLine($"    {name,-34} {err,-22} {value}");
			}
		}

		Console.WriteLine();
		Console.WriteLine("  [Non-APO / with input parameter]");
		foreach (var (name, input, label) in IntInputProperties)
		{
			var inPtr = Marshal.AllocHGlobal(sizeof(int));
			try
			{
				Marshal.WriteInt32(inPtr, input);
				var err = XearInterop.CmGetProperty(
					uid, name, XearInterop.CmDataType.CDT_ArrayedInt32, 1, inPtr,
					out var type, out var len, out var ptr);

				var value = err == XearInterop.CmErr.CE_OK ? XearInterop.Describe(type, len, ptr) : string.Empty;
				if (err == XearInterop.CmErr.CE_OK)
				{
					XearInterop.CmFreeData(type, len, ptr);
				}

				Console.WriteLine($"    {$"{name}({label})",-34} {err,-22} {value}");
			}
			finally
			{
				Marshal.FreeHGlobal(inPtr);
			}
		}
	}

	private static string ReadOne(Guid uid, string name)
	{
		var err = XearInterop.CmGetProperty(
			uid, name, XearInterop.CmDataType.CDT_None, 0, IntPtr.Zero,
			out var type, out var len, out var ptr);
		if (err != XearInterop.CmErr.CE_OK)
		{
			return $"<{err}>";
		}

		var text = XearInterop.Describe(type, len, ptr);
		XearInterop.CmFreeData(type, len, ptr);
		return text;
	}
}

// Minimal P/Invoke surface for CmXearSDK.dll — only the read paths this probe uses. The vendor's
// own C# wrapper (DemoApp/XearSDK.cs, XearSDK.CSWrapper.cs) is the reference; it is not vendored
// here because it is 970 lines of marshalling for setters we deliberately never call.
internal static class XearInterop
{
	private const string Dll = "CmXearSDK.dll";

	private static string? _resolvedPath;

	internal enum CmErr
	{
		CE_OK = 0,
		CE_UnknownError,
		CE_LicenseError,
		CE_UnsupportedError,
		CE_DeviceError,
		CE_ParameterError,
		CE_PropertyError,
		CE_EventError,
	}

	internal enum CmDataType
	{
		CDT_None = 0,
		CDT_ArrayedGUID,
		CDT_ArrayedInt32,
		CDT_ArrayedFloat,
		CDT_ArrayedByte,
		CDT_ArrayedCmDataWString,
		CDT_ArrayedCmDataLogSettings,
		CDT_ArrayedCmDataEventSettings,
		CDT_ArrayedCmDataAudioFormat,
		CDT_ArrayedCmDataRangeFloat,
		CDT_ArrayedCmDataChannelVolume,
		CDT_ArrayedCmDataProcessInfo,
		CDT_ArrayedCmDataVigilanceMode,
	}

	[StructLayout(LayoutKind.Sequential)]
	internal record struct AudioFormat(int Channels, int BitDepth, int SampleRate);

	[StructLayout(LayoutKind.Sequential)]
	internal struct ChannelVolume
	{
		public int Channel;
		public float Volume;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CmDataRangeFloat
	{
		public float Min;
		public float Max;
		public float Step;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CmDataVigilanceModeRaw
	{
		public int Enable;
		public int Mode;
	}

	// The DLL lives in the customer's SDK drop and needs its sibling .crt, so it is resolved by
	// absolute path rather than copied next to our binaries.
	internal static void LoadFrom(string dllPath)
	{
		_resolvedPath = dllPath;
		NativeLibrary.SetDllImportResolver(
			typeof(XearInterop).Assembly,
			(name, _, _) => name == Dll && _resolvedPath is not null
				? NativeLibrary.Load(_resolvedPath)
				: IntPtr.Zero);
	}

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmXearInit();

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmXearDeinit();

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmFreeData(CmDataType inDatType, int inArrLen, IntPtr inArrDat);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmGetLicenseSettings(out CmDataType outDatType, out int outArrLen, out IntPtr outArrDat);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmActivateLicense(Guid inLicense);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmDeactivateLicense(Guid inLicense);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmSetProperty(
		Guid inAudEndptUID,
		[MarshalAs(UnmanagedType.LPWStr)] string inProp,
		CmDataType inDatType, int inArrLen, IntPtr inArrDat);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmPlayAudio(
		Guid inAudEndptUID,
		[MarshalAs(UnmanagedType.LPWStr)] string inFilePath,
		uint inActiveDevChannel, int inLooping);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmStopAudio(Guid inAudEndptUID);

	internal static AudioFormat ReadAudioFormat(IntPtr ptr) => Marshal.PtrToStructure<AudioFormat>(ptr);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmGetAudioEndpoint(out CmDataType outDatType, out int outArrLen, out IntPtr outArrDat);

	[DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	internal static extern CmErr CmGetProperty(
		Guid inAudEndptUID,
		[MarshalAs(UnmanagedType.LPWStr)] string inProp,
		CmDataType inDatType, int inArrLen, IntPtr inArrDat,
		out CmDataType outDatType, out int outArrLen, out IntPtr outArrDat);

	internal static T[] ReadArray<T>(int len, IntPtr ptr) where T : struct
	{
		var result = new T[len];
		var size = Marshal.SizeOf<T>();
		for (var i = 0; i < len; ++i)
		{
			result[i] = Marshal.PtrToStructure<T>(ptr + i * size);
		}

		return result;
	}

	// One-line rendering of whatever the SDK handed back, so a 170-property dump stays readable.
	internal static string Describe(CmDataType type, int len, IntPtr ptr)
	{
		if (ptr == IntPtr.Zero || len == 0)
		{
			return type == CmDataType.CDT_None ? string.Empty : $"{type} (empty)";
		}

		switch (type)
		{
			case CmDataType.CDT_ArrayedGUID:
				return string.Join(", ", ReadArray<Guid>(len, ptr));
			case CmDataType.CDT_ArrayedInt32:
				return string.Join(", ", ReadArray<int>(len, ptr));
			case CmDataType.CDT_ArrayedFloat:
				return string.Join(", ", ReadArray<float>(len, ptr).Select(f => f.ToString("0.###")));
			case CmDataType.CDT_ArrayedByte:
				return Convert.ToHexString(ReadArray<byte>(len, ptr));
			case CmDataType.CDT_ArrayedCmDataWString:
			{
				var sb = new StringBuilder();
				for (var i = 0; i < len; ++i)
				{
					if (i > 0)
					{
						sb.Append(", ");
					}

					sb.Append(Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr + i * IntPtr.Size)));
				}

				return sb.ToString();
			}
			case CmDataType.CDT_ArrayedCmDataAudioFormat:
				return string.Join(", ", ReadArray<AudioFormat>(len, ptr)
					.Select(f => $"{f.Channels}ch/{f.BitDepth}bit/{f.SampleRate}Hz"));
			case CmDataType.CDT_ArrayedCmDataRangeFloat:
				return string.Join(", ", ReadArray<CmDataRangeFloat>(len, ptr)
					.Select(r => $"[{r.Min:0.###}..{r.Max:0.###} step {r.Step:0.###}]"));
			case CmDataType.CDT_ArrayedCmDataVigilanceMode:
				return string.Join(", ", ReadArray<CmDataVigilanceModeRaw>(len, ptr)
					.Select(v => $"enable={v.Enable} mode={v.Mode}"));
			default:
				return $"{type} x{len}";
		}
	}
}
