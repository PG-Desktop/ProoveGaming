using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// SmartInfiniteMacroService against a scripted fake device, through the ISmartInfiniteIo seam.
// The behaviours pinned here are the ones only hardware could previously catch:
//   - the verify runs on a FRESH session, because this family stages writes in volatile RAM that
//     reads back correctly within the writing session without being committed (the measured
//     staging-buffer behaviour) — a same-session verify passes on a board that stored nothing;
//   - a deterministic refusal (no macro space, or a read-back mismatch) is final, not retried;
//   - a transient open failure IS retried.
public class SmartInfiniteMacroServiceTests
{
	private static readonly IReadOnlyList<MacroLibraryEntry> OneMacro =
	[
		new MacroLibraryEntry(0, MacroRepeatMode.PlayCount,
			[new MacroEvent(0x04, true, 20), new MacroEvent(0x04, false, 20)]),
	];

	// ── Fake firmware ────────────────────────────────────────────────────────────
	// Speaks the wire protocol the transport expects: 64-byte requests
	// {0x06, cmd, addr_lo, addr_hi, len, sum, sum, 0, payload…}, replies with the cmd/addr echo
	// and 0x55 at byte 7. Supports Device Info (0x12) and the macro region (0x1B read / 0x1C write).
	private sealed class FakeSmartInfiniteDevice
	{
		public int MacroSpaceBytes { get; init; } = 1024;

		// True = the firmware behaves like the measured 0x18 staging buffer: a write is visible to
		// reads in the SAME session but never committed, so a fresh session sees the old flash.
		public bool StagingOnly { get; init; }

		public int SessionsOpened { get; private set; }
		public int MacroWritePackets { get; private set; }
		public byte[] Flash { get; } = new byte[4096];

		// Open failures to serve before sessions start succeeding (transient-retry scenario).
		public int FailNextOpens { get; set; }

		public ISmartInfiniteIo? Open()
		{
			if (FailNextOpens > 0)
			{
				FailNextOpens--;
				return null;
			}

			SessionsOpened++;
			return new Session(this);
		}

		private sealed class Session : ISmartInfiniteIo
		{
			private readonly FakeSmartInfiniteDevice _device;
			private readonly byte[] _visibleRegion;
			private readonly Queue<byte[]> _replies = new();

			public Session(FakeSmartInfiniteDevice device)
			{
				_device = device;
				// Staging firmware: this session's reads see this session's writes, but the copy
				// dies with the handle. Normal firmware: reads and writes go straight to flash.
				_visibleRegion = device.StagingOnly ? (byte[])device.Flash.Clone() : device.Flash;
			}

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

					case SmartInfiniteProtocol.CmdWriteMacroInfo:
						request.AsSpan(8, len).CopyTo(_visibleRegion.AsSpan(addr));
						_device.MacroWritePackets++;
						Reply(cmd, addr, ReadOnlySpan<byte>.Empty);
						break;

					case SmartInfiniteProtocol.CmdReadMacroInfo:
						Reply(cmd, addr, _visibleRegion.AsSpan(addr, len));
						break;

					default:
						// Unknown command: no reply queued; the transport's read times out.
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
				block[4] = 75;                                    // matrix slots
				block[5] = (byte)(_device.MacroSpaceBytes / 256); // macro space
				block[6] = 68;                                    // key count
				return block;
			}
		}
	}

	private static (SmartInfiniteMacroService Service, FakeSmartInfiniteDevice Device) NewService(
		FakeSmartInfiniteDevice device)
	{
		// Unique path per test: the family gate is keyed by device path and static process-wide.
		var transport = new SmartInfiniteTransport(
			device.Open, $"fake-{Guid.NewGuid():N}", "TEST Cyrex", logger: null);
		return (new SmartInfiniteMacroService(transport, "TEST Cyrex"), device);
	}

	[Fact]
	public async Task Upload_WritesTheRegionAndVerifiesOnAFreshSession()
	{
		var (service, device) = NewService(new FakeSmartInfiniteDevice());

		var ok = await service.UploadMacrosAsync(OneMacro);

		Assert.True(ok);
		// The write and the verify are two separate sessions — the property that makes the verify
		// mean "stored", not "echoed from the staging buffer". Collapsing them back into one is
		// exactly the regression the staging test below turns into a failure.
		Assert.Equal(2, device.SessionsOpened);

		// The flash holds precisely what the encoder produced for this board's 1024-byte region.
		var expected = SmartInfiniteMacroEncoder.EncodePacked(OneMacro, device.MacroSpaceBytes);
		Assert.Equal(
			expected.Buffer.AsSpan(0, expected.DataLen).ToArray(),
			device.Flash.AsSpan(0, expected.DataLen).ToArray());
	}

	[Fact]
	public async Task StagingOnlyFirmware_FailsTheUploadInsteadOfVerifyingItsOwnEcho()
	{
		// The M1 scenario: the firmware acks 0x1C into session-local RAM and answers 0x1B from it,
		// but commits nothing. A same-session verify would read its own bytes back and report
		// success; the fresh-session verify sees the untouched flash and must fail the upload.
		var (service, device) = NewService(new FakeSmartInfiniteDevice { StagingOnly = true });

		var ok = await service.UploadMacrosAsync(OneMacro);

		Assert.False(ok);
		// "Answered and disagreed" is final: one write session + one verify session, no retry
		// loop re-running the 20-packet write against a deterministic refusal.
		Assert.Equal(2, device.SessionsOpened);
	}

	[Fact]
	public async Task MacroSpaceZero_RefusesBeforeAnyWriteAndDoesNotRetry()
	{
		var (service, device) = NewService(new FakeSmartInfiniteDevice { MacroSpaceBytes = 0 });

		var ok = await service.UploadMacrosAsync(OneMacro);

		Assert.False(ok);
		Assert.Equal(0, device.MacroWritePackets);
		Assert.Equal(1, device.SessionsOpened);   // identity read only — refusal is final
	}

	[Fact]
	public async Task TransientOpenFailure_IsRetriedAndSucceeds()
	{
		// The failure WithRetry exists for: Windows still releasing the previous handle. The first
		// open fails, the retry goes through and the upload completes normally.
		var (service, device) = NewService(new FakeSmartInfiniteDevice { FailNextOpens = 1 });

		var ok = await service.UploadMacrosAsync(OneMacro);

		Assert.True(ok);
		Assert.Equal(2, device.SessionsOpened);
	}
}
