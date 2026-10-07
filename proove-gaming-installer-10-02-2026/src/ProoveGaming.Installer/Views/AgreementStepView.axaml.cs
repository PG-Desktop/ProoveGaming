using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ProoveGaming.Common.Localization;

namespace ProoveGaming.Installer.Views;

internal partial class AgreementStepView : UserControl
{
	private bool _isScrolledToEnd;

	public event EventHandler<bool>? AgreementScrollCompletionChanged;

	public bool IsAgreementScrollCompleted => _isScrolledToEnd;

	public AgreementStepView()
	{
		InitializeComponent();
		var scrollViewer = GetAgreementScrollViewer();
		scrollViewer.ScrollChanged += OnAgreementScrollChanged;
		UpdateAgreementText();
	}

	public void UpdateAgreementText()
	{
		var contentHost = this.FindControl<ContentControl>("AgreementContentHost");
		if (contentHost is null)
		{
			return;
		}

		var languageCode = LanguagePreferenceStore.NormalizeLanguage(LocalizationManager.CurrentLanguageCode);
		contentHost.Content = languageCode == LocalizationConstants.UkrainianLanguageCode
			? new AgreementTermsUkView()
			: new AgreementTermsEnView();

		ResetScrollCompletion();
	}

	private void OnAgreementScrollChanged(object? sender, ScrollChangedEventArgs e)
	{
		var scrollViewer = sender as ScrollViewer;
		if (scrollViewer is null)
		{
			return;
		}

		var reachedBottom = scrollViewer.Extent.Height <= scrollViewer.Viewport.Height ||
							scrollViewer.Offset.Y + scrollViewer.Viewport.Height >= scrollViewer.Extent.Height - 1;

		if (_isScrolledToEnd == reachedBottom)
		{
			return;
		}

		_isScrolledToEnd = reachedBottom;
		AgreementScrollCompletionChanged?.Invoke(this, _isScrolledToEnd);
	}

	private void ResetScrollCompletion()
	{
		var scrollViewer = GetAgreementScrollViewer();
		scrollViewer.Offset = new Avalonia.Vector(0, 0);

		if (_isScrolledToEnd)
		{
			_isScrolledToEnd = false;
			AgreementScrollCompletionChanged?.Invoke(this, false);
		}
	}

	private ScrollViewer GetAgreementScrollViewer() => this.FindControl<ScrollViewer>("AgreementScrollViewer")!;

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
