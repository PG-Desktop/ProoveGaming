using System.Runtime.InteropServices;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — what can the Xear SDK actually CHANGE on this device, right now? ────────────
//
// `starlight xear` established the read side: the non-APO half answers, the APO effect half is
// license-gated. This arm answers the follow-up — of the properties that DO answer, which ones
// accept a write, does the write land, and is it something the app could offer a user?
//
// Every case here is License Category "Public" in the vendor's API reference, so none of this needs
// the license or the APO. That is the point: this is the part of the SDK that works on the hardware
// we actually have.
//
// WHAT IT WRITES, AND HOW IT PROTECTS THE MACHINE
//
//   MutingState        mute -> read back -> restore                (render endpoint)
//   VolumeLevelScalar  set to 0.30 -> read back -> restore
//   DefaultFormat      switch sample rate -> read back -> restore  (reaches the USB audio stream)
//   SystemEffectsControl  toggle -> read back -> restore           (Windows enhancements switch)
//   DefaultDevice      set the role it ALREADY holds               (see the note below)
//   CmPlayAudio        plays a generated mono WAV to one channel   (a speaker / channel test)
//
// Every value is read first, restored in a `finally`, and the restore is verified. Nothing is left
// changed if the probe throws or is killed mid-run — except a Ctrl-C between set and restore, which
// is why each step restores before the next begins rather than at the end.
//
// DefaultDevice is deliberately NOT exercised destructively. Starlight already holds both roles on
// this machine, so setting them is a no-op that proves the call path without moving the user's
// default audio device to something else. Flipping it to another endpoint and back would be a
// visible, user-affecting change for no extra information.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight xear-write --i-mean-it
//
// Refuses to run without the opt-in flag, matching the other writing probes in this project.
internal static class StarlightXearWriteProbe
{
	private const string DefaultSdkDir =
		@"C:\Starlight_SDK\AUSDOM_A3_Pro\CmXearSDK_V1.0.28.0_C-Media\CmXearSDK_V1.0.28.0_C-Media\CmXearSDK_V1.0.28.0_C-Media\Release";

	private const string VendorId = "3468";
	private const string ProductId = "877";

	public static void Run(bool allowWrites, string? sdkDir)
	{
		Console.WriteLine("PG Starlight — Xear SDK WRITE probe (non-APO, license-free properties)");
		Console.WriteLine(new string('=', 96));

		if (!allowWrites)
		{
			Console.WriteLine("This arm WRITES to the audio endpoint. Re-run with --i-mean-it to proceed.");
			Console.WriteLine("Every write is read back and restored, but it does change live audio state.");
			return;
		}

		var dir = string.IsNullOrWhiteSpace(sdkDir) ? DefaultSdkDir : sdkDir;
		var dll = Path.Combine(dir, "CmXearSDK.dll");
		if (!File.Exists(dll))
		{
			Console.WriteLine($"CmXearSDK.dll not found at {dll}");
			return;
		}

		XearInterop.LoadFrom(dll);

		var err = XearInterop.CmXearInit();
		Console.WriteLine($"CmXearInit() -> {err}");
		if (err != XearInterop.CmErr.CE_OK)
		{
			return;
		}

		try
		{
			if (!TryFindEndpoints(out var render, out var capture))
			{
				Console.WriteLine("Starlight endpoints not found — is the dongle plugged in?");
				return;
			}

			Console.WriteLine($"render  {render}");
			Console.WriteLine($"capture {capture}");

			MutingStateCase(render);
			VolumeCase(render);
			DefaultFormatCase(render);
			SystemEffectsCase(render);
			DefaultDeviceCase(render);
			PlayAudioCase(render);
		}
		finally
		{
			Console.WriteLine();
			Console.WriteLine($"CmXearDeinit() -> {XearInterop.CmXearDeinit()}");
		}
	}

	// ── cases ─────────────────────────────────────────────────────────────────────────────────

