using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// The param_1 status bitmask names the blocks an apply CHANGES. We used to hard-code 0x07 on every
// apply, which made the firmware parse block 0 even for a pure LED change — and a block 0 holding a
// zero DPI entry is rejected, taking the LED write down with it. That is why colour cycle never
// applied on hardware while the OEM's own byte-for-byte replay always did.
//
// Bit layout: bit0 = block 0 (DPI table + debounce/lift-off), bit1 = block 1 (LED), bit2 = block 2
// (key matrix).
public class EasenBlocksMaskTests
{
	private static DpiStageParams Stage(ushort dpi) => new(dpi, 0, 0, 0);

	// The case the bug was found on: an LED-only apply must not declare block 0.
	[Fact]
	public void LedOnlyApply_DeclaresBlock1Only()
	{
		var config = new DeviceConfiguration
		{
			Led = new LedConfig(0, 0, 0xFF, Effect: 0x03, Brightness: 0xBD, Speed: 4),
		};

		Assert.Equal(0x02, EasenMouseService.ChangedBlocksMask(config));
	}

	[Fact]
	public void SettingsOrDpiApply_DeclaresBlock0()
	{
		Assert.Equal(0x01, EasenMouseService.ChangedBlocksMask(
			new DeviceConfiguration { DpiStages = [Stage(800), Stage(1600)] }));
	}

	[Fact]
	public void ButtonApply_DeclaresBlock2()
	{
		var config = new DeviceConfiguration
		{
			Buttons = [default],
		};

		Assert.Equal(0x04, EasenMouseService.ChangedBlocksMask(config));
	}

	// A full preset apply still writes everything, so the old 0x07 remains correct there.
	[Fact]
	public void FullApply_DeclaresAllThreeBlocks()
	{
		var config = new DeviceConfiguration
		{
			DpiStages = [Stage(800)],
			Led = new LedConfig(0, 0, 0xFF, Effect: 0x01, Brightness: 0xFC, Speed: 4),
			Buttons = [default],
		};

		Assert.Equal(0x07, EasenMouseService.ChangedBlocksMask(config));
	}

	// Regression: this used to assert an empty config "falls back to all blocks" (0x07). That
	// fallback is what made a DPI-stage change wipe button remaps — the composer builds block 2 from
	// the FACTORY key matrix whenever config.Buttons is absent, so declaring block 2 commits factory
	// defaults over the user's bindings. Nothing to declare must mean nothing to write.
	[Fact]
	public void EmptyApply_DeclaresNoBlocks()
	{
		Assert.Equal(0, EasenMouseService.ChangedBlocksMask(new DeviceConfiguration()));
	}

	// The two configs that actually hit that path in production. Both are live gRPC endpoints
	// (DeviceService.SetActiveDpiStage / SetReportRate) and both live entirely in param_0, so
	// neither may declare a param_1 block.
	[Fact]
	public void ActiveStageOnlyApply_DeclaresNoBlocks()
	{
		Assert.Equal(0, EasenMouseService.ChangedBlocksMask(
			new DeviceConfiguration { ActiveStage = 3 }));
	}

	[Fact]
	public void ReportRateOnlyApply_DeclaresNoBlocks()
	{
		Assert.Equal(0, EasenMouseService.ChangedBlocksMask(
			new DeviceConfiguration { ReportRateHz = 500 }));
	}

	// The reason the mask matters at all: with no Buttons supplied the composer emits the FACTORY
	// key matrix, not the device's current one. Anything that declares block 2 without supplying
	// bindings therefore destroys them — so this pins the composer half of the contract too.
	[Fact]
	public void ComposerEmitsTheFactoryKeyMatrixWhenNoButtonsAreSupplied()
	{
		var composed = X43ConfigComposer.Compose(
			new DeviceConfiguration { ActiveStage = 3 },
			new byte[64], new byte[64],
			cachedColorSlot: 0, cachedLedR: 0, cachedLedG: 0, cachedLedB: 0);

		Assert.Equal(X43KeyMatrix.CreateDefault(), composed.Block2);
	}

	// Empty collections are not changes. Sending 0x04 for an empty button map would declare the key
	// matrix changed and make the firmware rewrite it for nothing.
	[Fact]
	public void EmptyCollections_AreNotTreatedAsChanges()
	{
		var config = new DeviceConfiguration
		{
			DpiStages = [],
			Buttons = [],
			Led = new LedConfig(0, 0, 0xFF, Effect: 0x01, Brightness: 0xFC, Speed: 4),
		};

		Assert.Equal(0x02, EasenMouseService.ChangedBlocksMask(config));
	}

	// A key-matrix write leaves the LED dark on this firmware unless block 1 is committed with it.
	// The mask itself does not add that bit (it reports what the CONFIG changes) — ApplyConfiguration
	// ORs it in when a colour has already been applied. Pinned so the mask's contract stays narrow.
	[Fact]
	public void ButtonApply_MaskItselfDoesNotClaimTheLedChanged()
	{
		var config = new DeviceConfiguration { Buttons = [default] };

		Assert.Equal(0x04, EasenBlocksMask(config));
		Assert.Equal(0, EasenBlocksMask(config) & 0x02);
	}

	private static byte EasenBlocksMask(DeviceConfiguration config)
		=> EasenMouseService.ChangedBlocksMask(config);
}
