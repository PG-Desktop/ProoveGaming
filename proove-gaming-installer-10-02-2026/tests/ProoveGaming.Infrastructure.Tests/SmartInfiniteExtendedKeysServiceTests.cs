using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// SmartInfiniteExtendedKeysService against a scripted fake board, through the ISmartInfiniteIo seam.
// Pins the two-region write (area 0x3B before the full 384-byte matrix 0x18), the factory-triplet
// restore, byte-for-byte preservation of records the service does not manage, and every refusal
// that must leave the board untouched.
public class SmartInfiniteExtendedKeysServiceTests
{
	private const int MatrixSlots = 75;
	private const int FnSlot = 70;
	private const int SlotA = 31;
	private const int SlotD = 33;
	private static readonly int[] Placeholders = [42, 46, 63, 64, 65, 67, 68];

	// ── Fake firmware ────────────────────────────────────────────────────────────
	private sealed class FakeBoard
	{
		public int OpenAttempts { get; private set; }
		public int SessionsOpened { get; private set; }
		public int AreaReadPackets { get; private set; }
		public int AreaWritePackets { get; private set; }
		public int MatrixWritePackets { get; private set; }
		public int MatrixBytesWritten { get; private set; }
		// The order write commands arrived in — the area must precede the matrix.
		public List<byte> WriteOrder { get; } = [];
		// One entry per whole-table transfer, recorded when its first chunk (addr 0) arrives. Unlike
		// WriteOrder this stays unambiguous when a read repairs the board and a write follows it.
		public List<byte> Transfers { get; } = [];

		public byte AreaUnits { get; init; } = 4;
		public bool DiscardWrites { get; init; }
		// The transport cannot get a handle at all — a dead cable, not a verdict about the board.
		public bool RefuseOpen { get; init; }
		// The board is there but never answers the identity block.
		public bool SilentDeviceInfo { get; init; }
		// The current table arrives, but as something that is not a key matrix.
		public bool ScrambleCurrentTable { get; init; }
		// One lost packet, scripted: on the Nth whole-area transfer (1-based, 0 = never) the chunk at
		// address 0 is acked like every other and never lands. That is the single dropped area chunk
		// review m3 asks for, and it has to be per-transfer rather than a flag: DiscardWrites makes the
		// board refuse every write forever, which can only ever produce the negative case. Address 0 is
		// the chunk that matters because the records live at the start of the area — a later chunk
		// carries only the zero padding, which VerifyWrite deliberately does not check.
		public int DropFirstAreaChunkOfTransfer { get; init; }
		// Whole-area transfers seen so far, counted when their first chunk arrives.
		public int AreaTransfers { get; private set; }

		public byte[] Factory { get; } = new byte[SmartInfiniteProtocol.MaxTableBytes];
		public byte[] Current { get; } = new byte[SmartInfiniteProtocol.MaxTableBytes];
		public byte[] Area { get; }

		public FakeBoard()
		{
			Area = new byte[AreaUnits * 64];
			for (int slot = 0; slot < MatrixSlots; slot++)
				SmartInfiniteProtocol.PatchSlot(Factory, slot, new KeyMatrixRecord(0x10, 0x00, (byte)(slot + 4)));
			SmartInfiniteProtocol.PatchSlot(Factory, 45, new KeyMatrixRecord(0x10, 0x02, 0x00));   // Left Shift
			SmartInfiniteProtocol.PatchSlot(Factory, FnSlot, new KeyMatrixRecord(0x60, 0x01, 0x00));
			foreach (int slot in Placeholders)
				SmartInfiniteProtocol.PatchSlot(Factory, slot, new KeyMatrixRecord(0x10, 0x00, 0x00));
			Factory.CopyTo(Current, 0);
		}

		public KeyMatrixRecord Slot(int slot) => SmartInfiniteProtocol.ReadSlot(Current, slot);

		public void PlaceRecord(int offset, byte[] bytes, params int[] slots)
		{
			bytes.CopyTo(Area, offset);
			foreach (int slot in slots)
				SmartInfiniteProtocol.PatchSlot(Current, slot, SmartInfiniteAdvancedKeyArea.PointerRecord(bytes[0], offset));
		}

		// Gives a slot a different key in both tables — a board whose key really is that key, factory
		// identity and current record alike, rather than a remapped one.
		public void PatchFactoryKey(int slot, KeyMatrixRecord record)
		{
			SmartInfiniteProtocol.PatchSlot(Factory, slot, record);
			SmartInfiniteProtocol.PatchSlot(Current, slot, record);
		}

		// Points a slot at an area offset without touching the area — the two halves of a record can
		// then be made to disagree, which is what a torn two-region write leaves behind.
		public void PointSlotAt(int slot, byte type, int offset) =>
			SmartInfiniteProtocol.PatchSlot(Current, slot, SmartInfiniteAdvancedKeyArea.PointerRecord(type, offset));

		public ISmartInfiniteIo? Open()
		{
			OpenAttempts++;
			if (RefuseOpen) return null;
			SessionsOpened++;
			return new Session(this);
		}

		private sealed class Session : ISmartInfiniteIo
		{
			private readonly FakeBoard _board;
			private readonly Queue<byte[]> _replies = new();

			public Session(FakeBoard board) => _board = board;

			public void Write(byte[] request)
			{
				var cmd = request[1];
				int addr = request[2] | (request[3] << 8);
				int len = request[4];

				switch (cmd)
				{
					case SmartInfiniteProtocol.CmdReadDeviceInfo:
						// No reply enqueued: the next Read throws, which is how a silent board looks.
						if (_board.SilentDeviceInfo) break;
						Reply(cmd, addr, DeviceInfoBlock().AsSpan(addr, len));
						break;
					case SmartInfiniteProtocol.CmdReadDefaultKeyInfo:
						Reply(cmd, addr, _board.Factory.AsSpan(addr, len));
						break;
					case SmartInfiniteProtocol.CmdReadKeyInfo:
						if (_board.ScrambleCurrentTable)
						{
							Reply(cmd, addr, Enumerable.Repeat((byte)0xFF, len).ToArray());
							break;
						}
						Reply(cmd, addr, _board.Current.AsSpan(addr, len));
						break;
					case SmartInfiniteProtocol.CmdSetKeyInfo:
						if (!_board.DiscardWrites)
							request.AsSpan(8, len).CopyTo(_board.Current.AsSpan(addr));
						_board.MatrixWritePackets++;
						_board.MatrixBytesWritten += len;
						_board.WriteOrder.Add(cmd);
						if (addr == 0) _board.Transfers.Add(cmd);
						Reply(cmd, addr, ReadOnlySpan<byte>.Empty);
						break;
					case SmartInfiniteProtocol.CmdReadAdvancedKeyArea:
						_board.AreaReadPackets++;
						Reply(cmd, addr, _board.Area.AsSpan(addr, len));
						break;
					case SmartInfiniteProtocol.CmdWriteAdvancedKeyArea:
						if (addr == 0) _board.AreaTransfers++;
						bool dropped = addr == 0 && _board.AreaTransfers == _board.DropFirstAreaChunkOfTransfer;
						if (!_board.DiscardWrites && !dropped)
							request.AsSpan(8, len).CopyTo(_board.Area.AsSpan(addr));
						_board.AreaWritePackets++;
						_board.WriteOrder.Add(cmd);
						if (addr == 0) _board.Transfers.Add(cmd);
						Reply(cmd, addr, ReadOnlySpan<byte>.Empty);
						break;
				}
			}

