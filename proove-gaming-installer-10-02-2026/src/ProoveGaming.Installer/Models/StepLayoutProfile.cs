using Avalonia;

namespace ProoveGaming.Installer.Models;

internal sealed record StepLayoutProfile(
	bool HideShellChrome,
	Thickness RootMargin,
	Thickness ContentHostMargin,
	Thickness ContentHostPadding);
