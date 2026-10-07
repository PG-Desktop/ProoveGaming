namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Framing for the Rongyuan RY5088 vendor protocol (T800), shared by the lighting and key-matrix
/// services.
/// </summary>
/// <remarks>
/// <para>
/// Commands are HID <b>feature</b> reports on the <c>mi_02</c> vendor interface (usage page
/// <c>0xFFFF</c> / usage <c>0x02</c>), report ID <c>0x00</c>, 64 protocol bytes — so a HidSharp
/// buffer is 65 bytes and protocol byte <c>N</c> lives at index <c>N + 1</c>.
/// </para>
/// <para>
/// Everything here was measured against hardware, not taken from the vendor documents, which
/// define neither the checksum nor the key-record layout. See
/// <c>docs/knowledge-base/royuan-led-encoding-notes.md</c> and
/// <c>docs/plans/add-t800-key-assignments/notes.md</c>.
/// </para>
/// </remarks>
public static class Ry5088Protocol
{
	/// <summary>HidSharp buffer length: 64 protocol bytes plus the leading report-ID byte.</summary>
	public const int ReportLength = 65;

	public const byte HidReportId = 0x00;

	public const byte CmdSetKeyMatrix = 0x0A;
	public const byte CmdGetKeyMatrix = 0x8A;

	public const byte CmdSetMacro = 0x0B;
	public const byte CmdGetMacro = 0x8B;

	// Magnetic-axis trip (actuation) — §1/§2 of the RY5088 doc. Values are hundredths of a
	// millimetre at the default precision (0.1 mm = 10, 4.0 mm = 400); the actual precision comes
	// from the function-list read (0xE6). See docs/knowledge-base/ry5088-magnetic-axis-protocol.md.
	public const byte CmdSetTrip = 0x65;
	public const byte CmdGetTrip = 0xE5;
	public const byte CmdGetFunctionList = 0xE6;

	public const byte TripOptionPress = 0;
	public const byte TripOptionRelease = 1;
	public const byte TripOptionRtPress = 2;
	public const byte TripOptionRtRelease = 3;
	public const byte TripOptionKeyMode = 7;

	/// <summary>Key-mode value for a plain key; RT is the same mode with <see cref="KeyModeRapidTriggerFlag"/> OR-ed in.</summary>
	public const byte KeyModeNormal = 0;
	public const byte KeyModeRapidTriggerFlag = 0x80;

	/// <summary>
	/// Bulk-set paging (<c>Select = 1</c>): two-byte options carry 28 keys per report, one-byte
	/// options (key mode etc.) 56, covering all 128 slots in 5 or 3 pages respectively.
	/// </summary>
	public const int TripKeysPerPageTwoByte = 28;
	public const int TripKeysPerPageOneByte = 56;

	/// <summary>Whole-table addressing for <see cref="CmdGetKeyMatrix"/>; a slot number addresses one key.</summary>
	public const byte TypeWholeMatrix = 255;

	/// <summary>Matrix geometry: 512 bytes of 4-byte records, read back as 8 pages of 64 bytes.</summary>
	public const int MatrixSlots = 128;
	public const int RecordBytes = 4;
	public const int MatrixPages = 8;
	public const int PageBytes = 64;

	/// <summary>
	/// Macro geometry: 64 on-device slots of 256 bytes each, written in 56-byte pages
	/// (<see cref="CmdSetMacro"/>) and read back in 64-byte pages (<see cref="CmdGetMacro"/>) —
	/// the asymmetry is the vendor doc's, not ours (write pages carry a header with a Len field,
	/// read replies are raw pages like <see cref="CmdGetKeyMatrix"/> replies).
	/// </summary>
	public const int MacroSlots = 64;
	public const int MacroBytes = 256;
	public const int MacroWritePageBytes = 56;
	public const int MacroReadPageBytes = 64;
	public const int MacroReadPages = 4;

