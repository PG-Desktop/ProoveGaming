using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.ViewModels;

/// <summary>
/// One row of the "Configured Extended Keys" list: the key chip(s), the coloured mode badge between
/// them, the output chip(s) of a single-key mode (MT hold / tap, TGL toggled key), optional parameter text (SOCD rule
/// priority, MT / TGL threshold), an optional marker under the chip a SOCD absolute priority favours,
/// and the delete command. Immutable — a change to the set rebuilds the rows.
/// </summary>
public sealed class ExtendedKeyRowViewModel
{
	// Badge colours per mode — the single source; views bind BadgeBrush and never restate a hex.
	public const string RapidSnapBadgeHex = "#00CE4F";
	public const string SocdBadgeHex = "#F5C542";
	public const string ModTapBadgeHex = "#FF383C";
	public const string ToggleBadgeHex = "#3B9EFF";

	public required ExtendedKeyRecord Record { get; init; }

	public required string Key1Label { get; init; }

	/// <summary>The second key of a pair; null on the single-key modes, which hides the chip.</summary>
	public string? Key2Label { get; init; }

	public bool HasKey2 => Key2Label is not null;

	/// <summary>The short mode tag drawn in the badge ("RS", "SOCD", "MT", "TGL").</summary>
	public required string BadgeText { get; init; }

	public required IBrush BadgeBrush { get; init; }

	/// <summary>Trailing parameter text ("Last Input Priority", "43 ms"); null when the mode has none.</summary>
	public string? ParameterText { get; init; }

	public bool HasParameterText => !string.IsNullOrEmpty(ParameterText);

	/// <summary>Small tag under Key 1's chip ("Absolute Priority" when a SOCD pair favours it); null hides it.</summary>
	public string? Key1BadgeText { get; init; }

	/// <summary>As <see cref="Key1BadgeText"/>, for Key 2.</summary>
	public string? Key2BadgeText { get; init; }

	public bool HasKey1Badge => !string.IsNullOrEmpty(Key1BadgeText);
	public bool HasKey2Badge => !string.IsNullOrEmpty(Key2BadgeText);

	/// <summary>The first output of a single-key mode — MT hold or TGL toggled key ("CTRL (L)", "W"); null on the pair modes, which hides the chip.</summary>
	public string? HoldLabel { get; init; }

	/// <summary>The tap output (Mod-Tap only); null hides the chip.</summary>
	public string? TapLabel { get; init; }

	public bool HasHoldLabel => HoldLabel is not null;
	public bool HasTapLabel => TapLabel is not null;

	public IRelayCommand<ExtendedKeyRowViewModel>? DeleteCommand { get; init; }

	// Immutable, not SolidColorBrush: these are four constants that never animate, and a
	// SolidColorBrush is an AvaloniaObject whose Color getter verifies dispatcher access — so a row
	// built (or merely read) off the UI thread throws. Rows are now rebuilt behind an awaited storage
	// write, whose continuation need not land back on the dispatcher.
	public static IBrush BadgeBrushFor(int mode) => new ImmutableSolidColorBrush(Color.Parse(mode switch
	{
		ExtendedKeyMode.Socd => SocdBadgeHex,
		ExtendedKeyMode.ModTap => ModTapBadgeHex,
		ExtendedKeyMode.Toggle => ToggleBadgeHex,
		_ => RapidSnapBadgeHex,
	}));
}
