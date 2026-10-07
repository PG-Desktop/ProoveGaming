using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ProoveGaming.App.Models;
using ProoveGaming.App.Views.Information;

namespace ProoveGaming.App.Views;

public partial class InformationView : UserControl
{
	// Same pair ProfileViewModel uses for its tab icons.
	private static readonly IBrush ActiveIconBrush = new SolidColorBrush(Color.Parse("#6357FF"));
	private static readonly IBrush InactiveIconBrush = new SolidColorBrush(Color.FromArgb(128, 190, 188, 234));

	// Pixel depth of the bottom fade-out: kept below the content's 64px bottom padding
	// so the document's last line always reads at full opacity at full scroll,
	// regardless of the window height (PR #133 review m1).
	private const double BottomFadeDepth = 40;

	public event Action? BackRequested;

	private LegalDocument _section = LegalDocument.PrivacyPolicy;

	public InformationView()
	{
		InitializeComponent();
		NavBackButton.Click += OnBackClick;
		// Static manager + singleton view: the subscription lives as long as the window,
		// nothing to unsubscribe (same convention as SettingsView / HomeViewModel).
		LocalizationManager.LanguageChanged += UpdateDocument;
		// Populate the host once so the page is never empty; per-visit reset comes from
		// MainWindow (Loaded is tied to visual-tree attachment, NOT IsVisible, so it
		// cannot serve as an "on every visit" hook — PR #131 review M1).
		ResetToFirstSection();
		// The mask lives on the content presenter (not the ScrollViewer) so the design
		// scrollbar stays fully opaque; recomputed per resize to keep the depth in pixels.
		DocumentScrollViewer.SizeChanged += (_, _) => UpdateDocumentFade();
	}

	private void UpdateDocumentFade()
	{
		var presenter = DocumentScrollViewer.FindDescendantOfType<ScrollContentPresenter>();
		var height = DocumentScrollViewer.Bounds.Height;
		if (presenter is null || height <= BottomFadeDepth)
			return;

		var fade = new LinearGradientBrush
		{
			StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
			EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
		};
		fade.GradientStops.Add(new GradientStop(Colors.White, 0));
		fade.GradientStops.Add(new GradientStop(Colors.White, 1 - BottomFadeDepth / height));
		fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
		presenter.OpacityMask = fade;
	}

	// Called by MainWindow before the step is shown: every visit opens on the first
	// tab, scrolled to the top (same pattern as AgreementTermsView.ShowDocument).
	public void ResetToFirstSection()
	{
		_section = LegalDocument.PrivacyPolicy;
		UpdateDocument();
	}

	private void OnBackClick(object? sender, RoutedEventArgs e)
	{
		BackRequested?.Invoke();
	}

	private void OnPrivacyPolicyTabClick(object? sender, RoutedEventArgs e) =>
		SelectSection(LegalDocument.PrivacyPolicy);

	private void OnTermsOfServiceTabClick(object? sender, RoutedEventArgs e) =>
		SelectSection(LegalDocument.TermsOfService);

	private void OnLicenseAgreementTabClick(object? sender, RoutedEventArgs e) =>
		SelectSection(LegalDocument.LicenseAgreement);

	private void SelectSection(LegalDocument section)
	{
		if (_section == section)
			return;

		_section = section;
		UpdateDocument();
	}

	private void UpdateDocument()
	{
		DocumentHost.Content = LegalDocumentContent.CreateView(_section);

		PrivacyPolicyHighlight.IsVisible = _section == LegalDocument.PrivacyPolicy;
		TermsOfServiceHighlight.IsVisible = _section == LegalDocument.TermsOfService;
		LicenseAgreementHighlight.IsVisible = _section == LegalDocument.LicenseAgreement;

		PrivacyPolicyIcon.Fill = PrivacyPolicyHighlight.IsVisible ? ActiveIconBrush : InactiveIconBrush;
		TermsOfServiceIcon.Fill = TermsOfServiceHighlight.IsVisible ? ActiveIconBrush : InactiveIconBrush;
		LicenseAgreementIcon.Fill = LicenseAgreementHighlight.IsVisible ? ActiveIconBrush : InactiveIconBrush;

		DocumentScrollViewer.Offset = new Avalonia.Vector(0, 0);
	}
}
