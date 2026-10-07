// Scratch entry point for hardware probes. Each investigation rewrites this file; probe logic lives
// in its own class so it survives. Previous contents are in git history.
//
// CURRENT INVESTIGATION: PG Starlight headset (VID 0x0D8C / PID 0x036D).
// Plan and findings: docs/plans/add-starlight-headset/add-starlight-headset-plan.md
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight
//       Descriptor survey: interfaces, collections, report IDs, raw descriptors. Reports whether
//       output report 0xFF (the LED spec's own channel) is declared. It is not.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight buttons
//       Guided capture of the headset's two buttons and volume wheel. Listens on EVERY HID
//       interface, through both HidSharp and Raw Input, and reports which frames belong to which
//       button. READ-ONLY.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight console
//       Interactive capability console. Menu: the CM002X1 coprocessor probe (READ-ONLY, the
//       documented FD 12 / FD 13 I2C handshake that the 2026-08-07 bare read never performed),
//       the live audio setters, and an RGB verdict re-checked against the current descriptor.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight xear [cosmo] [sdkDir]
//       Loads the customer's CmXearSDK.dll (C:\Starlight_SDK by default) and reports, per SDK
//       property, whether the live dongle answers OK / unsupported / license-gated. READ-ONLY.
//       Add `cosmo` to point the same probe at PG Cosmo's dongle, which carries the CM002X1
//       licensing coprocessor Starlight lacks — the comparison is the diagnosis.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight watch
//       Guided passive listen on report 0xFD. Walks timed phases (volume, mic mute, power cycle)
//       and reports which byte offsets moved in which phase. READ-ONLY.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight read [samples]
//       HidD_GetInputReport on 0xFD — a control-transfer READ, no host->device write.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight led --i-mean-it
//       Drives the documented CM002X1 LED sequence (Stop -> Configuration -> Colour Array ->
//       Start) on 0xFD, targeting static red. WRITES. Refuses to run without the opt-in flag.
//
// PREVIOUS INVESTIGATION: PG Cosmo headset (VID 0x0D8C / PID 0x0334).
// Plan and findings: docs/plans/add-cosmo-headset/add-cosmo-headset-plan.md
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing
//       Guided status hunt — power on/off, battery level, mic mute. Walks you through timed
//       phases and reports which byte offsets moved during which phase.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- descriptor
//       One-shot descriptor dump: interfaces, collections, report IDs, raw descriptor.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- poke
//       Stage A only: HidD_GetInputReport on 0xBC. A control-transfer READ — no writes.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- poke write
//       Stage A, then the GUARDED OPCODE SWEEP, which DOES WRITE to the device. Zero payloads,
//       denylisted reset/DFU ranges, and it aborts the instant the device stops enumerating.
//       Authorised by the user 2026-08-05 after passive listening was exhausted.
//
// CURRENT SIDE INVESTIGATION: Rampage RGB over Bluetooth (VID 0x28E9 / PID 0x3258 over USB; the
// BLE identity is exactly what the probe reports). Plan and findings:
// docs/plans/add-rampage-bluetooth-rgb/, docs/knowledge-base/rampage-keyboard-protocol/.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- rampage-bt
//       Full HID enumeration (no VID filter) marking Bluetooth-stack paths and the SmartInfinite
//       vendor usage, then a 0x14 function-block read on each Bluetooth candidate. READ-ONLY.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- rampage-bt write
//       The above, then static red / static green via Infrastructure's RampageKeyboardRgbService —
//       the DeviceHost's own RGB path — with a 0x14 read-back. WRITES (routine 0x15 only).
//       Outcome (2026-08-13): BLE advertises the channel but no write API lands — see
//       docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- rampage-24g
//       2.4G receiver: enumeration, receiver state query (0xAA channel), 0x14 read with both the
//       wired 56-byte and the C12 24-byte chunking. Excludes Aress/Cyrex wired PIDs from probing.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- rampage-24g write
//       The above, then static red via RampageKeyboardRgbService (wired framing) and, if that
//       does not persist, static green as a bare 0x15 in 24-byte chunks. WRITES.
//       Outcome (2026-08-13): both framings persist — RGB over 2.4G works with the wired shape.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- rampage-24g keys
//       Key matrix over the receiver: 0x17/0x16 reads with the wired 56-byte shape and the C12
//       24-byte chunking, FN-canary alignment check, one macro-region read packet. READ-ONLY.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- rampage-24g keys write
//       The above, then slot 33 (Q) -> W via the production RampageKeyboardKeyMappingService,
//       falling back to 24-byte-chunk 0x18 packets if the wired shape is discarded; the original
//       record is restored and verified whatever the outcome. WRITES.
//       Outcome (2026-08-13): 24-byte chunking is REQUIRED — 56-byte reads arrive misaligned and
//       56-byte writes are refused/discarded; the production service with maxPayload=24 persisted
//       and restored the slot through the receiver.
//
// WHICH ARMS WRITE TO THE DEVICE:
//   identity               READ-ONLY (enumeration only).
//   rampage-bt             READ-ONLY (enumeration + 0x14 function-block reads).
//   rampage-bt write       WRITES  — the documented 0x15 RGB command via RampageKeyboardRgbService,
//                          nothing else; 0x20/0x31 are never sent.
//   rampage-24g            READ-ONLY apart from the documented receiver state query (0xAA 0xD0)
//                          and 0x14 read requests.
//   rampage-24g write      WRITES  — 0x15 in two framings + 0x1D sync; 0x20/0x31 never sent.
//   rampage-24g keys       READ-ONLY (0x17/0x16/0x1B read requests).
//   rampage-24g keys write WRITES  — full-table 0x18 (production shape, 24-chunk fallback) on
//                          ONE slot, restored and verified afterwards; 0x20/0x31 never sent.
//   starlight              READ-ONLY (enumeration and descriptor parsing).
//   starlight buttons      READ-ONLY (passive input reads on every interface + Raw Input).
//   taipan                 WRITES  — documented battery request (cmd 0x48) over the 2.4G dongle,
//                          then listens on every collection for the single reply.
//   taipan response [wired|dongle] [button] [secs] [--keep]
//                          WRITES  — replays the button-map write frame by frame and DUMPS every
//                                    reply, to answer the vendor's "if there is no response your
//                                    proxy code is not connected correctly". Run `wired` first as
//                                    the reference, then `dongle`, and compare.
//   taipan config [button] WRITES  — remaps a button over the dongle and restores it, driving
//                          TelinkMouseService exactly as the app does over the cable.
//                          button = right (default) | wheel | forward | back.
//   taipan presence [secs] READ-ONLY — watches BTHLE + BTHENUM device nodes for the TAIPAN and
//                          prints IsConnected changes with timestamps. Measures how long Windows
//                          itself takes to report the device gone, before any polling cadence.
//   last-arrival [name] [secs]
//                          READ-ONLY — watches every BTHLE node's DEVPKEY_Device_LastArrivalDate
//                          through a power off/on cycle. Answers whether the timestamp tracks
//                          reconnects (the gate for docs/plans/add-stale-pairing-detection/).
//   taipan paths           READ-ONLY (enumeration + descriptor capability reporting).
//
//   taipan configmode [wired|dongle] [--write] [--keep]
//                          Tests the configuration-mode handshake from the vendor's 2026-09-02
//                          capture: 0x46 Para1=0x00 to enter, 0x40 F3 <mask> to apply, a ONE-frame
//                          commit, 0x46 Para1=0x01 to exit. Stages A-C write no flash; --write adds
//                          the real button-table write. The mouse is re-enabled on every exit path,
//                          including Ctrl+C — CMD_SetMouseState can otherwise leave it disabled.
//   last-arrival           READ-ONLY (device-node property reads, nothing opened).
//   taipan off [seconds]   WRITES  — the documented battery request only (cmd 0x48), twice over,
//                          with the mouse switched on and then off, to find a presence signal.
//   taipan wheel [mode]    WRITES  — rewrites the button table to set wheel scroll direction.
//                          mode = fixed (default, hardware-correct) | legacy | clear.
//   taipan all [seconds]   WRITES  — attempts the button-map write on EVERY dongle interface in
//                          turn, restoring after each. Documented opcodes only.
//   starlight battery      READ-ONLY — report-id sweep (0x00-0xFF, control-transfer reads) plus a
//                          full Windows device-property dump, phased across headset on/off/on.
//                          Hunts a dongle-side battery or power signal. Never writes to 0xFD.
//   starlight audio        READ-ONLY (Core Audio endpoint state / mute / volume watch).
//   starlight power        READ-ONLY (enumeration + control-transfer reads through a power cycle).
//   starlight effects      WRITES  — varies the documented operation-mode byte, never sends Stop.
//   starlight sweep        WRITES  — 12 framings of the DOCUMENTED LED command set, one at a
//                          time with pauses. No unknown opcodes. Refuses without --i-mean-it.
//   starlight watch        READ-ONLY (passive input reads).
//   starlight read         READ-ONLY (GET_REPORT control-transfer reads).
//   starlight led          WRITES  — documented LED sequence on 0xFD; refuses without --i-mean-it.
//   descriptor, watch      READ-ONLY (enumeration and GET_REPORT control-transfer reads).
//   poke                   READ-ONLY (Stage A only).
//   poke write             WRITES  — guarded opcode sweep on 0xBC.
//   bridge                 WRITES  — Stage 1 sends a 0xFD OUT report before any further argument.
//   bridge bc              WRITES  — Stage 1, then 47 opcodes on 0xBC.
//
// These vendor channels are undocumented, and an unknown write to an unknown channel is how the
// BLAZE flash corruption happened (docs/mouse/blaze). Starlight additionally persists its LED table
// TO FLASH. Do not run a writing arm casually.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- blaze [seconds]
//       BLAZE LED effect cycle: breathing blue -> breathing gradient -> neon -> colour cycle,
//       holding each for `seconds` (default 10). WRITES, but only through Infrastructure's
//       EasenMouseService — the same coordinated write the DeviceHost uses, never hand-rolled.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- blaze replay
//       Replays the OEM software's gradient-breathing write byte-for-byte from
//       cap-06.08.26-for-blaze.pcapng. WRITES, but only the vendor's own captured bytes.
//       Add `drive` or `status7` to reintroduce ONE of our divergences on top of that
//       known-good sequence, to find which one kills the effect.
//
// Run with ProoveGamingDeviceHost STOPPED (it holds the HID handle open).
//
// The probe classes for earlier investigations (T800 lighting + key matrix, TAIPAN dongle, BLAZE /
// X43, Smart Infinite keyboards) are deliberately kept even though nothing dispatches to them now —
// they carry hardware knowledge the docs reference. Wire one back up here when it is next needed.

