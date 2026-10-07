using Avalonia.Controls;

namespace ProoveGaming.Common.Controls;

// Opens the mail client for the inline email links in the legal documents (App
// Information/agreement pages and the installer agreement step). The links are
// Buttons, so primary click, Enter and Space all activate them and Tab reaches them.
public static class LegalEmailLink
{
	public static void OpenFrom(object? sender)
	{
		if (sender is not Button button || button.Content is not string address || string.IsNullOrWhiteSpace(address))
			return;

		_ = TopLevel.GetTopLevel(button)?.Launcher.LaunchUriAsync(new System.Uri("mailto:" + address));
	}
}
