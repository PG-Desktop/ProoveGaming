using System.Buffers.Binary;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Codec for the Smart Infinite key travel area — the per-key actuation table of a Cyrex / Aress
/// magnetic board, read with <see cref="SmartInfiniteProtocol.CmdReadTravelArea"/> and written
/// whole with <see cref="SmartInfiniteProtocol.CmdWriteTravelArea"/>. Pure functions only, so the
/// wire format is unit-testable without hardware.
/// </summary>
/// <remarks>
/// <para>One record per slot, <c>TravelRecordBytes</c> long (10, or 12 when the board has a top
/// dead zone), all fields uint16 little-endian in device steps:</para>
/// <code>
/// +0  DOWN              actuation point
/// +2  UP                release point
/// +4  QUICKLY_DOWN      RT quick press; bits 15-14 = 0b01 flag RT active, value in bits 13-0
/// +6  QUICKLY_UP        RT quick release
/// +8  BOTTOM_DEAD_ZONE  bits 15-12 = axis (switch) type nibble, value in bits 11-0
/// +10 TOP_DEAD_ZONE     only on 12-byte records
/// </code>
/// <para>
/// This codec deliberately models only the four values the App edits. A patch preserves the axis
/// nibble, both dead zones and any field it does not understand byte-for-byte — the area is the
/// firmware's own table and inventing bytes we did not read is how boards get scrambled. Source:
/// <c>docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md</c> (extracted from the
/// vendor's Keyborder-v3 app, not yet hardware-verified — which is why the geometry gate exists).
/// </para>
/// </remarks>
public static class SmartInfiniteTravelArea
{
	public const int DownOffset = 0;
	public const int UpOffset = 2;
	public const int QuickDownOffset = 4;
	public const int QuickUpOffset = 6;
	public const int BottomDeadZoneOffset = 8;

	/// <summary>Record size without a top dead zone; byte 44 of Device Info reports 0 for it.</summary>
	public const int MinRecordBytes = 10;

	/// <summary>RT flag in the QUICKLY_DOWN field: bits 15-14 carry <c>0b01</c> when RT is active.</summary>
	private const int RtFlagMask = 0xC000;
	private const int RtFlagActive = 0x4000;
	private const int RtValueMask = 0x3FFF;

	/// <summary>
	/// True when the Device Info magnetic fields describe a coherent travel area this codec can
	/// safely operate on. Anything else means a mechanical board, an unexpected firmware revision
	/// or a misread — and per the safety gate, no write may ever be attempted.
	/// </summary>
	public static bool ValidateGeometry(SmartInfiniteDeviceInfo info, int areaLength)
	{
		return info.IsValid
			&& info.TravelSlots > 0
			&& info.TravelRecordBytes >= MinRecordBytes
			&& info.TravelStepMicrometres > 0
			&& info.TravelAreaBytes == areaLength
			&& info.PressMaxSteps > info.PressMinSteps
			&& info.PressMinSteps >= 0
			&& info.RtMaxSteps > info.RtMinSteps;
	}

	/// <summary>Converts device steps to hundredths of a millimetre, rounding to nearest.</summary>
	public static int StepsToCentiMm(int steps, int stepMicrometres) =>
		(steps * stepMicrometres + 5) / 10;

	/// <summary>Converts hundredths of a millimetre to device steps, rounding to nearest.</summary>
	public static int CentiMmToSteps(int centiMm, int stepMicrometres) =>
		(centiMm * 10 + stepMicrometres / 2) / stepMicrometres;

	/// <summary>Reads one slot's edited values, in device steps.</summary>
	public static (int PressSteps, int ReleaseSteps, bool RapidTrigger, int QuickPressSteps, int QuickReleaseSteps)
		ReadSlot(ReadOnlySpan<byte> area, int recordBytes, int slot)
	{
		var record = area.Slice(slot * recordBytes, recordBytes);
		int quickDownRaw = BinaryPrimitives.ReadUInt16LittleEndian(record[QuickDownOffset..]);

		return (
			PressSteps: BinaryPrimitives.ReadUInt16LittleEndian(record[DownOffset..]),
			ReleaseSteps: BinaryPrimitives.ReadUInt16LittleEndian(record[UpOffset..]),
			RapidTrigger: (quickDownRaw & RtFlagMask) == RtFlagActive,
			QuickPressSteps: quickDownRaw & RtValueMask,
			QuickReleaseSteps: BinaryPrimitives.ReadUInt16LittleEndian(record[QuickUpOffset..]));
	}