using ProoveGaming.Device.Testing;

if (args.Length > 0 && args[0].Equals("blaze", StringComparison.OrdinalIgnoreCase))
{
	if (args.Length > 1 && args[1].Equals("slot7", StringComparison.OrdinalIgnoreCase))
	{
		await BlazeSlot7Probe.RunAsync(
			holdSeconds: args.Length > 2 && int.TryParse(args[2], out var hold7) ? hold7 : 20);
		return;
	}

	if (args.Length > 1 && args[1].Equals("frames", StringComparison.OrdinalIgnoreCase))
	{
		BlazeFrameDiffProbe.Run();
		return;
	}

	if (args.Length > 1 && args[1].Equals("replay", StringComparison.OrdinalIgnoreCase))
		await BlazeVendorReplayProbe.RunAsync(string.Join(' ', args.Skip(2)).ToLowerInvariant());
	else
		await BlazeEffectCycleProbe.RunAsync(
			holdSeconds: args.Length > 1 && int.TryParse(args[1], out var hold) ? hold : 10);
}
else if (args.Length > 0 && args[0].Equals("taipan", StringComparison.OrdinalIgnoreCase))
{
	// `taipan`        battery request/response (vendor answer, 2026-08-07)
	// `taipan config` button remap over the dongle, driving TelinkMouseService unmodified
	// `taipan wheel [fixed|legacy|clear]`  scroll direction (default: fixed = hardware-correct)
	// `taipan paths`  full HID enumeration — every interface, unfiltered, and which one we write to
	if (args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
		await TaipanPresenceProbe.RunAsync(
			phaseSeconds: args.Length > 2 && int.TryParse(args[^1], out var phase) ? phase : 16);
	else if (args.Length > 1 && args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
		await TaipanAllInterfacesProbe.RunAsync(
			testSeconds: args.Length > 2 && int.TryParse(args[^1], out var secs) ? secs : 10);
	else if (args.Length > 1 && args[1].Equals("wheel", StringComparison.OrdinalIgnoreCase))
		await TaipanWheelProbe.RunAsync(
			args.FirstOrDefault(a => a is "fixed" or "legacy" or "clear") ?? "fixed");
	else if (args.Length > 1 && args[1].Equals("ids", StringComparison.OrdinalIgnoreCase))
		// What InTheHand's BluetoothDevice.Id really looks like, next to what the shipping parser
		// makes of it and what the device tree holds. READ-ONLY.
		await BluetoothIdFormatProbe.RunAsync();
	else if (args.Length > 1 && args[1].Equals("presence", StringComparison.OrdinalIgnoreCase))
		// How fast does WINDOWS report a Bluetooth device gone? Polls BTHLE + BTHENUM device nodes
		// and prints only on change, so the delay between switching the device off and the
		// IsConnected flip is read straight off the timestamps. READ-ONLY.
		BluetoothPresenceWatchProbe.Run(
			// `taipan presence all` drops the filter, to see every Bluetooth node on the machine.
			nameFilter: args.Any(a => a.Equals("all", StringComparison.OrdinalIgnoreCase)) ? null : "taipan",
			seconds: args.Where(a => int.TryParse(a, out _)).Select(int.Parse).FirstOrDefault(120));
	else if (args.Length > 1 && args[1].Equals("paths", StringComparison.OrdinalIgnoreCase))
		TaipanPathProbe.Run();
	else if (args.Length > 2 && args[1].Equals("configmode", StringComparison.OrdinalIgnoreCase)
		&& args[2].Equals("battery", StringComparison.OrdinalIgnoreCase))
		// Battery over the dongle through the REAL parser, showing the app flag on each reply.
		TaipanConfigModeProbe.RunBatteryParseAsync();
	else if (args.Length > 2 && args[1].Equals("configmode", StringComparison.OrdinalIgnoreCase)
		&& args[2].Equals("freeze", StringComparison.OrdinalIgnoreCase))
		// One apply touching two tables — counts as ONE configuration-mode session, i.e. one freeze.
		await TaipanConfigModeProbe.RunApplyFreezeCountAsync();
	else if (args.Length > 2 && args[1].Equals("configmode", StringComparison.OrdinalIgnoreCase)
		&& args[2].Equals("service", StringComparison.OrdinalIgnoreCase))
		// The same remap, driven through the SHIPPING TelinkMouseService rather than hand-built
		// frames — proves the app path, not just the protocol.
		await TaipanConfigModeProbe.RunServiceAsync(
			restore: !args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase)));
	else if (args.Length > 1 && args[1].Equals("configmode", StringComparison.OrdinalIgnoreCase))
		// Tests the enter/exit configuration-mode handshake found in the vendor's 2026-09-02 Bus
		// Hound capture, which our writer has never sent. Stages A-C touch no flash and run by
		// default; the real table write needs --write. Always re-enables the mouse on exit.
		TaipanConfigModeProbe.Run(
			transport: args.FirstOrDefault(a => a is "wired" or "dongle") ?? "dongle",
			write: args.Any(a => a.Equals("--write", StringComparison.OrdinalIgnoreCase)),
			restore: !args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase)));
	else if (args.Length > 2 && args[1].Equals("response", StringComparison.OrdinalIgnoreCase)
		&& args[2].Equals("sweep", StringComparison.OrdinalIgnoreCase))
		TaipanConfigResponseProbe.Sweep();
	else if (args.Length > 1 && args[1].Equals("response", StringComparison.OrdinalIgnoreCase))
		TaipanConfigResponseProbe.Run(
			transport: args.FirstOrDefault(a => a is "wired" or "dongle") ?? "dongle",
			buttonName: args.FirstOrDefault(a => a is "right" or "wheel" or "forward" or "back") ?? "right",
			holdSeconds: args.Where(a => int.TryParse(a, out _)).Select(int.Parse).FirstOrDefault(20),
			restore: !args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase)));
	else if (args.Length > 1 && args[1].Equals("config", StringComparison.OrdinalIgnoreCase))
		TaipanDongleConfigProbe.Run(
			buttonName: args.FirstOrDefault(a => a is "right" or "wheel" or "forward" or "back") ?? "right",
			holdSeconds: args.Length > 2 && int.TryParse(args[^1], out var hold) ? hold : 25);
	else
		TaipanBatteryProbe.Run();
}
else if (args.Length > 0 && args[0].Equals("starlight", StringComparison.OrdinalIgnoreCase))
{
	// Sub-arms are read-only except `led`, which refuses to write without an explicit opt-in.
	var arm = args.Length > 1 ? args[1].ToLowerInvariant() : string.Empty;
	switch (arm)
	{
		case "audio":
			// Watches the Windows audio endpoints through mic-mute, wheel and a power cycle.
			// Answers power state, mic mute and wheel visibility in one run. READ-ONLY.
			StarlightAudioProbe.Run();
			break;
		case "battery":
			// PG-608 follow-up: is a battery or power state reachable over the DONGLE? Runs the two
			// things never tried — a full report-id sweep and a full device-property dump — phased
			// across headset on/off so a value that never moves is visibly not a battery.
			// READ-ONLY; report 0xFD is never written to.
			StarlightBatteryHuntProbe.Run(
				phased: !args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)));
			break;
		case "bt":
			// PG-608 Phase 0: is Starlight a classic-Bluetooth audio device like Cosmo, and does
			// a battery value exist as a Windows device property? Reuses the Cosmo device-tree
			// technique unchanged, matched on the endpoint name. READ-ONLY.
			ClassicBluetoothDiscoveryProbe.Run("starlight");
			break;
		case "power":
			// Phase 0c: does switching the headset off remove it from USB, or does it stay
			// enumerated like Cosmo's dongle? READ-ONLY.
			StarlightPowerProbe.Run();
			break;
		case "effects":
			// Varies the documented operation-mode byte and never sends Stop (which erases the
			// device's stored LED table). WRITES; refuses without --i-mean-it.
			StarlightEffectProbe.Run(
				allowWrites: args.Any(a => a.Equals("--i-mean-it", StringComparison.OrdinalIgnoreCase)),
				holdSeconds: args.Length > 2 && int.TryParse(args[2], out var eh) ? eh : 6);
			break;
		case "sweep":
			// Bounded matrix of LED command FRAMINGS — documented commands only, never unknown
			// opcodes. WRITES; refuses without --i-mean-it.
			StarlightLedSweep.Run(
				allowWrites: args.Any(a => a.Equals("--i-mean-it", StringComparison.OrdinalIgnoreCase)),
				holdSeconds: args.Length > 2 && int.TryParse(args[2], out var hold) ? hold : 5);
			break;
		case "buttons":
			// Listens on EVERY interface, via HidSharp and Raw Input at once, through guided
			// button phases. Read-only.
			StarlightButtonProbe.Run();
			break;
		case "xear":
			// Runs the customer-supplied C-Media Xear SDK against the live dongle and reports, per
			// property, whether it is OK / unsupported / license-gated. READ-ONLY — only getters.
			StarlightXearProbe.Run(
				sdkDir: args.Skip(2).FirstOrDefault(a =>
					!a.Equals("cosmo", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("--")),
				device: args.FirstOrDefault(a => a.Equals("cosmo", StringComparison.OrdinalIgnoreCase)),
				// --activate [guid] runs the vendor's documented CmActivateLicense with the GUID
				// shipped in CM002X1_AUSDOM_A3_PRO_GUID.txt, then re-reads the property matrix.
				activateGuid: args.Any(a => a.Equals("--activate", StringComparison.OrdinalIgnoreCase))
					? args.SkipWhile(a => !a.Equals("--activate", StringComparison.OrdinalIgnoreCase))
						.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? string.Empty
					: null);
			break;
		case "rgb-oracle":
		{
			// Documented LED commands only, each followed by an oracle read that says whether the
			// firmware parsed it. WRITES; refuses without --i-mean-it. Never sends 0x02 or 0x11.
			var hex = args.SkipWhile(a => !a.Equals("--colour", StringComparison.OrdinalIgnoreCase))
				.Skip(1).FirstOrDefault();
			byte r = 0x00, g = 0xFF, b = 0x00;
			if (hex is { Length: 6 })
			{
				r = Convert.ToByte(hex[..2], 16);
				g = Convert.ToByte(hex[2..4], 16);
				b = Convert.ToByte(hex[4..], 16);
			}

			StarlightRgbOracleProbe.Run(
				allowWrites: args.Any(a => a.Equals("--i-mean-it", StringComparison.OrdinalIgnoreCase)),
				r, g, b);
		}
			break;
		case "console":
			// Interactive capability console: coprocessor probe, live audio changes, RGB verdict.
			StarlightConsole.Run();
			break;
		case "xear-write":
			// The license-free half of the SDK, exercised as SETTERS: mute, volume, stream format,
			// enhancements switch, default-device role, channel test tone. Every write is read back
			// and restored. WRITES; refuses without --i-mean-it.
			StarlightXearWriteProbe.Run(
				allowWrites: args.Any(a => a.Equals("--i-mean-it", StringComparison.OrdinalIgnoreCase)),
				sdkDir: args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")));
			break;
		case "watch":
			StarlightProbe.RunWatch();
			break;
		case "read":
			StarlightProbe.RunRead(args.Length > 2 && int.TryParse(args[2], out var samples) ? samples : 10);
			break;
		case "led":
		{
			// --rgb RRGGBB sets the Colour Array payload; defaults to red.
			var hex = args.SkipWhile(a => !a.Equals("--rgb", StringComparison.OrdinalIgnoreCase))
				.Skip(1).FirstOrDefault();
			byte r = 0xFF, g = 0x00, b = 0x00;
			if (hex is { Length: 6 })
			{
				r = Convert.ToByte(hex[..2], 16);
				g = Convert.ToByte(hex[2..4], 16);
				b = Convert.ToByte(hex[4..], 16);
			}

			StarlightProbe.RunLed(
				allowWrites: args.Any(a => a.Equals("--i-mean-it", StringComparison.OrdinalIgnoreCase)),
				variant: args.FirstOrDefault(a =>
					a is "envelope" or "native" or "short" or "direct") ?? "direct",
				r, g, b,
				step: args.Any(a => a.Equals("--step", StringComparison.OrdinalIgnoreCase)));
		}
			break;
		default:
			StarlightProbe.Run();
			break;
	}
}
else if (args.Length > 0 && args[0].Equals("cosmo-bt", StringComparison.OrdinalIgnoreCase))
{
	if (args.Length > 1 && args[1].Equals("discovery", StringComparison.OrdinalIgnoreCase))
		ClassicBluetoothDiscoveryProbe.Run();
	else
		await CosmoBluetoothAudioProbe.RunAsync();
}
else if (args.Length > 0 && args[0].Equals("rampage-bt", StringComparison.OrdinalIgnoreCase))
	await RampageBtProbe.RunAsync(
		allowWrites: args.Length > 1 && args[1].Equals("write", StringComparison.OrdinalIgnoreCase));