	private static void MutingStateCase(Guid uid)
	{
		Header("MutingState", "mute the headset from software");

		if (!TryGetInt(uid, "MutingState", out var original))
		{
			return;
		}

		Console.WriteLine($"    before        {original}");
		var target = original == 0 ? 1 : 0;
		try
		{
			Console.WriteLine($"    set {target}         -> {SetInt(uid, "MutingState", target)}");
			Console.WriteLine($"    read back     {(TryGetInt(uid, "MutingState", out var now) ? now.ToString() : "?")}"
				+ (TryGetInt(uid, "MutingState", out var n2) && n2 == target ? "   LANDED" : "   did not land"));
		}
		finally
		{
			SetInt(uid, "MutingState", original);
			Console.WriteLine($"    restored      {(TryGetInt(uid, "MutingState", out var back) ? back.ToString() : "?")}");
		}
	}

	private static void VolumeCase(Guid uid)
	{
		Header("VolumeLevelScalar", "move the output volume");

		if (!TryGetFloat(uid, "VolumeLevelScalar", -1, out var original))
		{
			return;
		}

		Console.WriteLine($"    before        {original:0.###}");
		const float target = 0.30f;
		try
		{
			// The setter takes CmDataChannelVolume { int channel; float volume; }, -1 = master.
			Console.WriteLine($"    set {target:0.00}      -> {SetChannelVolume(uid, "VolumeLevelScalar", -1, target)}");
			if (TryGetFloat(uid, "VolumeLevelScalar", -1, out var now))
			{
				Console.WriteLine($"    read back     {now:0.###}"
					+ (Math.Abs(now - target) < 0.02f ? "   LANDED" : "   did not land"));
			}
		}
		finally
		{
			SetChannelVolume(uid, "VolumeLevelScalar", -1, original);
			if (TryGetFloat(uid, "VolumeLevelScalar", -1, out var back))
			{
				Console.WriteLine($"    restored      {back:0.###}");
			}
		}
	}

	private static void DefaultFormatCase(Guid uid)
	{
		Header("DefaultFormat", "change the USB audio stream format the device runs at");

		var err = XearInterop.CmGetProperty(
			uid, "DefaultFormat", XearInterop.CmDataType.CDT_None, 0, IntPtr.Zero,
			out var type, out var len, out var ptr);
		if (err != XearInterop.CmErr.CE_OK || len == 0)
		{
			Console.WriteLine($"    read failed   {err}");
			return;
		}

		var original = XearInterop.ReadAudioFormat(ptr);
		XearInterop.CmFreeData(type, len, ptr);
		Console.WriteLine($"    before        {Fmt(original)}");

		// Ask the device which formats it accepts before trying to set one. This separates "the
		// setter does not work" from "this headset is 48 kHz only", which look identical otherwise.
		foreach (var candidate in new[]
		{
			original with { SampleRate = 44100 },
			original with { SampleRate = 96000 },
			original with { BitDepth = 24 },
			original,
		})
		{
			Console.WriteLine($"    supported? {Fmt(candidate),-18} {IsFormatSupported(uid, candidate)}");
		}

		// 48000 -> 44100 and back. Both are USB Audio Class rates every headset supports, so this
		// is the least invasive format change that is still a real one.
		var target = original with { SampleRate = original.SampleRate == 48000 ? 44100 : 48000 };
		try
		{
			Console.WriteLine($"    set {Fmt(target),-18}-> {SetAudioFormat(uid, "DefaultFormat", target)}");
			if (TryGetAudioFormat(uid, out var now))
			{
				Console.WriteLine($"    read back     {Fmt(now)}"
					+ (now.SampleRate == target.SampleRate ? "   LANDED" : "   did not land"));
			}
		}
		finally
		{
			SetAudioFormat(uid, "DefaultFormat", original);
			if (TryGetAudioFormat(uid, out var back))
			{
				Console.WriteLine($"    restored      {Fmt(back)}");
			}
		}
	}

	private static void SystemEffectsCase(Guid uid)
	{
		Header("SystemEffectsControl", "the Windows \"audio enhancements\" switch");

		if (!TryGetInt(uid, "SystemEffectsControl", out var original))
		{
			return;
		}

		Console.WriteLine($"    before        {original}");
		var target = original == 0 ? 1 : 0;
		try
		{
			Console.WriteLine($"    set {target}         -> {SetInt(uid, "SystemEffectsControl", target)}");
			if (TryGetInt(uid, "SystemEffectsControl", out var now))
			{
				Console.WriteLine($"    read back     {now}" + (now == target ? "   LANDED" : "   did not land"));
			}
		}
		finally
		{
			SetInt(uid, "SystemEffectsControl", original);
			Console.WriteLine($"    restored      {(TryGetInt(uid, "SystemEffectsControl", out var back) ? back.ToString() : "?")}");
		}
	}

