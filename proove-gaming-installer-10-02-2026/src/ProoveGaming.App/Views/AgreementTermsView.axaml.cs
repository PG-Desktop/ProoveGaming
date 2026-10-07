using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ProoveGaming.App.Models;
using ProoveGaming.App.Views.Information;

namespace ProoveGaming.App.Views;

public partial class AgreementTermsView : UserControl
{
	public event Action? BackRequested;

	public AgreementTermsView()
	{
		InitializeComponent();
		BackButton.Click += OnBackClick;
	}

	// Called by MainWindow before the step is shown. The content is resolved at click
	// time for the current app language, so no LanguageChanged subscription is needed
	// (the language cannot change while this step is up).
	public void ShowDocument(LegalDocument document)
	{
		TitleText.Text = LocalizationManager.GetString(LegalDocumentContent.TitleResourceKey(document));
		DocumentHost.Content = LegalDocumentContent.CreateView(document);
		AgreementScrollViewer.Offset = new Avalonia.Vector(0, 0);
	}

	private void OnBackClick(object? sender, RoutedEventArgs e)
	{
		BackRequested?.Invoke();
	}
}