			public int Read(byte[] buffer)
			{
				if (_replies.Count == 0)
					throw new TimeoutException("no reply pending");

				var reply = _replies.Dequeue();
				reply.AsSpan(0, Math.Min(reply.Length, buffer.Length)).CopyTo(buffer);
				return reply.Length;
			}

			public void Dispose() { }

			private void Reply(byte cmd, int addr, ReadOnlySpan<byte> payload)
			{
				var reply = new byte[SmartInfiniteProtocol.ReportLength];
				reply[1] = cmd;
				reply[2] = (byte)(addr & 0xFF);
				reply[3] = (byte)(addr >> 8);
				reply[SmartInfiniteProtocol.ResponseStatusOffset] = SmartInfiniteProtocol.ResponseSuccess;
				payload.CopyTo(reply.AsSpan(SmartInfiniteProtocol.ResponsePayloadOffset));
				_replies.Enqueue(reply);
			}

			// Cyrex-shaped Device Info: 75 matrix slots, 68 keys, byte 8 = advanced-key area in 64 B units.
			private byte[] DeviceInfoBlock()
			{
				var block = new byte[64];
				block[0] = 0xAA;
				block[1] = 0xBB;
				block[4] = MatrixSlots;
				block[6] = 68;
				block[8] = _board.AreaUnits;
				return block;
			}
		}
	}

	private static SmartInfiniteExtendedKeysService NewService(FakeBoard board)
	{
		var transport = new SmartInfiniteTransport(
			board.Open, $"fake-{Guid.NewGuid():N}", "EXT TEST", logger: null);
		return new SmartInfiniteExtendedKeysService(transport, "TEST");
	}

	private static byte[] ExpectedRs(int slot1, int slot2) =>
		[0x84, 0x06, (byte)slot1, 1, (byte)(slot1 + 4), (byte)slot2, 1, (byte)(slot2 + 4)];

	private static byte[] ExpectedSocd(int slot1, int slot2, int priority) =>
		[0x83, (byte)((priority << 4) | 0x06), (byte)slot1, 1, (byte)(slot1 + 4), (byte)slot2, 1, (byte)(slot2 + 4)];

	// ── Read ─────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Read_NoAdvancedKeyArea_IsUnsupportedAndNeverTouchesTheArea()
	{
		var board = new FakeBoard { AreaUnits = 0 };
		var service = NewService(board);

		// A verdict about the board, not a failure to reach it — the two are distinct results.
		Assert.Equal(ExtendedKeysReadStatus.Unsupported, (await service.ReadAsync()).Status);
		Assert.Equal(0, board.AreaReadPackets);
	}

	[Fact]
	public async Task Read_EmptyArea_ReportsNoEntriesAndTheWholeAreaFree()
	{
		var board = new FakeBoard();
		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		Assert.Empty(state!.Entries);
		Assert.Empty(state.BlockedSlots);
		Assert.Equal(256, state.AreaBytes);
		Assert.Equal(256, state.FreeBytes);
		Assert.Equal(32, state.MaxRecords);
	}

	[Fact]
	public async Task Read_DecodesAnRsRecord_AndBlocksTheSlotsOfAnUnmanagedOne()
	{
		var board = new FakeBoard();
		var dks = new byte[] { 0x80, 0x0A, 0x01, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44 };
		board.PlaceRecord(0, ExpectedRs(SlotA, SlotD), SlotA, SlotD);
		board.PlaceRecord(8, dks, 16);

		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		var entry = Assert.Single(state!.Entries);
		Assert.Equal(ExtendedKeyEntry.RapidSnap(SlotA, SlotD), entry);
		Assert.Equal([16], state.BlockedSlots);
		Assert.Equal(256 - 8 - 12, state.FreeBytes);
	}

	[Fact]
	public async Task Read_DecodesASocdRecordWithItsPriority()
	{
		var board = new FakeBoard();
		board.PlaceRecord(0, ExpectedSocd(18, 19, 2), 18, 19);

		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		Assert.Equal(ExtendedKeyEntry.Socd(18, 19, 2), Assert.Single(state!.Entries));
		Assert.Empty(state.BlockedSlots);
	}

	[Fact]
	public async Task Read_SocdWithAForeignPriorityNibble_IsPreservedAsUnmanaged()
	{
		var board = new FakeBoard();
		board.PlaceRecord(0, [0x83, 0x96, 18, 1, 0x16, 19, 1, 0x17], 18, 19);

		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		Assert.Empty(state!.Entries);
		Assert.Equal([18, 19], state.BlockedSlots.OrderBy(s => s));
	}

