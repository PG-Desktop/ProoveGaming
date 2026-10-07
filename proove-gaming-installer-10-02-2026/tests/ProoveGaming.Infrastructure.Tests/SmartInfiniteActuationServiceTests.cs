using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// SmartInfiniteActuationService against a scripted fake device, through the ISmartInfiniteIo
// seam — the per-key write path's safety rules pinned without hardware: only the requested slots
// change, a firmware that answers with different bytes is final (no re-write storm), and the
// per-key read exposes each slot's values in centi-mm.
public class SmartInfiniteActuationServiceTests
{
	private const int TravelSlots = 75;
	private const int RecordBytes = 10;
	private const int AreaBytes = TravelSlots * RecordBytes;

	// ── Fake firmware ────────────────────────────────────────────────────────────
	// Same wire shape as the key-mapping fake: 64-byte requests, cmd/addr echo replies with 0x55
	// at byte 7. Serves Device Info (0x12) and the travel area (0x38 read / 0x39 write).
	private sealed class FakeMagneticDevice
	{
		public int SessionsOpened { get; private set; }
		public int AreaWritePackets { get; private set; }
		public byte[] TravelArea { get; } = new byte[AreaBytes];

		// A firmware that acks the 0x39 transfer but silently discards it — the "answered and
		// disagreed" case the verify read-back must catch as final.
		public bool DiscardWrites { get; init; }

		public FakeMagneticDevice()
		{
			// Factory-uniform table: press/release 200 steps, RT off (quick 10), axis nibble 0x3
			// with a 0x014 bottom dead zone the service must never touch.
			for (int slot = 0; slot < TravelSlots; slot++)
			{
				int o = slot * RecordBytes;
				TravelArea[o] = 200;
				TravelArea[o + 2] = 200;
				TravelArea[o + 4] = 10;
				TravelArea[o + 6] = 10;
				TravelArea[o + 8] = 0x14; TravelArea[o + 9] = 0x30;
			}
		}

		public ISmartInfiniteIo? Open()
		{
			SessionsOpened++;
			return new Session(this);
		}

		private sealed class Session : ISmartInfiniteIo
		{
			private readonly FakeMagneticDevice _device;
			private readonly Queue<byte[]> _replies = new();

			public Session(FakeMagneticDevice device) => _device = device;

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

					case SmartInfiniteProtocol.CmdReadTravelArea:
						Reply(cmd, addr, _device.TravelArea.AsSpan(addr, len));
						break;

					case SmartInfiniteProtocol.CmdWriteTravelArea:
						if (!_device.DiscardWrites)
							request.AsSpan(8, len).CopyTo(_device.TravelArea.AsSpan(addr));
						_device.AreaWritePackets++;
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

			// The Cyrex-shaped magnetic Device Info the geometry gate validates against: 75 travel
			// slots, 0.01 mm step, 10-byte records, press 20..350 steps, RT 1..250.
			private static byte[] DeviceInfoBlock()
			{
				var block = new byte[64];
				block[0] = 0xAA;
				block[1] = 0xBB;
				block[4] = TravelSlots;             // matrix slots
				block[6] = 68;                      // key count
				block[7] = TravelSlots;             // travel slots
				block[16] = 0x01;                   // step: 1 x 0.01 mm
				block[19] = 0x5E; block[20] = 0x01; // press max 350
				block[21] = 20;                     // press min
				block[23] = 0xFA;                   // rt max 250
				block[25] = 1;                      // rt min
				block[29] = 0xC8;                   // default press 200
				block[31] = 0xC8;                   // default release 200
				block[37] = 10;                     // default quick press
				block[39] = 10;                     // default quick release
				return block;
			}
		}
	}

	private static SmartInfiniteActuationService NewService(FakeMagneticDevice device)
	{
		var transport = new SmartInfiniteTransport(
			device.Open, $"fake-{Guid.NewGuid():N}", "ACT TEST", logger: null);
		return new SmartInfiniteActuationService(transport, "TEST");
	}

	// ── Per-key read ─────────────────────────────────────────────────────────────