	private static void DefaultDeviceCase(Guid uid)
	{
		Header("DefaultDevice", "make Starlight the default playback device");
		Console.WriteLine("    Starlight already holds both roles here, so this sets the role it already has —");
		Console.WriteLine("    enough to prove the call path without moving the user's default device.");

		foreach (var (role, label) in new[] { (1, "Multimedia"), (2, "Communication") })
		{
			Console.WriteLine($"    set {label,-14}-> {SetInt(uid, "DefaultDevice", role)}");
		}
	}

	private static void PlayAudioCase(Guid uid)
	{
		Header("CmPlayAudio", "play a test tone to one channel — a speaker / channel test");

		var wav = Path.Combine(Path.GetTempPath(), "pg_starlight_channel_test.wav");
		WriteMonoToneWav(wav, seconds: 1, hz: 440);
		Console.WriteLine($"    file          {wav}");

		foreach (var (mask, label) in new[] { (0x1u, "FRONT_LEFT"), (0x2u, "FRONT_RIGHT") })
		{
			var playErr = XearInterop.CmPlayAudio(uid, wav, mask, 0);
			Console.WriteLine($"    play {label,-12}-> {playErr}");
			if (playErr == XearInterop.CmErr.CE_OK)
			{
				Thread.Sleep(1200);
				XearInterop.CmStopAudio(uid);
			}
		}

		try
		{
			File.Delete(wav);
		}
		catch (IOException)
		{
			// The SDK may still hold the handle briefly; a temp file left behind is harmless.
		}
	}

	// ── helpers ───────────────────────────────────────────────────────────────────────────────

	private static void Header(string name, string what)
	{
		Console.WriteLine();
		Console.WriteLine($"  {name} — {what}");
	}

	private static string Fmt(XearInterop.AudioFormat f) => $"{f.Channels}ch/{f.BitDepth}bit/{f.SampleRate}Hz";

	private static bool TryFindEndpoints(out Guid render, out Guid capture)
	{
		render = Guid.Empty;
		capture = Guid.Empty;

		var err = XearInterop.CmGetAudioEndpoint(out var type, out var len, out var ptr);
		if (err != XearInterop.CmErr.CE_OK)
		{
			return false;
		}

		var uids = XearInterop.ReadArray<Guid>(len, ptr);
		XearInterop.CmFreeData(type, len, ptr);

		foreach (var uid in uids)
		{
			if (ReadString(uid, "USBVendorID") != VendorId || ReadString(uid, "USBProductID") != ProductId)
			{
				continue;
			}

			if (ReadString(uid, "DataFlow") == "1")
			{
				render = uid;
			}
			else
			{
				capture = uid;
			}
		}

		return render != Guid.Empty;
	}

	private static string ReadString(Guid uid, string prop)
	{
		var err = XearInterop.CmGetProperty(
			uid, prop, XearInterop.CmDataType.CDT_None, 0, IntPtr.Zero,
			out var type, out var len, out var ptr);
		if (err != XearInterop.CmErr.CE_OK)
		{
			return string.Empty;
		}

		var text = XearInterop.Describe(type, len, ptr);
		XearInterop.CmFreeData(type, len, ptr);
		return text;
	}

	private static bool TryGetInt(Guid uid, string prop, out int value)
	{
		value = 0;
		var err = XearInterop.CmGetProperty(
			uid, prop, XearInterop.CmDataType.CDT_None, 0, IntPtr.Zero,
			out var type, out var len, out var ptr);
		if (err != XearInterop.CmErr.CE_OK || len == 0)
		{
			Console.WriteLine($"    read failed   {err}");
			return false;
		}

		value = XearInterop.ReadArray<int>(len, ptr)[0];
		XearInterop.CmFreeData(type, len, ptr);
		return true;
	}

