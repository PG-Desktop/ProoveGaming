using System.Linq;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the TAIPAN DPI stage table (command 0x02). The register is CPI/50 — the app's stage
// set encodes to clean values (800→0x10 … 12000→0xF0) and every value in the vendor's worked
// example is an exact ×50 multiple. Baseline: docs/mouse/taipan/protocol.md §DPI.
public class TaipanDpiTableTests
{
	private static DpiStageParams[] Stages(params int[] cpi) =>
		cpi.Select(v => new DpiStageParams((ushort)v, 0, 0, 0)).ToArray();

	[Theory]
	[InlineData(800, 0x0010)]
	[InlineData(1200, 0x0018)]
	[InlineData(2400, 0x0030)]
	[InlineData(3600, 0x0048)]
	[InlineData(4800, 0x0060)]
	[InlineData(6000, 0x0078)]
	[InlineData(12000, 0x00F0)]
	public void DpiRegister_EncodesAppStageSet(int cpi, int expected)
	{
		Assert.Equal(expected, TaipanDpiTable.DpiRegister(cpi));
	}

	// The vendor example's raw values decode to real, ×50-aligned DPIs.
	[Theory]
	[InlineData(0x0009, 450)]
	[InlineData(0x0012, 900)]
	[InlineData(0x002A, 2100)]
	[InlineData(0x018F, 19950)]
	public void CpiFromRegister_DecodesVendorExampleValues(int register, int expectedCpi)
	{
		Assert.Equal(expectedCpi, TaipanDpiTable.CpiFromRegister((ushort)register));
	}

	[Fact]
	public void DpiRegister_RoundTripsThroughCpi()
	{
		foreach (var cpi in new[] { 800, 1200, 2400, 12000 })
			Assert.Equal(cpi, TaipanDpiTable.CpiFromRegister(TaipanDpiTable.DpiRegister(cpi)));
	}

	[Fact]
	public void BuildTable_ReturnsFixedLength()
	{
		Assert.Equal(TaipanDpiTable.Length, TaipanDpiTable.BuildTable(Stages(800)).Length);
	}

	[Fact]
	public void BuildTable_MatchesGoldenBytesForAppStageSet()
	{
		var table = TaipanDpiTable.BuildTable(Stages(800, 1200, 2400, 3600, 4800, 6000, 12000));

		byte[] expected =
		[
			0x07,                          // stage count
			0x01, 0x10, 0x00, 0x10, 0x00,  // 800   → 0x0010, X and Y
			0x01, 0x18, 0x00, 0x18, 0x00,  // 1200  → 0x0018
			0x01, 0x30, 0x00, 0x30, 0x00,  // 2400  → 0x0030
			0x01, 0x48, 0x00, 0x48, 0x00,  // 3600  → 0x0048
			0x01, 0x60, 0x00, 0x60, 0x00,  // 4800  → 0x0060
			0x01, 0x78, 0x00, 0x78, 0x00,  // 6000  → 0x0078
			0x01, 0xF0, 0x00, 0xF0, 0x00,  // 12000 → 0x00F0
			0x00, 0x00, 0x00, 0x00, 0x00,  // stage 8 unused → disabled + zeroed
		];
		Assert.Equal(expected, table);
	}

	[Fact]
	public void BuildTable_WritesTheSameRegisterToBothAxes()
	{
		var table = TaipanDpiTable.BuildTable(Stages(1200));
		var x = table[TaipanDpiTable.StagesOffset + 1] | (table[TaipanDpiTable.StagesOffset + 2] << 8);
		var y = table[TaipanDpiTable.StagesOffset + 3] | (table[TaipanDpiTable.StagesOffset + 4] << 8);
		Assert.Equal(x, y);
		Assert.Equal(0x0018, x);
	}

	[Fact]
	public void BuildTable_DisablesAndZeroesUnusedStages()
	{
		var table = TaipanDpiTable.BuildTable(Stages(800, 1200));

		for (var i = 2; i < TaipanDpiTable.MaxStages; i++)
		{
			var o = TaipanDpiTable.StagesOffset + i * TaipanDpiTable.BytesPerStage;
			Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, table.Skip(o).Take(TaipanDpiTable.BytesPerStage).ToArray());
		}
	}

	// The invariant the whole custom-DPI safety argument rests on. Vendor Q&A T7 leaves two
	// candidate layouts for a stage entry — [enable, X_L, X_H, Y_L, Y_H] (what this class writes)
	// and [enable, X_value, X_double, Y_value, Y_double] — which decode the same bytes to different
	// DPIs. While the register fits in ONE byte the high byte is 0x00, so both layouts emit
	// identical bytes and the ambiguity cannot bite. The register is CPI/50, so that holds up to
	// 12750 DPI; the catalog caps both mice at 12000 precisely to stay inside it. If this test ever
	// fails, someone raised DpiMax past 12750 and T7 must be settled on hardware first.
	// See docs/mouse/taipan/protocol.md §DPI and docs/plans/add-custom-dpi-values/.
	[Theory]
	[InlineData(200)]
	[InlineData(800)]
	[InlineData(2450)]
	[InlineData(12000)]
	[InlineData(12750)]
	public void BuildTable_KeepsTheRegisterInsideOneByte_ForEverySettableValue(int cpi)
	{
		var table = TaipanDpiTable.BuildTable(Stages(cpi));
		var offset = TaipanDpiTable.StagesOffset;

		Assert.Equal(0, table[offset + 2]); // X high byte
		Assert.Equal(0, table[offset + 4]); // Y high byte
		Assert.Equal(cpi / TaipanDpiTable.CpiUnit, table[offset + 1]);
	}

	// A user-defined ladder is any length between 1 and 8, and the count byte is what tells the
	// firmware how far to read.
	[Theory]
	[InlineData(1)]
	[InlineData(3)]
	[InlineData(5)]
	[InlineData(8)]
	public void BuildTable_WritesTheCountOfAUserDefinedLadder(int count)
	{
		var table = TaipanDpiTable.BuildTable(Stages(Enumerable.Repeat(800, count).ToArray()));

		Assert.Equal(count, table[TaipanDpiTable.StageCountOffset]);
		for (var i = 0; i < count; i++)
			Assert.Equal(1, table[TaipanDpiTable.StagesOffset + i * TaipanDpiTable.BytesPerStage]);
		for (var i = count; i < TaipanDpiTable.MaxStages; i++)
			Assert.Equal(0, table[TaipanDpiTable.StagesOffset + i * TaipanDpiTable.BytesPerStage]);
	}

	[Fact]
	public void BuildTable_IgnoresStagesBeyondDeviceMaximum()
	{
		var table = TaipanDpiTable.BuildTable(Stages(Enumerable.Repeat(800, TaipanDpiTable.MaxStages + 3).ToArray()));
		Assert.Equal(TaipanDpiTable.MaxStages, table[TaipanDpiTable.StageCountOffset]);
		Assert.Equal(TaipanDpiTable.Length, table.Length);
	}

	[Fact]
	public void BuildTable_HandlesNoStages()
	{
		var table = TaipanDpiTable.BuildTable([]);
		Assert.Equal(0, table[TaipanDpiTable.StageCountOffset]);
		Assert.Equal(TaipanDpiTable.Length, table.Length);
	}
}
