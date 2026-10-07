using System.Collections.Generic;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// A live language switch (Settings page → language dropdown) cannot reach the card's code-built
// strings through DynamicResource: the type label rides a converter binding and the connection
// tooltip is a computed getter over LocalizationManager.GetString. HomeViewModel's LanguageChanged
// handler therefore calls RefreshLocalizedText() on every card. These tests pin the helper's
// contract: it re-raises exactly the two localized properties and mutates nothing — which is what
// keeps the language-changed handler notification-only (no property writes, no re-entrancy).
// HomeViewModel itself is not constructed here (its ctor needs the full device/session service
// graph) — the repo-wide pattern is to drive the helper directly, as with the grouping helpers.
public class DeviceCardLanguageRefreshTests
{
	private static SingleCardViewModel Card() => new()
	{
		Name = "Test Device",
		DeviceTypeLabel = "Wired Mouse",
	};

	// Exactly the two names, in a stable order: DeviceTypeLabel re-runs its
	// DeviceTypeLabelConverter binding, ConnectionTooltip re-reads its resource strings. Anything
	// extra would mean the language handler started touching state it does not own.
	[Fact]
	public void RefreshLocalizedText_RaisesExactlyDeviceTypeLabelThenConnectionTooltip()
	{
		var card = Card();
		var raised = new List<string?>();
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		card.RefreshLocalizedText();

		Assert.Equal(
			new[]
			{
				nameof(DeviceCardViewModel.DeviceTypeLabel),
				nameof(DeviceCardViewModel.ConnectionTooltip),
			},
			raised);
	}

	// Notification-only: the helper must not write any property, or the LanguageChanged handler
	// that walks every card could re-enter itself through the cards' own change notifications.
	[Fact]
	public void RefreshLocalizedText_DoesNotMutateCardState()
	{
		var card = Card();
		card.HasBluetooth = true;
		var typeLabelBefore = card.DeviceTypeLabel;
		var tooltipBefore = card.ConnectionTooltip;

		card.RefreshLocalizedText();

		Assert.Equal(typeLabelBefore, card.DeviceTypeLabel);
		Assert.Equal(tooltipBefore, card.ConnectionTooltip);
		Assert.True(card.HasBluetooth);
	}

	// Repeat invocations must stay safe — the user can flip en↔uk in Settings as often as they
	// like, and every flip re-runs the refresh over the same cards.
	[Fact]
	public void RefreshLocalizedText_IsRepeatable()
	{
		var card = Card();
		var raised = new List<string?>();
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		card.RefreshLocalizedText();
		card.RefreshLocalizedText();

		Assert.Equal(4, raised.Count);
	}
}
