using System.Collections.Generic;
using System.ComponentModel;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The home card's audio/mic mute icons bind DeviceCardViewModel directly, and the catalog fills
// SupportedAudioSettings AFTER the card is constructed. So HasAudioIndicators must NOTIFY when that
// list is assigned — as a silent auto-property the card evaluated it once (empty, false) and never
// again, and the icons never appeared on the board while the details view showed them fine.
//
// The details view hid the bug: swapping DeviceDetailsViewModel.Device re-evaluates every Device.*
// binding, so it always re-read the flag. Only the card was affected, which is exactly why this
// needs its own test.
public class DeviceCardAudioIndicatorTests
{
	private static SingleCardViewModel Card() => new()
	{
		Name = "Cosmo",
		DeviceTypeLabel = "Wireless Headset",
	};

	[Fact]
	public void HasAudioIndicators_IsFalseBeforeTheCatalogArrives()
	{
		Assert.False(Card().HasAudioIndicators);
	}

	[Fact]
	public void HasAudioIndicators_IsTrueOnceTheCatalogPopulatesTheListAndTheDeviceIsConnected()
	{
		var card = Card();
		card.SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"];
		card.HasDongle = true;

		Assert.True(card.HasAudioIndicators);
	}

	// A disconnected device shows NO status icons. Without the Connected term the audio/mic pair
	// stayed on screen for a headset that was switched off, because "not muted" is true when there
	// is no endpoint to read.
	[Fact]
	public void HasAudioIndicators_IsFalseWhileDisconnected()
	{
		var card = Card();
		card.SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"];

		Assert.False(card.Connected);
		Assert.False(card.HasAudioIndicators);
	}

	// Losing the transport has to hide the icons, so the flag must be re-raised when it changes.
	[Fact]
	public void LosingTheConnection_RaisesHasAudioIndicators()
	{
		var card = Card();
		card.SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"];
		card.HasDongle = true;

		var raised = new List<string?>();
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
		card.HasDongle = false;

		Assert.Contains(nameof(DeviceCardViewModel.HasAudioIndicators), raised);
		Assert.False(card.HasAudioIndicators);
	}

	// The actual regression: without a notification the card's IsVisible binding never re-reads.
	[Fact]
	public void AssigningSupportedAudioSettings_RaisesHasAudioIndicators()
	{
		var card = Card();
		var raised = new List<string?>();
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		card.SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"];

		Assert.Contains(nameof(DeviceCardViewModel.HasAudioIndicators), raised);
	}

	// A device with no audio must never show the icons, so the flag has to stay false.
	[Fact]
	public void ADeviceWithoutAudioNeverShowsTheIndicators()
	{
		var card = Card();
		card.SupportedAudioSettings = [];

		Assert.False(card.HasAudioIndicators);
	}

	[Fact]
	public void MuteFlagsNotifyIndividually()
	{
		var card = Card();
		var raised = new List<string?>();
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		card.IsOutputSilent = true;
		card.IsMicrophoneSilent = true;

		Assert.Contains(nameof(DeviceCardViewModel.IsOutputSilent), raised);
		Assert.Contains(nameof(DeviceCardViewModel.IsMicrophoneSilent), raised);
	}

	// ── Silence, not mute ─────────────────────────────────────────────────────

	// The icon means "nothing will be heard". Windows tracks mute and level INDEPENDENTLY, so an
	// endpoint at volume 0 is not muted and GetMute keeps returning false — which is why a
	// zero-volume microphone originally kept showing the live Mic icon.
	[Theory]
	[InlineData(false, 50, false)]   // audible
	[InlineData(true, 50, true)]     // muted outright
	[InlineData(false, 0, true)]     // level 0 — the case that was missed
	[InlineData(true, 0, true)]      // both
	public void IsSilent_IsTrueWhenMutedOrAtZero(bool muted, int volume, bool expected)
	{
		Assert.Equal(expected, AudioMuteMonitor.IsSilent(muted, volume));
	}

	// A null reading means no endpoint resolved (headset off, unplugged). That is "unknown", not
	// "silent" — reporting silence would put a false warning on a device that is not even there.
	[Theory]
	[InlineData(null, null)]
	[InlineData(null, 50)]
	[InlineData(false, null)]
	public void IsSilent_TreatsAnUnknownReadingAsAudible(bool? muted, int? volume)
	{
		Assert.False(AudioMuteMonitor.IsSilent(muted, volume));
	}
}