	[Fact]
	public async Task Write_AroundASocdWithAForeignPriorityNibble_CarriesItVerbatim_AndRefusesItsSlots()
	{
		var board = new FakeBoard();
		var foreign = new byte[] { 0x83, 0x96, 18, 1, 0x16, 19, 1, 0x17 };
		board.PlaceRecord(0, foreign, 18, 19);

		// Blocked on read means blocked on write: the slots cannot be claimed...
		var refused = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(18, SlotD)]);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, refused);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);

		// ...and a rewrite around the record keeps its bytes and pointers (SOCD group, so offset 0).
		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);
		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(foreign, board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(8, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x83, 0, 0), board.Slot(18));
		Assert.Equal(new KeyMatrixRecord(0x83, 0, 0), board.Slot(19));
	}

	// ── Write: the golden path ────────────────────────────────────────────────────

	[Fact]
	public async Task Write_Socd_WritesThePriorityNibbleAndBothPointers()
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.Socd(18, 19, 2)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(ExpectedSocd(18, 19, 2), board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(0x26, board.Area[1]);
		Assert.Equal(new KeyMatrixRecord(0x83, 0, 0), board.Slot(18));
		Assert.Equal(new KeyMatrixRecord(0x83, 0, 0), board.Slot(19));
	}

	[Fact]
	public async Task Write_SocdAndRapidSnapCoexist_InTheVendorGroupOrder()
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync(
			[ExtendedKeyEntry.RapidSnap(SlotA, SlotD), ExtendedKeyEntry.Socd(18, 19, 1)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		// SOCD sorts before RS, so it takes offset 0 and the RS pair follows at 8.
		Assert.Equal(ExpectedSocd(18, 19, 1), board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(8, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x83, 0, 0), board.Slot(18));
		Assert.Equal(new KeyMatrixRecord(0x84, 8, 0), board.Slot(SlotA));

		var state = (await NewService(board).ReadAsync()).State;
		Assert.Equal(2, state!.Entries.Count);
		Assert.Contains(ExtendedKeyEntry.Socd(18, 19, 1), state.Entries);
		Assert.Contains(ExtendedKeyEntry.RapidSnap(SlotA, SlotD), state.Entries);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(4)]
	public async Task Write_SocdPriorityOutOfRange_IsRejectedWithoutWriting(int priority)
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.Socd(18, 19, priority)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_Pair_WritesRecordAndPointers_LeavesEveryOtherSlotAlone_AndVerifies()
	{
		var board = new FakeBoard();
		var before = (byte[])board.Current.Clone();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x84, 0, 0), board.Slot(SlotA));
		Assert.Equal(new KeyMatrixRecord(0x84, 0, 0), board.Slot(SlotD));
		for (int slot = 0; slot < MatrixSlots; slot++)
			if (slot is not (SlotA or SlotD))
				Assert.Equal(SmartInfiniteProtocol.ReadSlot(before, slot), board.Slot(slot));
	}

	[Fact]
	public async Task Write_SendsTheWholeAreaThenTheFull384ByteMatrix()
	{
		var board = new FakeBoard();

		await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(SmartInfiniteProtocol.EnumerateChunks(256).Count, board.AreaWritePackets);
		Assert.Equal(SmartInfiniteProtocol.KeyMatrixTableSize, board.MatrixBytesWritten);
		Assert.Equal(SmartInfiniteProtocol.EnumerateChunks(SmartInfiniteProtocol.KeyMatrixTableSize).Count, board.MatrixWritePackets);
		// Every area packet before the first matrix packet.
		int firstMatrix = board.WriteOrder.IndexOf(SmartInfiniteProtocol.CmdSetKeyInfo);
		Assert.All(board.WriteOrder.Take(firstMatrix), cmd => Assert.Equal(SmartInfiniteProtocol.CmdWriteAdvancedKeyArea, cmd));
		Assert.Equal(1, board.SessionsOpened);
	}

	[Fact]
	public async Task Write_EmptySet_RestoresTheFactoryTripletsAndZeroesTheArea()
	{
		var board = new FakeBoard();
		board.PlaceRecord(0, ExpectedRs(SlotA, SlotD), SlotA, SlotD);

		var result = await NewService(board).WriteAsync([]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(SmartInfiniteProtocol.ReadSlot(board.Factory, SlotA), board.Slot(SlotA));
		Assert.Equal(SmartInfiniteProtocol.ReadSlot(board.Factory, SlotD), board.Slot(SlotD));
		Assert.All(board.Area, b => Assert.Equal(0, b));
	}

	[Fact]
	public async Task Write_UnmanagedRecordSurvivesByteForByte_AndItsSlotIsRePointed()
	{
		var board = new FakeBoard();
		var dks = new byte[] { 0x80, 0x0A, 0x01, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44 };
		// The vendor left DKS first; the rebuild puts RS ahead of it, so DKS moves.
		board.PlaceRecord(0, dks, 16);

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(dks, board.Area.AsSpan(8, dks.Length).ToArray());
		Assert.Equal(SmartInfiniteAdvancedKeyArea.PointerRecord(0x80, 8), board.Slot(16));
	}

	// ── Mod-Tap (docs/plans/add-extended-key-mod-tap) ────────────────────────────

	private const int SlotCaps = 30;
	private static readonly ExtendedKeyCode LeftCtrl = new(0x10, 0x01, 0x00);
	private static readonly ExtendedKeyCode Esc = new(0x10, 0x00, 0x29);
	private static readonly byte[] CapsModTapBytes = [0x81, 0x06, 0x96, 0x00, 0x02, 0x01, 0x01, 0x29];

	[Fact]
	public async Task Write_ModTap_PointsOnlyThePhysicalSlot_AndEncodesTheOutputsFromTheirOwnIdentity()
	{
		var board = new FakeBoard();
		var before = (byte[])board.Current.Clone();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, Esc)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		// The acceptance criterion's bytes: neither output is a factory slot of this board's table
		// (the fake's slot 30 is usage 0x22), the record carries the picked keys' own identity.
		Assert.Equal(CapsModTapBytes, board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x81, 0, 0), board.Slot(SlotCaps));
		for (int slot = 0; slot < MatrixSlots; slot++)
			if (slot != SlotCaps)
				Assert.Equal(SmartInfiniteProtocol.ReadSlot(before, slot), board.Slot(slot));

		var state = (await NewService(board).ReadAsync()).State;
		Assert.Equal(ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, Esc), Assert.Single(state!.Entries));
		Assert.Empty(state.BlockedSlots);
	}

	[Fact]
	public async Task Write_ModTapSocdAndRapidSnapCoexist_InTheVendorGroupOrder()
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync(
			[ExtendedKeyEntry.RapidSnap(SlotA, SlotD), ExtendedKeyEntry.Socd(18, 19, 1), ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, Esc)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		// MT, then SOCD, then RS — the vendor's group order; the pointers follow.
		Assert.Equal(CapsModTapBytes, board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(ExpectedSocd(18, 19, 1), board.Area.AsSpan(8, 8).ToArray());
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(16, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x81, 0, 0), board.Slot(SlotCaps));
		Assert.Equal(new KeyMatrixRecord(0x83, 8, 0), board.Slot(18));
		Assert.Equal(new KeyMatrixRecord(0x84, 16, 0), board.Slot(SlotA));

		var state = (await NewService(board).ReadAsync()).State;
		Assert.Equal(3, state!.Entries.Count);
		Assert.Contains(ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, Esc), state.Entries);
		Assert.Equal(256 - 24, state.FreeBytes);
	}

	[Fact]
	public async Task Write_ModTapWithoutATapOutput_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, ExtendedKeyCode.None)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(1001)]
	public async Task Write_ModTapTimeOutsideTheSlider_IsRejectedWithoutWriting(int timeMs)
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, timeMs, LeftCtrl, Esc)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_ModTapWithAConsumerCommandOutput_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard();
		// A SYSTEM-tab command record ({0x20, lo, hi}) has no firmware code type in an MT record.
		var volumeUp = new ExtendedKeyCode(0x20, 0xE9, 0x00);

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, 150, volumeUp, Esc)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Read_ModTapWithAMouseButtonOutput_IsPreservedAsUnmanaged()
	{
		var board = new FakeBoard();
		// codeType 3 (mouse button) is representable on the wire but not as a keyboard record.
		board.PlaceRecord(0, [0x81, 0x06, 0x64, 0x00, 0x03, 0x01, 0x01, 0x29], SlotCaps);

		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		Assert.Empty(state!.Entries);
		Assert.Equal([SlotCaps], state.BlockedSlots);

		// And a rewrite around it carries the bytes verbatim — still in the MT group (the group order
		// goes by TYPE, managed or not), so it keeps offset 0 and the RS pair lands after it.
		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);
		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(new byte[] { 0x81, 0x06, 0x64, 0x00, 0x03, 0x01, 0x01, 0x29 }, board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(8, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x81, 0, 0), board.Slot(SlotCaps));
	}

	// ── Toggle (docs/plans/add-extended-key-toggle) ──────────────────────────────

	private const int SlotT = 20;
	private const int SlotW = 17;
	private static readonly ExtendedKeyCode W = new(0x10, 0x00, 0x1A);
	private static readonly byte[] TToggleWBytes = [0x82, 0x04, 0x64, 0x00, 0x01, 0x1A];

	[Fact]
	public async Task Write_Toggle_PointsOnlyThePhysicalSlot_AndLeavesTheOutputKeysSlotAlone()
	{
		var board = new FakeBoard();
		var before = (byte[])board.Current.Clone();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(SlotT, 100, W)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		// The acceptance criterion's bytes; W's own slot is not part of the record and stays as it was.
		Assert.Equal(TToggleWBytes, board.Area.AsSpan(0, 6).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x82, 0, 0), board.Slot(SlotT));
		Assert.Equal(SmartInfiniteProtocol.ReadSlot(before, SlotW), board.Slot(SlotW));
		for (int slot = 0; slot < MatrixSlots; slot++)
			if (slot != SlotT)
				Assert.Equal(SmartInfiniteProtocol.ReadSlot(before, slot), board.Slot(slot));

		var state = (await NewService(board).ReadAsync()).State;
		Assert.Equal(ExtendedKeyEntry.Toggle(SlotT, 100, W), Assert.Single(state!.Entries));
		Assert.Empty(state.BlockedSlots);
		Assert.Equal(256 - 6, state.FreeBytes);
	}

	[Fact]
	public async Task Write_ToggleWithAModifierOutput_EncodesCodeType2_AndRoundTrips()
	{
		var board = new FakeBoard();
		var leftShift = new ExtendedKeyCode(0x10, 0x02, 0x00);

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(SlotT, 100, leftShift)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(new byte[] { 0x82, 0x04, 0x64, 0x00, 0x02, 0x02 }, board.Area.AsSpan(0, 6).ToArray());
		var state = (await NewService(board).ReadAsync()).State;
		Assert.Equal(ExtendedKeyEntry.Toggle(SlotT, 100, leftShift), Assert.Single(state!.Entries));
	}

	[Fact]
	public async Task Write_AllFourModesCoexist_InTheVendorGroupOrder()
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync(
		[
			ExtendedKeyEntry.RapidSnap(SlotA, SlotD),
			ExtendedKeyEntry.Socd(18, 19, 1),
			ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, Esc),
			ExtendedKeyEntry.Toggle(SlotT, 100, W),
		]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		// TGL, MT, SOCD, RS — and the 6-byte Toggle record shifts everything after it by 6, not 8.
		Assert.Equal(TToggleWBytes, board.Area.AsSpan(0, 6).ToArray());
		Assert.Equal(CapsModTapBytes, board.Area.AsSpan(6, 8).ToArray());
		Assert.Equal(ExpectedSocd(18, 19, 1), board.Area.AsSpan(14, 8).ToArray());
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(22, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x82, 0, 0), board.Slot(SlotT));
		Assert.Equal(new KeyMatrixRecord(0x81, 6, 0), board.Slot(SlotCaps));
		Assert.Equal(new KeyMatrixRecord(0x83, 14, 0), board.Slot(18));
		Assert.Equal(new KeyMatrixRecord(0x84, 22, 0), board.Slot(SlotA));

		var state = (await NewService(board).ReadAsync()).State;
		Assert.Equal(4, state!.Entries.Count);
		Assert.Contains(ExtendedKeyEntry.Toggle(SlotT, 100, W), state.Entries);
		Assert.Equal(256 - 30, state.FreeBytes);
	}

	[Fact]
	public async Task Write_ToggleWithoutAnOutput_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(SlotT, 100, ExtendedKeyCode.None)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(1001)]
	public async Task Write_ToggleTimeOutsideTheSlider_IsRejectedWithoutWriting(int timeMs)
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(SlotT, timeMs, W)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_ToggleWithATapOutputOrASecondSlot_IsRejectedWithoutWriting()
	{
		// The record has one output and one slot; a tap_key or slot2 on a Toggle entry is a client bug,
		// refused rather than silently dropped.
		var board = new FakeBoard();

		var withTap = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(SlotT, 100, W) with { TapKey = Esc }]);
		var withSlot2 = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(SlotT, 100, W) with { Slot2 = SlotW }]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, withTap);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, withSlot2);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Read_ToggleWithAMouseButtonOutput_IsPreservedAsUnmanaged()
	{
		var board = new FakeBoard();
		board.PlaceRecord(0, [0x82, 0x04, 0x64, 0x00, 0x03, 0x01], SlotT);

		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		Assert.Empty(state!.Entries);
		Assert.Equal([SlotT], state.BlockedSlots);

		// A rewrite around it carries the 6 bytes verbatim in the TGL group (offset 0); the RS pair follows.
		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);
		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(new byte[] { 0x82, 0x04, 0x64, 0x00, 0x03, 0x01 }, board.Area.AsSpan(0, 6).ToArray());
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(6, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x82, 0, 0), board.Slot(SlotT));
		Assert.Equal(new KeyMatrixRecord(0x84, 6, 0), board.Slot(SlotA));
	}

	// ── Write: refusals, none of which may send a packet ────────────────────────

	[Fact]
	public async Task Write_SlotOwnedByAnUnmanagedRecord_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard();
		board.PlaceRecord(0, [0x80, 0x0A, 0x01, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44], SlotA);

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_Overflow_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard();
		// 33 pairs = 264 bytes into a 256-byte area, over 66 distinct real keys.
		var keys = Enumerable.Range(0, MatrixSlots).Where(s => s != FnSlot && !Placeholders.Contains(s)).Take(66).ToArray();
		var desired = new List<ExtendedKeyEntry>();
		for (int i = 0; i < 33; i++) desired.Add(ExtendedKeyEntry.RapidSnap(keys[2 * i], keys[2 * i + 1]));

		var result = await NewService(board).WriteAsync(desired);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Theory]
	[InlineData(SlotA, SlotA)]     // same key twice
	[InlineData(SlotA, FnSlot)]    // FN
	[InlineData(SlotA, 42)]        // an unused placeholder slot
	[InlineData(SlotA, MatrixSlots)] // out of range
	public async Task Write_InvalidSlot_IsRejectedWithoutWriting(int slot1, int slot2)
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(slot1, slot2)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_SlotNamedInTwoEntries_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard();

		var result = await NewService(board).WriteAsync(
			[ExtendedKeyEntry.RapidSnap(SlotA, SlotD), ExtendedKeyEntry.RapidSnap(SlotD, 35)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_ModeNotManagedByThisBuild_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard();
		// All four enum members are managed now; DKS (0x80) is the TYPE this build only preserves.
		var entry = new ExtendedKeyEntry((ExtendedKeyMode)0x80, SlotA, ExtendedKeyEntry.NoSlot,
			0, 100, new ExtendedKeyCode(0x10, 0x00, 0x29), ExtendedKeyCode.None);

		var result = await NewService(board).WriteAsync([entry]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_FirmwareDiscardsTheWrite_IsRejectedAfterExactlyTwoAttemptsInOneSession()
	{
		var board = new FakeBoard { DiscardWrites = true };

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		// The contract this pins CHANGED with m3's retry. A read-back mismatch used to be final on the
		// first try; it no longer is, because it says the board is torn — one region landed and the
		// other did not — rather than that the request was wrong, and a board left torn is wiped by the
		// repair on the App's very next read. So the set goes down a second time against a snapshot
		// read after the failure. What still holds, and is what this test now guards, is that there is
		// no retry storm: the second attempt is the last, and both share one session, so a firmware
		// that simply disagrees costs two pairs of transfers and not WithRetry's three sessions.
		Assert.Equal(1, board.SessionsOpened);
		Assert.Equal(2 * AreaChunks, board.AreaWritePackets);
		Assert.Equal(2 * MatrixChunks, board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_OneDroppedAreaChunk_IsHealedByTheSecondAttempt_AndVerifies()
	{
		// The case the retry exists for, and the other half of the test above: one packet is lost on the
		// cable, not every packet. The area's first chunk — the one carrying the record — is acked and
		// never lands, so the matrix goes out with pointers aimed at bytes that were never written and
		// the verify fails. Answering Rejected there is what m3 is about: the board is left torn, the
		// App's very next read repairs it, and with persist:true that empty set lands in the user's
		// rows. Instead the set goes down once more in the same call, and this time the board keeps it.
		var board = new FakeBoard { DropFirstAreaChunkOfTransfer = 1 };

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x84, 0, 0), board.Slot(SlotA));
		Assert.Equal(new KeyMatrixRecord(0x84, 0, 0), board.Slot(SlotD));

		// The whole heal, in order: the torn write (area, matrix), the repair the second attempt's
		// snapshot read runs because the board no longer parses (matrix, area — the mirror order), then
		// the write again on a coherent board (area, matrix). One session throughout, so the retry is
		// the loop inside WriteOnce and not WithRetry re-opening the handle.
		Assert.Equal(
		[
			SmartInfiniteProtocol.CmdWriteAdvancedKeyArea, SmartInfiniteProtocol.CmdSetKeyInfo,
			SmartInfiniteProtocol.CmdSetKeyInfo, SmartInfiniteProtocol.CmdWriteAdvancedKeyArea,
			SmartInfiniteProtocol.CmdWriteAdvancedKeyArea, SmartInfiniteProtocol.CmdSetKeyInfo,
		], board.Transfers);
		Assert.Equal(1, board.SessionsOpened);

		// And the board really holds it: a fresh read gets the pair back, with nothing blocked and
		// nothing to repair.
		var state = (await NewService(board).ReadAsync()).State;
		Assert.Equal(ExtendedKeyEntry.RapidSnap(SlotA, SlotD), Assert.Single(state!.Entries));
		Assert.Empty(state.BlockedSlots);
		Assert.False(state.Repaired);
	}

	// ── S1: a torn two-region write is repaired on read, and only that ──────────
	//
	// The torn state a dropped 0x18 packet leaves: the area holds the records the write put there,
	// the matrix still holds the pointers of the layout before it. Nothing can be rebuilt on top of
	// it, and reporting "unsupported" would hide the section and with it the one control that could
	// rewrite the board — so the read resets the board instead (review S1).

	// The area the write landed, with the pointers the matrix transfer never replaced: slots 18/19
	// still claim a SOCD record at offset 0, where a Rapid Snap record now sits.
	private static FakeBoard TornBoard(bool discardWrites = false)
	{
		var board = new FakeBoard { DiscardWrites = discardWrites };
		ExpectedRs(SlotA, SlotD).CopyTo(board.Area, 0);
		board.PointSlotAt(18, SmartInfiniteProtocol.TypeExtendedSocd, 0);
		board.PointSlotAt(19, SmartInfiniteProtocol.TypeExtendedSocd, 0);
		return board;
	}

	private static int AreaChunks => SmartInfiniteProtocol.EnumerateChunks(256).Count;
	private static int MatrixChunks => SmartInfiniteProtocol.EnumerateChunks(SmartInfiniteProtocol.KeyMatrixTableSize).Count;

	[Fact]
	public async Task Read_AreaThatDoesNotParseAgainstThePointers_IsRepairedAndReadsBackEmpty()
	{
		var board = TornBoard();

		var result = await NewService(board).ReadAsync();

		// Not "unsupported": the board is made coherent and then answers normally, with no records.
		Assert.Equal(ExtendedKeysReadStatus.Read, result.Status);
		Assert.Empty(result.State!.Entries);
		Assert.Empty(result.State.BlockedSlots);
		Assert.Equal(256, result.State.FreeBytes);

		// The factory triplets are back on the slots that pointed at a record, and the area is zeroed.
		Assert.Equal(SmartInfiniteProtocol.ReadSlot(board.Factory, 18), board.Slot(18));
		Assert.Equal(SmartInfiniteProtocol.ReadSlot(board.Factory, 19), board.Slot(19));
		Assert.All(board.Area, b => Assert.Equal(0, b));
	}

	[Fact]
	public async Task Read_RepairingATornBoard_WritesTheMatrixFirst_AndExactlyOnce()
	{
		var board = TornBoard();

		Assert.Equal(ExtendedKeysReadStatus.Read, (await NewService(board).ReadAsync()).Status);

		// Two whole-table transfers and no more: matrix, then area. Matrix first is the mirror of the
		// write path's order and safe for the mirror reason — dropping a pointer before the record it
		// aims at never leaves a pointer aimed at a hole.
		Assert.Equal([SmartInfiniteProtocol.CmdSetKeyInfo, SmartInfiniteProtocol.CmdWriteAdvancedKeyArea], board.Transfers);
		Assert.Equal(MatrixChunks, board.MatrixWritePackets);
		Assert.Equal(AreaChunks, board.AreaWritePackets);
		Assert.Equal(1, board.SessionsOpened);
	}

	[Fact]
	public async Task Read_BoardThatStillDoesNotParseAfterTheRepair_IsUnsupportedAndIsNotRepairedAgain()
	{
		// The firmware takes both transfers and keeps its old bytes, so the reset changes nothing.
		var board = TornBoard(discardWrites: true);

		var result = await NewService(board).ReadAsync();

		Assert.Equal(ExtendedKeysReadStatus.Unsupported, result.Status);
		Assert.Null(result.State);
		// One attempt, never a loop: a board this build cannot make sense of is left alone rather
		// than having its flash rewritten on every probe. Unsupported is an answer, so no retry either.
		Assert.Equal([SmartInfiniteProtocol.CmdSetKeyInfo, SmartInfiniteProtocol.CmdWriteAdvancedKeyArea], board.Transfers);
		Assert.Equal(1, board.SessionsOpened);
	}

	[Fact]
	public async Task Read_BoardCarryingOnlyRecordsThisBuildDoesNotManage_IsNotRepaired()
	{
		// The safety argument for the repair in one case: it must fire on data that is incoherent in
		// itself, never on data this build merely does not understand. A DKS record, an END record and
		// a SOCD with a priority nibble past 3 all parse — they are a user's configuration, and
		// "repairing" them would silently delete it.
		var board = new FakeBoard();
		var dks = new byte[] { 0x80, 0x0A, 0x01, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44 };
		var end = new byte[] { SmartInfiniteProtocol.TypeExtendedEnd, 0x00 };
		var foreignSocd = new byte[] { 0x83, 0x96, 18, 1, 0x16, 19, 1, 0x17 };
		board.PlaceRecord(0, dks, 16);
		board.PlaceRecord(12, end, 17);
		board.PlaceRecord(14, foreignSocd, 18, 19);
		var areaBefore = (byte[])board.Area.Clone();
		var matrixBefore = (byte[])board.Current.Clone();

		var result = await NewService(board).ReadAsync();

		Assert.Equal(ExtendedKeysReadStatus.Read, result.Status);
		Assert.Empty(result.State!.Entries);
		Assert.Equal([16, 17, 18, 19], result.State.BlockedSlots.OrderBy(s => s));
		Assert.Equal(256 - 12 - 2 - 8, result.State.FreeBytes);
		// Not one packet of flash was rewritten, and both regions are byte-for-byte what they were.
		Assert.Empty(board.Transfers);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
		Assert.Equal(areaBefore, board.Area);
		Assert.Equal(matrixBefore, board.Current);
	}

	// ── m3(b): a read that had to reset the board says so ───────────────────────

	[Fact]
	public async Task Read_ThatHadToResetTheBoard_ReportsRepaired_AndAnOrdinaryReadDoesNot()
	{
		// Both of the reads above come back as Read with a state, so without this flag the App cannot
		// tell a board whose records it is looking at from one whose records the repair just deleted —
		// and it must show the second but never persist it as the owner's rows (review m3(b)).
		var torn = TornBoard();

		var repaired = await NewService(torn).ReadAsync();

		Assert.Equal(ExtendedKeysReadStatus.Read, repaired.Status);
		Assert.True(repaired.State!.Repaired);
		Assert.Empty(repaired.State.Entries);

		// An ordinary read of a board carrying a real configuration says false.
		var board = new FakeBoard();
		board.PlaceRecord(0, ExpectedRs(SlotA, SlotD), SlotA, SlotD);
		var ordinary = await NewService(board).ReadAsync();

		Assert.Equal(ExtendedKeysReadStatus.Read, ordinary.Status);
		Assert.False(ordinary.State!.Repaired);

		// And so does the next read of the board that was just repaired: the flag describes THIS read,
		// not a board that was ever torn, so the App goes back to persisting once it is coherent again.
		Assert.False((await NewService(torn).ReadAsync()).State!.Repaired);
	}

	// ── m6: the two halves of a pair record must name the same two keys ─────────
	//
	// MT / TGL take their slot from the pointer side, RS / SOCD take theirs from bytes 2 and 5 inside
	// the record. A torn write can make the two disagree: delete A-D, add B-C, lose the 0x18, and the
	// old pointers on A and D find a record of the right TYPE at the old offset. The area still parses
	// — every check ParseRecords makes is about the pointers, not about what the record says — so the
	// board decoded as the pair B-C while the pointers sat on A and D, and a re-read after a failed
	// write saved that phantom pair into the user's rows (review m6).

	// Pointers on A and D, a record at offset 0 whose own slot bytes name 18 and 19.
	private static FakeBoard PairRecordNamingOtherSlots(byte[] record)
	{
		var board = new FakeBoard();
		record.CopyTo(board.Area, 0);
		board.PointSlotAt(SlotA, record[0], 0);
		board.PointSlotAt(SlotD, record[0], 0);
		return board;
	}

	[Fact]
	public async Task Read_RapidSnapRecordNamingOtherSlotsThanItsPointers_IsNotManaged()
	{
		var board = PairRecordNamingOtherSlots(ExpectedRs(18, 19));

		var state = (await NewService(board).ReadAsync()).State;

		// Not reported as a configured pair — neither the pair the record names nor the one the
		// pointers name. The slots that do point at it are blocked, which is how every record this
		// build cannot own is treated, and the area was left alone: it parses, so nothing is repaired.
		Assert.NotNull(state);
		Assert.Empty(state!.Entries);
		Assert.Equal([SlotA, SlotD], state.BlockedSlots.OrderBy(s => s));
		Assert.False(state.Repaired);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);

		// The control, without which this test would only prove that something was refused: the same
		// eight bytes with the pointers on the slots they name are a pair this build owns.
		var coherent = new FakeBoard();
		coherent.PlaceRecord(0, ExpectedRs(18, 19), 18, 19);
		var control = (await NewService(coherent).ReadAsync()).State;

		Assert.Equal(ExtendedKeyEntry.RapidSnap(18, 19), Assert.Single(control!.Entries));
		Assert.Empty(control.BlockedSlots);
	}

	[Fact]
	public async Task Read_SocdRecordNamingOtherSlotsThanItsPointers_IsNotManaged()
	{
		var board = PairRecordNamingOtherSlots(ExpectedSocd(18, 19, 2));

		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		Assert.Empty(state!.Entries);
		Assert.Equal([SlotA, SlotD], state.BlockedSlots.OrderBy(s => s));

		// The control: the same record, priority nibble and all, with coherent pointers.
		var coherent = new FakeBoard();
		coherent.PlaceRecord(0, ExpectedSocd(18, 19, 2), 18, 19);
		var control = (await NewService(coherent).ReadAsync()).State;

		Assert.Equal(ExtendedKeyEntry.Socd(18, 19, 2), Assert.Single(control!.Entries));
		Assert.Empty(control.BlockedSlots);
	}

	[Fact]
	public async Task Read_PairRecordWhosePointersOnlyHalfAgreeWithIt_IsNotManaged()
	{
		// One slot in common is not agreement: the pair the record describes is still not the pair the
		// board would play. A check that only asked whether the pointers are among the record's slots
		// would let this through.
		var board = new FakeBoard();
		ExpectedRs(18, 19).CopyTo(board.Area, 0);
		board.PointSlotAt(18, SmartInfiniteProtocol.TypeExtendedRapidSnap, 0);
		board.PointSlotAt(SlotD, SmartInfiniteProtocol.TypeExtendedRapidSnap, 0);

		var state = (await NewService(board).ReadAsync()).State;

		Assert.NotNull(state);
		Assert.Empty(state!.Entries);
		Assert.Equal([18, SlotD], state.BlockedSlots.OrderBy(s => s));
	}

	[Fact]
	public async Task Write_AroundAPairRecordNamingOtherSlotsThanItsPointers_CarriesItVerbatim_AndRefusesItsSlots()
	{
		// Blocked, not repaired — the decision recorded with the m6 fix. The area parses, so every
		// other record on the board (a user's DKS or END, which this build cannot recreate) is still
		// reachable, and a whole-board reset to get rid of one incoherent record would destroy them.
		// So it travels through the rebuild like any other unmanaged record: bytes verbatim, pointers
		// re-aimed, slots refused to anyone who asks for them.
		var torn = ExpectedRs(18, 19);
		var board = PairRecordNamingOtherSlots(torn);

		var refused = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, 35)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, refused);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);

		// A rewrite around it: the new pair takes offset 0 (both are 0x84, and a desired record sorts
		// ahead of a carried one), the incoherent record moves to 8 with its bytes intact.
		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(20, 21)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		Assert.Equal(ExpectedRs(20, 21), board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(torn, board.Area.AsSpan(8, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x84, 0, 0), board.Slot(20));
		Assert.Equal(new KeyMatrixRecord(0x84, 8, 0), board.Slot(SlotA));
		Assert.Equal(new KeyMatrixRecord(0x84, 8, 0), board.Slot(SlotD));
		// Two transfers, the write's own: the board was never reset on the way.
		Assert.Equal([SmartInfiniteProtocol.CmdWriteAdvancedKeyArea, SmartInfiniteProtocol.CmdSetKeyInfo], board.Transfers);
	}

	// ── S2: "could not talk to the board" is not a verdict about the board ──────
	//
	// Both used to be one null. Unsupported hides the section for the rest of the session, so a dead
	// cable must never produce it. (The Unsupported half is Read_NoAdvancedKeyArea_… above.)

	[Fact]
	public async Task Read_TransportThatWillNotOpen_IsFailedNotUnsupported()
	{
		var board = new FakeBoard { RefuseOpen = true };

		var result = await NewService(board).ReadAsync();

		Assert.Equal(ExtendedKeysReadStatus.Failed, result.Status);
		Assert.Null(result.State);
		// And it was retried rather than answered — a verdict stops the retries, a failure does not.
		Assert.Equal(0, board.SessionsOpened);
		Assert.True(board.OpenAttempts > 1, $"expected the open to be retried, got {board.OpenAttempts} attempt(s)");
	}

	[Fact]
	public async Task Read_DeviceInfoThatNeverAnswers_IsFailedNotUnsupported()
	{
		var board = new FakeBoard { SilentDeviceInfo = true };

		var result = await NewService(board).ReadAsync();

		Assert.Equal(ExtendedKeysReadStatus.Failed, result.Status);
		Assert.Null(result.State);
		Assert.True(board.SessionsOpened > 1, $"expected the read to be retried, got {board.SessionsOpened} session(s)");
		Assert.Equal(0, board.AreaReadPackets);
	}

	[Fact]
	public async Task Read_CurrentTableThatIsNotAKeyMatrix_IsUnsupportedNotFailed()
	{
		// The board answered, and what it said is not a matrix this build will build a write on. That
		// is a verdict, so it is returned once rather than retried three more times.
		var board = new FakeBoard { ScrambleCurrentTable = true };

		var result = await NewService(board).ReadAsync();

		Assert.Equal(ExtendedKeysReadStatus.Unsupported, result.Status);
		Assert.Equal(1, board.SessionsOpened);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	// ── S3: a pair record has no room for a time, an output or (for RS) a priority ──

	[Fact]
	public async Task Write_RapidSnapCarryingASingleKeyField_IsRejectedWithoutWriting()
	{
		// Without this the board is written with a plain pair and Verified is returned for a set the
		// board does not hold — so the App's "already matches" skip can never fire and every preset
		// apply rewrites the flash (review S3).
		var board = new FakeBoard();
		var pair = ExtendedKeyEntry.RapidSnap(SlotA, SlotD);

		var withTime = await NewService(board).WriteAsync([pair with { TimeMs = 100 }]);
		var withHold = await NewService(board).WriteAsync([pair with { HoldKey = LeftCtrl }]);
		var withTap = await NewService(board).WriteAsync([pair with { TapKey = Esc }]);
		var withPriority = await NewService(board).WriteAsync([pair with { Priority = 2 }]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, withTime);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, withHold);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, withTap);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, withPriority);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
	}

	[Fact]
	public async Task Write_SocdCarryingASingleKeyField_IsRejectedWithoutWriting()
	{
		// SOCD's LEN nibble does carry a priority, so that one field stays legal; everything else is
		// refused for the same reason as Rapid Snap's.
		var board = new FakeBoard();
		var pair = ExtendedKeyEntry.Socd(18, 19, 2);

		var withTime = await NewService(board).WriteAsync([pair with { TimeMs = 100 }]);
		var withHold = await NewService(board).WriteAsync([pair with { HoldKey = LeftCtrl }]);
		var withTap = await NewService(board).WriteAsync([pair with { TapKey = Esc }]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, withTime);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, withHold);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, withTap);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);

		// The priority itself is still the legal field it always was.
		Assert.Equal(ExtendedKeysWriteResult.Verified, await NewService(board).WriteAsync([pair]));
	}

	// ── S4: a single-key mode needs a real key under its slot ───────────────────

	[Fact]
	public async Task Write_SingleKeyModeOnASlotThatIsNotAKey_IsRejectedWithoutWriting()
	{
		// Slot 42 is a matrix placeholder: its factory triplet names no key. The pair modes refused it
		// already (PairKeyCodes reads that triplet); MT and TGL never did, so a record was written that
		// ate area bytes, blocked the slot in Key Assignments and Macros, and could never fire (S4).
		var board = new FakeBoard();
		const int Placeholder = 42;

		var modTap = await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(Placeholder, 150, LeftCtrl, Esc)]);
		var toggle = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(Placeholder, 100, W)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, modTap);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, toggle);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
		// The same entries on a real key are the ones that pass, so the refusal is about the slot.
		Assert.Equal(ExtendedKeysWriteResult.Verified,
			await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, Esc)]));
	}

	// ── m1: an output names one key, and a factory key's identity is read the vendor's way ──

	[Fact]
	public async Task Write_SingleKeyOutputCarryingBothAModifierMaskAndAUsage_IsRejectedWithoutWriting()
	{
		// {0x10, mask, usage} with both non-zero names two keys and the record has room for one.
		// KeyCodeFromRecord answers with the usage, so before this guard the board was written with a
		// key nobody asked for and verified: the caller's stored row could then never match the
		// read-back, and the App's self-heal rewrote the flash on every page open without ever
		// converging (review m1). Refused whole-entry with a reason instead, the way S3 refuses a
		// foreign field on a pair.
		var board = new FakeBoard();
		var ctrlAndC = new ExtendedKeyCode(0x10, 0x01, 0x06);

		var hold = await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, 150, ctrlAndC, Esc)]);
		var tap = await NewService(board).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, ctrlAndC)]);
		var toggled = await NewService(board).WriteAsync([ExtendedKeyEntry.Toggle(SlotT, 100, ctrlAndC)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, hold);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, tap);
		Assert.Equal(ExtendedKeysWriteResult.Rejected, toggled);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);

		// Either half on its own is still a legal output — the refusal is about naming two keys at
		// once, not about masks or usages.
		Assert.Equal(ExtendedKeysWriteResult.Verified,
			await NewService(new FakeBoard()).WriteAsync([ExtendedKeyEntry.ModTap(SlotCaps, 150, LeftCtrl, Esc)]));
	}

	[Fact]
	public async Task Write_PairOnFactoryKeysCarryingAMask_StillEncodesTheVendorsWay()
	{
		// The deliberate non-change, and the half of m1 that matters most: the refusal above lives in
		// the OUTPUT path only. KeyCodeFromRecord is the reader of a board key's FACTORY identity —
		// the vendor's own getKeyCodeAndType, "a usage wins, else the mask"
		// (docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md) — and the pair
		// modes and the plain-key gate read the factory table through it. Moving the mask+usage
		// refusal in there to tidy up the divergence would make the host reject Rapid Snap and SOCD
		// pairs on real keys the vendor accepts, and it would buy nothing: a pair entry's stored row
		// carries only slots, so the never-converging self-heal m1 describes cannot arise on it.
		var board = new FakeBoard();
		const int LeftShift = 45;      // the fake's factory record is {0x10, 0x02, 0x00} — a mask, no usage
		const int MaskAndUsage = 44;   // a factory key whose record carries both, which is why the rule exists
		board.PatchFactoryKey(MaskAndUsage, new KeyMatrixRecord(0x10, 0x01, 0x1D));

		var rapidSnap = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(LeftShift, MaskAndUsage)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, rapidSnap);
		// Left Shift carries codeType 2 and its bitmask; the mask+usage key is truncated to codeType 1
		// and its usage — the vendor's rule, not a refusal and not the mask.
		Assert.Equal(
			new byte[] { 0x84, 0x06, LeftShift, 2, 0x02, MaskAndUsage, 1, 0x1D },
			board.Area.AsSpan(0, 8).ToArray());

		// The SOCD twin, which reads the factory table through exactly the same helper.
		var socdBoard = new FakeBoard();
		socdBoard.PatchFactoryKey(MaskAndUsage, new KeyMatrixRecord(0x10, 0x01, 0x1D));

		var socd = await NewService(socdBoard).WriteAsync([ExtendedKeyEntry.Socd(LeftShift, MaskAndUsage, 2)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, socd);
		Assert.Equal(
			new byte[] { 0x83, 0x26, LeftShift, 2, 0x02, MaskAndUsage, 1, 0x1D },
			socdBoard.Area.AsSpan(0, 8).ToArray());
	}

	// ── The two write-side gates (Round 2 → Tests) ──────────────────────────────

	[Fact]
	public async Task Write_NoAdvancedKeyArea_IsRejectedWithoutWriting()
	{
		var board = new FakeBoard { AreaUnits = 0 };

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal(0, board.AreaWritePackets + board.MatrixWritePackets);
		Assert.Equal(0, board.AreaReadPackets);
		// A board without an area will not grow one on a retry: answered once, not four times.
		Assert.Equal(1, board.SessionsOpened);
	}

	[Fact]
	public async Task Write_AreaThatDoesNotParse_RepairsTheBoardFirst_ThenWritesTheDesiredSet()
	{
		// The contract this pins CHANGED with S1's repair. Round 2 asked for "zero packets when the
		// area does not parse", which was right while a torn board was simply unsupported. It is no
		// longer what the code does or should do: WriteOnce goes through the same ReadSnapshot, so the
		// board is repaired and the write then proceeds on a coherent board. The gate that still holds
		// is the narrower one asserted below — nothing is written on top of an area that was not
		// understood; the repair zeroes it first.
		var board = TornBoard();

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Verified, result);
		// Four transfers: the repair's matrix-then-area, then the write's area-then-matrix.
		Assert.Equal(
		[
			SmartInfiniteProtocol.CmdSetKeyInfo, SmartInfiniteProtocol.CmdWriteAdvancedKeyArea,
			SmartInfiniteProtocol.CmdWriteAdvancedKeyArea, SmartInfiniteProtocol.CmdSetKeyInfo,
		], board.Transfers);
		// The stale pointers are gone, and only the requested record is on the board.
		Assert.Equal(SmartInfiniteProtocol.ReadSlot(board.Factory, 18), board.Slot(18));
		Assert.Equal(SmartInfiniteProtocol.ReadSlot(board.Factory, 19), board.Slot(19));
		Assert.Equal(ExpectedRs(SlotA, SlotD), board.Area.AsSpan(0, 8).ToArray());
		Assert.Equal(new KeyMatrixRecord(0x84, 0, 0), board.Slot(SlotA));
		Assert.Equal(ExtendedKeysReadStatus.Read, (await NewService(board).ReadAsync()).Status);
	}

	[Fact]
	public async Task Write_AreaThatStillDoesNotParseAfterTheRepair_IsRejectedWithoutWritingTheSet()
	{
		// The surviving half of Round 2's gate: when the repair cannot make the board coherent, the
		// desired set is refused and not one packet of it is sent — only the single repair attempt was.
		var board = TornBoard(discardWrites: true);

		var result = await NewService(board).WriteAsync([ExtendedKeyEntry.RapidSnap(SlotA, SlotD)]);

		Assert.Equal(ExtendedKeysWriteResult.Rejected, result);
		Assert.Equal([SmartInfiniteProtocol.CmdSetKeyInfo, SmartInfiniteProtocol.CmdWriteAdvancedKeyArea], board.Transfers);
		Assert.Equal(MatrixChunks, board.MatrixWritePackets);
		Assert.Equal(AreaChunks, board.AreaWritePackets);
		Assert.Equal(1, board.SessionsOpened);
	}

	// S5 (the proto key-code range checks) is deliberately absent: the rule lives in
	// DeviceService.TryFromProto, a private static of the gRPC service, and there is no RPC harness in
	// tests/. It has no Infrastructure-level twin to pin — ExtendedKeyCode is three bytes by
	// construction, so by the time a value reaches this service the truncation has already happened.
}
