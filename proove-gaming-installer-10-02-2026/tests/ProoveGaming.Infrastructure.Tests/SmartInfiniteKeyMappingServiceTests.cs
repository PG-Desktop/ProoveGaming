using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// SmartInfiniteKeyMappingService against a scripted fake device, through the ISmartInfiniteIo
// seam — the coverage the transport extraction previously lacked (PR #84 review, m5): the
// hardware-measured behaviours (key-count gate, whole-384-byte write, single-slot patch that
// touches nothing else, misaligned-read refusal) were only exercised on real boards before.
public class SmartInfiniteKeyMappingServiceTests
{
	private const int CyrexSlots = 75;

	// ── Fake firmware ────────────────────────────────────────────────────────────
	// Same wire shape as the macro-service fake: 64-byte requests, cmd/addr echo replies with
	// 0x55 at byte 7. Serves Device Info (0x12) and the key matrix (0x17 read / 0x18 write).
	private sealed class FakeSmartInfiniteDevice
	{
		public byte KeyCount { get; init; } = 68;
		public byte MatrixSlots { get; init; } = CyrexSlots;

		public int SessionsOpened { get; private set; }
		public int KeyWritePackets { get; private set; }
		public byte[] KeyMatrix { get; } = new byte[SmartInfiniteProtocol.KeyMatrixTableSize];

		public FakeSmartInfiniteDevice()
		{
			// A plausible factory table: every populated slot is a plain key {0x10, 0, usage}.
			for (int slot = 0; slot < CyrexSlots; slot++)
			{
				KeyMatrix[slot * 3] = 0x10;
				KeyMatrix[slot * 3 + 2] = (byte)(slot + 4);
			}
		}

		public ISmartInfiniteIo? Open()
		{
			SessionsOpened++;
			return new Session(this);
		}

		private sealed class Session : ISmartInfiniteIo
		{
			private readonly FakeSmartInfiniteDevice _device;
			private readonly Queue<byte[]> _replies = new();

			public Session(FakeSmartInfiniteDevice device) => _device = device;

			public void Write(byte[] request)
			{
				var cmd = request[1];
				int addr = request[2] | (request[3] << 8);
				int len = request[4];

				switch (cmd)
				{
					case SmartInfiniteProtocol.CmdReadDeviceInfo:
						Reply(cmd, addr, DeviceInfoBlock().AsSpan(addr, len));
						break;

					case SmartInfiniteProtocol.CmdReadKeyInfo:
						Reply(cmd, addr, _device.KeyMatrix.AsSpan(addr, len));
						break;

					case SmartInfiniteProtocol.CmdSetKeyInfo:
						request.AsSpan(8, len).CopyTo(_device.KeyMatrix.AsSpan(addr));
						_device.KeyWritePackets++;
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

			private byte[] DeviceInfoBlock()
			{
				var block = new byte[64];
				block[0] = 0xAA;
				block[1] = 0xBB;
				block[4] = _device.MatrixSlots;
				block[5] = 4;                      // macro space, irrelevant here
				block[6] = _device.KeyCount;
				return block;
			}
		}
	}

	private static (SmartInfiniteKeyMappingService Service, FakeSmartInfiniteDevice Device) NewService(
		FakeSmartInfiniteDevice device)
	{
		var transport = new SmartInfiniteTransport(
			device.Open, $"fake-{Guid.NewGuid():N}", "KEY TEST", logger: null);
		return (new SmartInfiniteKeyMappingService(transport, "TEST"), device);
	}

	[Fact]
	public async Task ReadKeyMatrix_ReturnsTheDeviceTable()
	{
		var (service, _) = NewService(new FakeSmartInfiniteDevice());

		var map = await service.ReadKeyMatrixAsync();

		Assert.NotNull(map);
		Assert.Equal(CyrexSlots, map!.Count);
		Assert.Equal(new KeyMatrixRecord(0x10, 0x00, 0x04), map[0]);
	}

	[Fact]
	public async Task SetKeyMapping_PatchesOneSlotAndWritesTheFullReserve()
	{
		var (service, device) = NewService(new FakeSmartInfiniteDevice());
		var before = (byte[])device.KeyMatrix.Clone();

		var ok = await service.SetKeyMappingAsync(18, 0x10, 0x00, 0x14); // slot 18 -> Q

		Assert.True(ok);
		// The write is the full 384-byte reserve — the C1/C3 shape that makes writes persist —
		// which is 7 packets (6 x 56 + 48).
		Assert.Equal(7, device.KeyWritePackets);
		Assert.Equal(0x14, device.KeyMatrix[18 * 3 + 2]);

		// The read-modify-write touched exactly the named slot and nothing else.
		for (int i = 0; i < device.KeyMatrix.Length; i++)
			if (i is not (18 * 3 + 2))
				Assert.Equal(before[i], device.KeyMatrix[i]);
	}

	[Fact]
	public async Task KeyCountZero_RefusesTheWriteBeforeTouchingTheTable()
	{
		// The Rampage-signal gate: a firmware reporting zero remappable keys acks writes and
		// discards them, so the service must refuse rather than report success.
		var (service, device) = NewService(new FakeSmartInfiniteDevice { KeyCount = 0 });

		var ok = await service.SetKeyMappingAsync(18, 0x10, 0x00, 0x14);

		Assert.False(ok);
		Assert.Equal(0, device.KeyWritePackets);
	}

	[Fact]
	public async Task MisalignedTable_IsDiscardedNotReturned()
	{
		// A table whose TYPE column is garbage (the 56 = 2 mod 3 shift failure) must never escape
		// the read — and, on the write path, must never be written back.
		var device = new FakeSmartInfiniteDevice();
		Array.Clear(device.KeyMatrix);   // all-zero: no slot carries a known TYPE
		var (service, _) = NewService(device);

		var map = await service.ReadKeyMatrixAsync();

		Assert.Null(map);
	}
}
