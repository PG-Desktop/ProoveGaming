using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Every param_1 block write is a FLASH commit on this firmware. The App sends the whole configuration
// on any edit, so an RGB change used to drag the key matrix and the DPI table through a flash commit
// with it — nine "Random lighting" clicks in half a minute and the mouse came back with left and
// right click dead (2026-08-17 host log; docs/mouse/blaze/key-matrix.md).
//
// This cache is what makes "the caller supplied this block" and "this block actually differs" two
// different questions.
public class X43BlockCommitCacheTests
{
	private const byte B0 = X43BlockCommitCache.Block0;
	private const byte B1 = X43BlockCommitCache.Block1;
	private const byte B2 = X43BlockCommitCache.Block2;
	private const byte All = B0 | B1 | B2;

	private static byte[] Block(byte fill) => Enumerable.Repeat(fill, 64).ToArray();

	// Nothing is known about the device yet, so nothing may be assumed away.
	[Fact]
	public void Filter_BeforeAnythingIsCommitted_KeepsEveryBlock()
	{
		var cache = new X43BlockCommitCache();

		Assert.Equal(All, cache.Filter(All, Block(1), Block(2), Block(3)));
	}

	// THE case this exists for: the LED changed, the key matrix and DPI table did not.
	[Fact]
	public void Filter_AnRgbOnlyChange_DropsTheKeyMatrixAndDpiTable()
	{
		var cache = new X43BlockCommitCache();
		var dpi = Block(1);
		var matrix = Block(3);

		cache.Remember(All, dpi, Block(2), matrix);

		// Same DPI table, same key matrix, a new colour table.
		var blocks = cache.Filter(All, dpi, Block(0x99), matrix);

		Assert.Equal(B1, blocks);
		Assert.Equal(0, blocks & B2);   // the button table is not re-committed
		Assert.Equal(0, blocks & B0);
	}

	// The opposite direction has to keep working — a real button remap must still reach flash.
	[Fact]
	public void Filter_WhenTheKeyMatrixActuallyChanges_KeepsIt()
	{
		var cache = new X43BlockCommitCache();
		cache.Remember(All, Block(1), Block(2), Block(3));

		var blocks = cache.Filter(All, Block(1), Block(2), Block(0x77));

		Assert.Equal(B2, blocks);
	}

	[Fact]
	public void Filter_WhenNothingChanged_DropsEverything()
	{
		var cache = new X43BlockCommitCache();
		cache.Remember(All, Block(1), Block(2), Block(3));

		Assert.Equal(0, cache.Filter(All, Block(1), Block(2), Block(3)));
	}

	// Re-applying the same colour twice must not start committing again the third time.
	[Fact]
	public void Filter_IsStableAcrossRepeatedIdenticalApplies()
	{
		var cache = new X43BlockCommitCache();
		var (a, b, c) = (Block(1), Block(2), Block(3));
		cache.Remember(All, a, b, c);

		for (var i = 0; i < 5; i++)
		{
			var blocks = cache.Filter(All, a, b, c);
			cache.Remember(blocks, a, b, c);
			Assert.Equal(0, blocks);
		}
	}

	// Remember records only what was COMMITTED. A block the caller never supplied must leave no
	// record, or the next genuine change to it would be silently dropped — the failure mode this
	// whole cache would otherwise introduce: a button remap that never reaches the mouse.
	[Fact]
	public void Remember_DoesNotRecordABlockThatWasNotCommitted()
	{
		var cache = new X43BlockCommitCache();

		// An LED-only apply: block 1 committed, the matrix merely present in the payload.
		var matrix = Block(3);
		cache.Remember(B1, Block(1), Block(2), matrix);

		// That same matrix has never been committed, so it must still be sent.
		Assert.Equal(B2, cache.Filter(B2, Block(1), Block(2), matrix));
	}

	// A dropped block keeps the record it matched, so it is not resurrected by a later partial write.
	[Fact]
	public void Filter_ThenRemember_KeepsTheRecordOfADroppedBlock()
	{
		var cache = new X43BlockCommitCache();
		var matrix = Block(3);
		cache.Remember(All, Block(1), Block(2), matrix);

		var blocks = cache.Filter(All, Block(1), Block(0x99), matrix);
		cache.Remember(blocks, Block(1), Block(0x99), matrix);

		Assert.Equal(0, cache.Filter(B2, Block(1), Block(0x99), matrix));
	}

	// The record is only meaningful while this process is the last thing that wrote the device. Once
	// the handle is lost the mouse can come back re-flashed — the OEM tool and the vendor's reset
	// both rewrite the key matrix — and a stale record would drop the very write meant to restore
	// the user's remaps, leaving them unrecoverable without picking a different binding first.
	[Fact]
	public void Reset_MakesTheNextApplyCommitEverythingAgain()
	{
		var cache = new X43BlockCommitCache();
		var (a, b, c) = (Block(1), Block(2), Block(3));
		cache.Remember(All, a, b, c);
		Assert.Equal(0, cache.Filter(All, a, b, c));   // nothing to do while the record holds

		cache.Reset();

		Assert.Equal(All, cache.Filter(All, a, b, c));
	}

	// A recreated service adopts its predecessor's record (factory eviction on a HID list change,
	// PR #119 review M1): the device's flash did not change because the USB list did, so the first
	// apply after the rebind must still drop unchanged blocks instead of re-committing them all.
	[Fact]
	public void CopyFrom_CarriesTheRecordToAFreshCache()
	{
		var predecessor = new X43BlockCommitCache();
		var (a, b, c) = (Block(1), Block(2), Block(3));
		predecessor.Remember(All, a, b, c);

		var fresh = new X43BlockCommitCache();
		fresh.CopyFrom(predecessor);

		Assert.Equal(0, fresh.Filter(All, a, b, c));
		// A genuine change still reaches flash after the copy.
		Assert.Equal(B2, fresh.Filter(All, a, b, Block(0x77)) & B2);
	}

	// The images are copied, not aliased: the composer reuses buffers, and a cache holding a live
	// reference would compare a block against itself and drop every real change.
	[Fact]
	public void Remember_CopiesTheImage_SoALaterMutationStillCountsAsAChange()
	{
		var cache = new X43BlockCommitCache();
		var matrix = Block(3);
		cache.Remember(All, Block(1), Block(2), matrix);

		matrix[10] = 0xEE;

		Assert.Equal(B2, cache.Filter(B2, Block(1), Block(2), matrix));
	}
}