else if (args.Length > 0 && args[0].Equals("rampage-24g", StringComparison.OrdinalIgnoreCase))
{
	if (args.Length > 1 && args[1].Equals("keys", StringComparison.OrdinalIgnoreCase))
		await Rampage24gProbe.RunKeysAsync(
			allowWrites: args.Any(a => a.Equals("write", StringComparison.OrdinalIgnoreCase)));
	else
		await Rampage24gProbe.RunAsync(
			allowWrites: args.Length > 1 && args[1].Equals("write", StringComparison.OrdinalIgnoreCase));
}
else if (args.Length > 0 && args[0].Equals("last-arrival", StringComparison.OrdinalIgnoreCase))
	// Does DEVPKEY_Device_LastArrivalDate on a BTHLE node move on every reconnect, and does a
	// stale ghost pairing carry a weeks-old value? Gates docs/plans/add-stale-pairing-detection/.
	// READ-ONLY.
	LastArrivalDateProbe.Run(
		nameFilter: args.Skip(1).FirstOrDefault(a => !int.TryParse(a, out _)),
		seconds: args.Where(a => int.TryParse(a, out _)).Select(int.Parse).FirstOrDefault(120));
else if (args.Length > 0 && args[0].Equals("identity", StringComparison.OrdinalIgnoreCase))
	// Read-only enumeration of every HID interface, optionally filtered to one VID
	// (`identity 0d8c`). Used to identify a newly-added device.
	HidIdentityProbe.Run(args.Length > 1 ? Convert.ToInt32(args[1], 16) : null);