	[Fact]
	public async Task Read_BuildsThePerKeyMapInCentiMmKeyedBySlot()
	{
		var device = new FakeMagneticDevice();
		// Slot 3 vendor-configured: press 50 steps, RT on with quick press 3.
		device.TravelArea[3 * RecordBytes] = 50;
		device.TravelArea[3 * RecordBytes + 4] = 0x03;
		device.TravelArea[3 * RecordBytes + 5] = 0x40;
		var service = NewService(device);

		var state = await service.ReadAsync();

		Assert.NotNull(state);
		Assert.Equal(TravelSlots, state!.PerKeyValues.Count);
		// 0.01 mm step: steps ARE centi-mm.
		Assert.Equal(200, state.PerKeyValues[0].PressCentiMm);
		Assert.False(state.PerKeyValues[0].RapidTrigger);
		Assert.Equal(50, state.PerKeyValues[3].PressCentiMm);
		Assert.True(state.PerKeyValues[3].RapidTrigger);
		Assert.Equal(3, state.PerKeyValues[3].QuickPressCentiMm);
		// The global reduction still reflects the majority, not the outlier.
		Assert.Equal(200, state.Values.PressCentiMm);
	}

	// ── Per-key write ────────────────────────────────────────────────────────────

	[Fact]
	public async Task WriteKeys_PatchesExactlyTheRequestedSlotsAndVerifies()
	{
		var device = new FakeMagneticDevice();
		var before = (byte[])device.TravelArea.Clone();
		var service = NewService(device);

		var ok = await service.WriteKeysAsync([2, 5],
			new KeyboardActuationValues(100, 90, RapidTrigger: true, 5, 6));

		Assert.True(ok);
		foreach (int slot in new[] { 2, 5 })
		{
			var (press, release, rt, quickPress, quickRelease) =
				SmartInfiniteTravelArea.ReadSlot(device.TravelArea, RecordBytes, slot);
			Assert.Equal(100, press);
			Assert.Equal(90, release);
			Assert.True(rt);
			Assert.Equal(5, quickPress);
			Assert.Equal(6, quickRelease);
			// The dead-zone field of a patched slot survives byte-for-byte.
			Assert.Equal(before[slot * RecordBytes + 8], device.TravelArea[slot * RecordBytes + 8]);
			Assert.Equal(before[slot * RecordBytes + 9], device.TravelArea[slot * RecordBytes + 9]);
		}

		// Every other slot is byte-identical across the write.
		for (int slot = 0; slot < TravelSlots; slot++)
			if (slot is not (2 or 5))
				Assert.True(before.AsSpan(slot * RecordBytes, RecordBytes)
					.SequenceEqual(device.TravelArea.AsSpan(slot * RecordBytes, RecordBytes)));
	}

	[Fact]
	public async Task WriteKeys_FirmwareDiscardsTheWrite_IsRejectedWithoutARetryStorm()
	{
		var device = new FakeMagneticDevice { DiscardWrites = true };
		var service = NewService(device);

		var ok = await service.WriteKeysAsync([2],
			new KeyboardActuationValues(100, 90, RapidTrigger: false, 5, 6));

		Assert.False(ok);
		// "Answered and disagreed" is final: one session, one area transfer — not MaxAttempts
		// re-writes of the same refused area.
		Assert.Equal(1, device.SessionsOpened);
		Assert.Equal(SmartInfiniteProtocol.EnumerateChunks(AreaBytes).Count, device.AreaWritePackets);
	}

	[Fact]
	public async Task WriteKeys_NoSlots_RefusesWithoutTouchingTheDevice()
	{
		var device = new FakeMagneticDevice();
		var service = NewService(device);

		Assert.False(await service.WriteKeysAsync([], new KeyboardActuationValues(100, 90, false, 5, 6)));
		Assert.Equal(0, device.SessionsOpened);
	}

	[Fact]
	public async Task WriteKeys_OnlyOutOfRangeSlots_RefusesWithoutWriting()
	{
		var device = new FakeMagneticDevice();
		var service = NewService(device);

		Assert.False(await service.WriteKeysAsync([TravelSlots, 500],
			new KeyboardActuationValues(100, 90, false, 5, 6)));
		Assert.Equal(0, device.AreaWritePackets);
	}
}