	/// <summary>
	/// Writes the four edited values into every slot, in place. The BOTTOM_DEAD_ZONE field (axis
	/// nibble + dead-zone value), the TOP_DEAD_ZONE and anything past the modelled fields are left
	/// untouched — this is the modify step of a read-modify-write over the table the device gave us.
	/// </summary>
	public static void PatchAllSlots(
		Span<byte> area, int recordBytes, int pressSteps, int releaseSteps,
		bool rapidTrigger, int quickPressSteps, int quickReleaseSteps)
	{
		int quickDownRaw = (quickPressSteps & RtValueMask) | (rapidTrigger ? RtFlagActive : 0);

		for (int offset = 0; offset + recordBytes <= area.Length; offset += recordBytes)
		{
			var record = area.Slice(offset, recordBytes);
			BinaryPrimitives.WriteUInt16LittleEndian(record[DownOffset..], (ushort)pressSteps);
			BinaryPrimitives.WriteUInt16LittleEndian(record[UpOffset..], (ushort)releaseSteps);
			BinaryPrimitives.WriteUInt16LittleEndian(record[QuickDownOffset..], (ushort)quickDownRaw);
			BinaryPrimitives.WriteUInt16LittleEndian(record[QuickUpOffset..], (ushort)quickReleaseSteps);
		}
	}

	/// <summary>
	/// Writes the four edited values into the listed slots only, in place — the per-key counterpart
	/// of <see cref="PatchAllSlots"/>, with the same preservation guarantees for every field it does
	/// not model and for every slot it was not asked to touch. Slots outside the area are ignored.
	/// </summary>
	public static void PatchSlots(
		Span<byte> area, int recordBytes, IReadOnlyCollection<int> slots, int pressSteps,
		int releaseSteps, bool rapidTrigger, int quickPressSteps, int quickReleaseSteps)
	{
		int quickDownRaw = (quickPressSteps & RtValueMask) | (rapidTrigger ? RtFlagActive : 0);
		int slotCount = area.Length / recordBytes;

		foreach (int slot in slots)
		{
			if (slot < 0 || slot >= slotCount) continue;

			var record = area.Slice(slot * recordBytes, recordBytes);
			BinaryPrimitives.WriteUInt16LittleEndian(record[DownOffset..], (ushort)pressSteps);
			BinaryPrimitives.WriteUInt16LittleEndian(record[UpOffset..], (ushort)releaseSteps);
			BinaryPrimitives.WriteUInt16LittleEndian(record[QuickDownOffset..], (ushort)quickDownRaw);
			BinaryPrimitives.WriteUInt16LittleEndian(record[QuickUpOffset..], (ushort)quickReleaseSteps);
		}
	}

	/// <summary>Decodes every slot's edited values, in device steps, keyed by slot.</summary>
	public static Dictionary<int, (int PressSteps, int ReleaseSteps, bool RapidTrigger, int QuickPressSteps, int QuickReleaseSteps)>
		ReadAllSlots(ReadOnlySpan<byte> area, int recordBytes)
	{
		int slots = area.Length / recordBytes;
		var map = new Dictionary<int, (int, int, bool, int, int)>(slots);
		for (int slot = 0; slot < slots; slot++)
			map[slot] = ReadSlot(area, recordBytes, slot);
		return map;
	}

	/// <summary>
	/// Reduces a per-key table to the single set of values the global editor shows: the value held
	/// by the majority of slots, field by field. A board the vendor app configured per-key still
	/// gets a sensible reading; the next apply from our UI makes it uniform, which is the documented
	/// behaviour of a global-only editor.
	/// </summary>
	public static (int PressSteps, int ReleaseSteps, bool RapidTrigger, int QuickPressSteps, int QuickReleaseSteps)
		ReduceMajority(ReadOnlySpan<byte> area, int recordBytes)
	{
		int slots = area.Length / recordBytes;
		var press = new Dictionary<int, int>();
		var release = new Dictionary<int, int>();
		var quickPress = new Dictionary<int, int>();
		var quickRelease = new Dictionary<int, int>();
		int rtVotes = 0;

		for (int slot = 0; slot < slots; slot++)
		{
			var (p, r, rt, qp, qr) = ReadSlot(area, recordBytes, slot);
			Vote(press, p);
			Vote(release, r);
			Vote(quickPress, qp);
			Vote(quickRelease, qr);
			if (rt) rtVotes++;
		}

		return (Winner(press), Winner(release), rtVotes * 2 > slots, Winner(quickPress), Winner(quickRelease));

		static void Vote(Dictionary<int, int> votes, int value)
		{
			votes.TryGetValue(value, out int count);
			votes[value] = count + 1;
		}

		static int Winner(Dictionary<int, int> votes)
		{
			int best = 0, bestCount = -1;
			foreach (var (value, count) in votes)
				if (count > bestCount) { best = value; bestCount = count; }
			return best;
		}
	}
}