	/// <summary>Key-matrix record type byte for "this slot plays a macro": <c>[0x09, playType, slot, 0]</c>.</summary>
	/// <remarks>
	/// Note the operand order — code1 is the <b>play type</b> and code2 the <b>library slot</b>,
	/// the mirror image of the Raster / Smart Infinite records where code1 is the slot. Play
	/// types per the vendor key-code table: <c>0</c> plays the blob's repeat count and stops on
	/// any key press, <c>1</c> loops until any key press, <c>2</c> plays while held and stops on
	/// release. See <c>docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md</c>.
	/// </remarks>
	public const byte TypeMacro = 0x09;
	public const byte MacroPlayTypeRepeat = 0;
	public const byte MacroPlayTypeAlways = 1;
	public const byte MacroPlayTypeWhileHeld = 2;

	/// <summary>
	/// Checksum: the <b>one's complement</b> of the sum, <c>0xFF - (sum &amp; 0xFF)</c>.
	/// </summary>
	/// <remarks>
	/// Not a plain sum and not the two's complement — a plain sum is off by exactly one, and the
	/// firmware discards a packet with a wrong checksum <b>silently</b>: no ack, no error, and a
	/// subsequent read returns the driver's cached copy of the request rather than device state.
	/// That combination is what made two earlier attempts at this device fail invisibly.
	/// <para>
	/// Two variants differ only in the range covered and where the byte lands: bytes 0..6 stored at
	/// byte 7 for most commands, bytes 0..7 stored at byte 8 for the seven-parameter
	/// <c>SET_LEDPARAM</c> / <c>SET_SLEDPARAM</c>.
	/// </para>
	/// </remarks>
	/// <param name="packet">Full HidSharp buffer, including the report-ID byte at index 0.</param>
	/// <param name="fromInclusive">First buffer index to include.</param>
	/// <param name="toInclusive">Last buffer index to include. The checksum byte is never in range.</param>
	public static byte Checksum(byte[] packet, int fromInclusive, int toInclusive)
	{
		int sum = 0;
		for (int i = fromInclusive; i <= toInclusive; i++)
			sum += packet[i];
		return (byte)(0xFF - (sum & 0xFF));
	}