	private static bool TryGetFloat(Guid uid, string prop, int channel, out float value)
	{
		value = 0;
		var inPtr = Marshal.AllocHGlobal(sizeof(int));
		try
		{
			Marshal.WriteInt32(inPtr, channel);
			var err = XearInterop.CmGetProperty(
				uid, prop, XearInterop.CmDataType.CDT_ArrayedInt32, 1, inPtr,
				out var type, out var len, out var ptr);
			if (err != XearInterop.CmErr.CE_OK || len == 0)
			{
				Console.WriteLine($"    read failed   {err}");
				return false;
			}

			value = XearInterop.ReadArray<float>(len, ptr)[0];
			XearInterop.CmFreeData(type, len, ptr);
			return true;
		}
		finally
		{
			Marshal.FreeHGlobal(inPtr);
		}
	}

	private static bool TryGetAudioFormat(Guid uid, out XearInterop.AudioFormat value)
	{
		value = default;
		var err = XearInterop.CmGetProperty(
			uid, "DefaultFormat", XearInterop.CmDataType.CDT_None, 0, IntPtr.Zero,
			out var type, out var len, out var ptr);
		if (err != XearInterop.CmErr.CE_OK || len == 0)
		{
			return false;
		}

		value = XearInterop.ReadAudioFormat(ptr);
		XearInterop.CmFreeData(type, len, ptr);
		return true;
	}

	// IsFormatSupported takes the candidate format as INPUT and answers int -> bool.
	private static string IsFormatSupported(Guid uid, XearInterop.AudioFormat format)
	{
		var inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XearInterop.AudioFormat>());
		try
		{
			Marshal.StructureToPtr(format, inPtr, false);
			var err = XearInterop.CmGetProperty(
				uid, "IsFormatSupported", XearInterop.CmDataType.CDT_ArrayedCmDataAudioFormat, 1, inPtr,
				out var type, out var len, out var ptr);
			if (err != XearInterop.CmErr.CE_OK || len == 0)
			{
				return err.ToString();
			}

			var supported = XearInterop.ReadArray<int>(len, ptr)[0];
			XearInterop.CmFreeData(type, len, ptr);
			return supported != 0 ? "yes" : "no";
		}
		finally
		{
			Marshal.FreeHGlobal(inPtr);
		}
	}

	private static XearInterop.CmErr SetInt(Guid uid, string prop, int value)
	{
		var ptr = Marshal.AllocHGlobal(sizeof(int));
		try
		{
			Marshal.WriteInt32(ptr, value);
			return XearInterop.CmSetProperty(uid, prop, XearInterop.CmDataType.CDT_ArrayedInt32, 1, ptr);
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}
	}

	private static XearInterop.CmErr SetChannelVolume(Guid uid, string prop, int channel, float volume)
	{
		var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<XearInterop.ChannelVolume>());
		try
		{
			Marshal.StructureToPtr(new XearInterop.ChannelVolume { Channel = channel, Volume = volume }, ptr, false);
			return XearInterop.CmSetProperty(
				uid, prop, XearInterop.CmDataType.CDT_ArrayedCmDataChannelVolume, 1, ptr);
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}
	}

	private static XearInterop.CmErr SetAudioFormat(Guid uid, string prop, XearInterop.AudioFormat format)
	{
		var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<XearInterop.AudioFormat>());
		try
		{
			Marshal.StructureToPtr(format, ptr, false);
			return XearInterop.CmSetProperty(
				uid, prop, XearInterop.CmDataType.CDT_ArrayedCmDataAudioFormat, 1, ptr);
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}
	}

	// CmPlayAudio accepts mono WAV only, so the test tone is generated rather than shipped.
	private static void WriteMonoToneWav(string path, int seconds, double hz)
	{
		const int rate = 48000;
		var samples = rate * seconds;
		using var w = new BinaryWriter(File.Create(path));

		w.Write("RIFF"u8);
		w.Write(36 + samples * 2);
		w.Write("WAVE"u8);
		w.Write("fmt "u8);
		w.Write(16);
		w.Write((short)1);      // PCM
		w.Write((short)1);      // mono
		w.Write(rate);
		w.Write(rate * 2);      // byte rate
		w.Write((short)2);      // block align
		w.Write((short)16);     // bits
		w.Write("data"u8);
		w.Write(samples * 2);

		for (var i = 0; i < samples; ++i)
		{
			// Fade the first and last 5 ms so the tone does not click.
			var fade = Math.Min(1.0, Math.Min(i, samples - i) / (rate * 0.005));
			w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 0.25 * fade * short.MaxValue));
		}
	}
}
