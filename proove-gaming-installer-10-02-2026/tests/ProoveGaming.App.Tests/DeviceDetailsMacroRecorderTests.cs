using System.Collections.Generic;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The recorder's step limit is per device (TAIPAN 32, BLAZE 62 — the encoders' MaxEvents), and the
// "maximum of N steps reached" message must name the limit recording actually stopped at. It used
// to keep the value its binding read first (62, from no device or a BLAZE), so a TAIPAN stopped at
// 32 strings while announcing 62.
public class DeviceDetailsMacroRecorderTests
{
	private const byte KeyA = 0x04;

	private static SingleCardViewModel Taipan() => new()
	{
		Name = "PG TAIPAN",
		DeviceTypeLabel = "Wireless Mouse",
		VendorId = 9354,
		ProductId = 33651,
		SupportsMacros = true,
	};

	[Fact]
	public void Taipan_StopsAt32Steps_AndRefreshesTheLimitMessage()
	{
		var vm = new DeviceDetailsViewModel();
		_ = vm.RecordLimitLabel; // what a binding made before the device was known would hold

		vm.Device = Taipan();
		var notified = new List<string?>();
		vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

		vm.ToggleRecordingCommand.Execute(null);
		for (var i = 0; i < 40; i++)
			vm.CaptureKeyEvent(KeyA, isPress: i % 2 == 0, "A");

		Assert.Equal(32, vm.RecordedSteps.Count);
		Assert.True(vm.IsRecordLimitReached);
		Assert.False(vm.IsRecording);
		Assert.Contains(nameof(DeviceDetailsViewModel.RecordLimitLabel), notified);
	}
}
