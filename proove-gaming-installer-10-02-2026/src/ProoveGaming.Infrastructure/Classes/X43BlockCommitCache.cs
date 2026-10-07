namespace ProoveGaming.Infrastructure.Classes;

// Remembers which param_1 block images this device has already been told to commit, so an apply can
// skip re-committing one whose content has not changed.
//
// Every block in a param_1 write is a FLASH commit, and the App sends the whole configuration on any
// edit — change an RGB effect and the key matrix and DPI table ride along unchanged. A user clicking
// "Random lighting" nine times in half a minute therefore re-committed the button table to flash nine
// times, and the mouse came back with left and right click dead (2026-08-17 host log; see
// docs/mouse/blaze/key-matrix.md). The caller asking for a block is not evidence that the block
// differs, and only a difference is worth a flash write.
//
// Deliberately conservative: a block with no record yet is always kept, so the first apply of a
// session commits whatever the caller asked for, exactly as before. One instance per
// EasenMouseService, i.e. per device — the service cache keeps those alive for the host's lifetime.
internal sealed class X43BlockCommitCache
{
	public const byte Block0 = 0x01;   // DPI table + debounce / lift-off
	public const byte Block1 = 0x02;   // LED effect colours
	public const byte Block2 = 0x04;   // key matrix

	private byte[]? _block0;
	private byte[]? _block1;
	private byte[]? _block2;

	// Clears the bit for any block whose image is byte-identical to the one last committed.
	public byte Filter(byte blocks, byte[] block0, byte[] block1, byte[] block2)
	{
		if ((blocks & Block0) != 0 && Same(_block0, block0)) blocks &= unchecked((byte)~Block0);
		if ((blocks & Block1) != 0 && Same(_block1, block1)) blocks &= unchecked((byte)~Block1);
		if ((blocks & Block2) != 0 && Same(_block2, block2)) blocks &= unchecked((byte)~Block2);
		return blocks;
	}

	// Records the images that were just written. Only the blocks actually committed are recorded —
	// a block that was dropped keeps the record it already matched, and a block the caller never
	// supplied must not be recorded at all, or the next real change to it would be dropped.
	public void Remember(byte blocks, byte[] block0, byte[] block1, byte[] block2)
	{
		if ((blocks & Block0) != 0) _block0 = (byte[])block0.Clone();
		if ((blocks & Block1) != 0) _block1 = (byte[])block1.Clone();
		if ((blocks & Block2) != 0) _block2 = (byte[])block2.Clone();
	}

	// Copies another cache's records into this one. Used when a service instance is recreated for
	// the same physical unit (factory cache eviction on a HID list change): the device's flash did
	// not change just because the USB list did, so the predecessor's record of what is committed
	// stays valid. Sharing the arrays is safe — Remember clones them in and nothing mutates them.
	public void CopyFrom(X43BlockCommitCache other)
	{
		_block0 = other._block0;
		_block1 = other._block1;
		_block2 = other._block2;
	}

	// Forgets everything, so the next apply commits whatever it is given.
	//
	// The record is only meaningful while this process is the last thing that wrote the device. Once
	// the handle is lost the mouse may come back re-flashed — the OEM tool and the vendor's reset
	// both rewrite the key matrix — and a stale record would drop the very write meant to restore
	// the user's remaps, leaving them unrecoverable without picking a different binding first.
	//
	// Losing the handle is the only signal available: a rewrite performed while the device stays
	// open cannot be detected from here at all.
	public void Reset()
	{
		_block0 = null;
		_block1 = null;
		_block2 = null;
	}

	private static bool Same(byte[]? committed, byte[] candidate) =>
		committed is not null && candidate.AsSpan().SequenceEqual(committed);
}