else if (args.Length > 0 && args[0].Equals("descriptor", StringComparison.OrdinalIgnoreCase))
	CosmoProbe.Run();
else if (args.Length > 0 && args[0].Equals("bridge", StringComparison.OrdinalIgnoreCase))
	CosmoBridgeProbe.Run(probeVendorChannel: args.Length > 1 && args[1].Equals("bc", StringComparison.OrdinalIgnoreCase));
else if (args.Length > 0 && args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
	CosmoPokeProbe.RunWatch();
else if (args.Length > 0 && args[0].Equals("poke", StringComparison.OrdinalIgnoreCase))
	CosmoPokeProbe.Run(allowWrites: args.Length > 1 && args[1].Equals("write", StringComparison.OrdinalIgnoreCase));
else if (args.Length > 0 && args[0].Equals("cosmoled", StringComparison.OrdinalIgnoreCase))
	// Drives the headset LEDs over the dongle using the CM002X1 sequence read out of the dongle's
	// own firmware source. WRITES — refuses to run without the opt-in flag.
	CosmoLedProbe.Run(
		allowWrites: args.Any(a => a.Equals("--i-mean-it", StringComparison.OrdinalIgnoreCase)),
		reportId: args.Any(a => a.Equals("fd", StringComparison.OrdinalIgnoreCase)) ? (byte)0xFD : null);
else
	CosmoStatusProbe.Run();