	/// <summary>
	/// <c>GET_KEYMATRIX</c> for one page of the whole table:
	/// <c>0x8A | Profile | Type=255 | Page | Index | 00 | 00 | CS</c>.
	/// </summary>
	/// <remarks>
	/// The reply is 64 bytes of raw matrix data with <b>no echoed header</b> — the first record
	/// starts at protocol byte 0.
	/// </remarks>
	public static byte[] BuildGetKeyMatrixPage(byte page, byte profile = 0, byte layer = 0)
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdGetKeyMatrix;
		packet[2] = profile;
		packet[3] = TypeWholeMatrix;
		packet[4] = page;
		packet[5] = layer;
		packet[8] = Checksum(packet, 1, 7);
		return packet;
	}

	/// <summary>
	/// <c>SET_KEYMATRIX</c> for a single key:
	/// <c>0x0A | Profile | Type=slot | Page=0 | Len=4 | FlashEnable | Index | CS | record</c>.
	/// </summary>
	/// <remarks>
	/// Addressing one slot rather than the whole table is what keeps this safe: verified on hardware
	/// to change exactly the slot named and nothing else, so — unlike the Smart Infinite family —
	/// there is no read-modify-write of the full table and no way for a failed read to corrupt keys
	/// the user never touched. <paramref name="flashEnable"/> persists the change; without it the
	/// firmware buffers and the remap is lost on replug.
	/// </remarks>
	public static byte[] BuildSetKeyRecord(
		byte slot, byte type, byte code1, byte code2, bool flashEnable = true, byte profile = 0, byte layer = 0)
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdSetKeyMatrix;
		packet[2] = profile;
		packet[3] = slot;
		packet[4] = 0;
		packet[5] = RecordBytes;
		packet[6] = flashEnable ? (byte)1 : (byte)0;
		packet[7] = layer;
		packet[8] = Checksum(packet, 1, 7);
		packet[9] = type;
		packet[10] = code1;
		packet[11] = code2;
		// packet[12] is the record's fourth byte, zero for every record the factory table carries.
		return packet;
	}

	/// <summary>
	/// <c>SET_MACRO</c> for one page of a macro blob:
	/// <c>0x0B | Macro | Page | Len | End | 00 | 00 | CS | payload…</c>.
	/// </summary>
	/// <remarks>
	/// <paramref name="end"/> is the vendor's "macro end" flag and must be set on the <b>last</b>
	/// page of each macro — it is this command's analogue of the <c>FlashEnable</c> byte the
	/// key-matrix and trip commands carry.
	/// </remarks>
	public static byte[] BuildSetMacroPage(byte macroIndex, byte page, ReadOnlySpan<byte> payload, bool end)
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdSetMacro;
		packet[2] = macroIndex;
		packet[3] = page;
		packet[4] = (byte)Math.Min(payload.Length, MacroWritePageBytes);
		packet[5] = end ? (byte)1 : (byte)0;
		packet[8] = Checksum(packet, 1, 7);
		for (int i = 0; i < payload.Length && 9 + i < ReportLength; i++)
			packet[9 + i] = payload[i];
		return packet;
	}

	/// <summary>
	/// <c>GET_MACRO</c> for one 64-byte page of a macro blob:
	/// <c>0x8B | Macro | Page | 00 | 00 | 00 | 00 | CS</c>. The reply carries raw blob bytes with
	/// no echoed header, like <see cref="BuildGetKeyMatrixPage"/> replies.
	/// </summary>
	public static byte[] BuildGetMacroPage(byte macroIndex, byte page)
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdGetMacro;
		packet[2] = macroIndex;
		packet[3] = page;
		packet[8] = Checksum(packet, 1, 7);
		return packet;
	}

	/// <summary>Pages a bulk trip transfer needs for the given per-page key count.</summary>
	public static int TripPageCount(int keysPerPage) =>
		(MatrixSlots + keysPerPage - 1) / keysPerPage;

	/// <summary>
	/// The ascending, deduplicated page indices a per-key trip write must touch to cover the given
	/// slots. Pages are independently addressed, so writing only these keeps a per-key apply off the
	/// flash pages it does not change. Slots outside the matrix are ignored.
	/// </summary>
	public static int[] TripPagesContaining(IReadOnlyCollection<int> slots, int keysPerPage)
	{
		var pages = new SortedSet<int>();
		foreach (int slot in slots)
			if (slot >= 0 && slot < MatrixSlots)
				pages.Add(slot / keysPerPage);

		var result = new int[pages.Count];
		pages.CopyTo(result);
		return result;
	}

	/// <summary>
	/// <c>0x65</c> bulk set of a two-byte trip option for one page of keys:
	/// <c>0x65 | Option | Select=1 | Index=page | FlashEnable | 00 | 00 | CS | values…</c>.
	/// Values are uint16 little-endian, <see cref="TripKeysPerPageTwoByte"/> keys per page in slot
	/// order; the final page carries the remainder.
	/// </summary>
	/// <remarks>
	/// <paramref name="flashEnable"/> must be set on the <b>last</b> command of a batch only: the
	/// firmware buffers until it sees the flag, so a multi-option apply costs one flash commit
	/// rather than one per packet.
	/// </remarks>
	public static byte[] BuildSetTripPageTwoByte(byte option, byte page, ReadOnlySpan<ushort> values, bool flashEnable)
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdSetTrip;
		packet[2] = option;
		packet[3] = 1;
		packet[4] = page;
		packet[5] = flashEnable ? (byte)1 : (byte)0;
		packet[8] = Checksum(packet, 1, 7);
		for (int i = 0; i < values.Length && 9 + i * 2 + 1 < ReportLength; i++)
		{
			packet[9 + i * 2] = (byte)values[i];
			packet[10 + i * 2] = (byte)(values[i] >> 8);
		}
		return packet;
	}

	/// <summary>
	/// <c>0x65</c> bulk set of a one-byte trip option (key mode etc.) for one page of keys —
	/// <see cref="TripKeysPerPageOneByte"/> keys per page, one byte each.
	/// </summary>
	public static byte[] BuildSetTripPageOneByte(byte option, byte page, ReadOnlySpan<byte> values, bool flashEnable)
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdSetTrip;
		packet[2] = option;
		packet[3] = 1;
		packet[4] = page;
		packet[5] = flashEnable ? (byte)1 : (byte)0;
		packet[8] = Checksum(packet, 1, 7);
		for (int i = 0; i < values.Length && 9 + i < ReportLength; i++)
			packet[9 + i] = values[i];
		return packet;
	}

	/// <summary>
	/// <c>0xE5</c> bulk read of one trip-option page:
	/// <c>0xE5 | Option | Select=1 | Index=page | 00 | 00 | 00 | CS</c>. The reply carries raw
	/// per-key data with no echoed header, like <see cref="BuildGetKeyMatrixPage"/> replies.
	/// </summary>
	public static byte[] BuildGetTripPage(byte option, byte page)
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdGetTrip;
		packet[2] = option;
		packet[3] = 1;
		packet[4] = page;
		packet[8] = Checksum(packet, 1, 7);
		return packet;
	}

	/// <summary>
	/// <c>0xE6</c> function-list read; response byte 1 is the trip precision
	/// (<c>0</c> = 0.01 mm, <c>1</c> = 0.005 mm, <c>2</c> = 0.001 mm) behind an <c>0xAA</c> marker.
	/// </summary>
	public static byte[] BuildGetFunctionList()
	{
		var packet = new byte[ReportLength];
		packet[1] = CmdGetFunctionList;
		packet[8] = Checksum(packet, 1, 7);
		return packet;
	}

	/// <summary>Trip precision in micrometres from the function-list response, 0 when unreadable.</summary>
	public static int ParseTripPrecisionMicrometres(byte marker, byte precision) =>
		marker != 0xAA ? 0 : precision switch { 0 => 10, 1 => 5, 2 => 1, _ => 0 };

	/// <summary>
	/// Device trip units (sized by <see cref="ParseTripPrecisionMicrometres"/>) → hundredths of a
	/// millimetre, rounding to nearest. At the default 0.01 mm precision the two are identical.
	/// </summary>
	public static int TripUnitsToCentiMm(int units, int precisionUm) =>
		(units * precisionUm + 5) / 10;

	/// <summary>Hundredths of a millimetre → device trip units, rounding to nearest.</summary>
	public static int CentiMmToTripUnits(int centiMm, int precisionUm) =>
		(centiMm * 10 + precisionUm / 2) / precisionUm;

	/// <summary>
	/// Decodes a 512-byte matrix into <c>slot → record</c>, skipping slots the board does not use.
	/// </summary>
	/// <remarks>
	/// The fourth byte of each record is dropped: it is <c>0x00</c> throughout the factory table, so
	/// the App's 3-byte <see cref="KeyMatrixRecord"/> carries these without loss.
	/// </remarks>
	public static Dictionary<byte, KeyMatrixRecord> ParseMatrix(byte[] matrix)
	{
		var map = new Dictionary<byte, KeyMatrixRecord>();
		int slots = Math.Min(MatrixSlots, matrix.Length / RecordBytes);

		for (int slot = 0; slot < slots; slot++)
		{
			int offset = slot * RecordBytes;
			if (matrix[offset] == 0 && matrix[offset + 1] == 0 &&
			    matrix[offset + 2] == 0 && matrix[offset + 3] == 0)
				continue;

			map[(byte)slot] = new KeyMatrixRecord(matrix[offset], matrix[offset + 1], matrix[offset + 2]);
		}

		return map;
	}

	/// <summary>
	/// True when a decoded matrix looks like a real key table rather than a failed read.
	/// </summary>
	/// <remarks>
	/// A rejected command leaves the caller holding its own request echoed back, which parses
	/// perfectly well into a handful of nonsense slots. The board carries 68 keys, so anything far
	/// below that is a read that did not happen — and the caller must treat it as "unknown", never
	/// as "the board has no keys".
	/// </remarks>
	public static bool IsPlausibleKeyTable(IReadOnlyDictionary<byte, KeyMatrixRecord> map) =>
		map.Count >= MinPlausibleKeys;

	/// <summary>Fewest decoded slots a genuine read can produce. The board has 68 real keys.</summary>
	public const int MinPlausibleKeys = 32;
}
