using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ProoveGaming.Common.Classes;
using ProoveGaming.Installer.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProoveGaming.Installer.Views;

internal partial class MainWindow : Window
{
	private InstallerStep _currentStep = InstallerStep.Splash;
	private const long MinimumRequiredInstallBytes = 500L * 1024 * 1024;

	public MainWindow()
	{
		InitializeComponent();
		GetInstallPathTextBox().Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallLayout.AppDataFolderName);
		GetBrowseButton().Click += OnBrowseClick!;
		GetInstallPathTextBox().TextChanged += OnInstallPathTextChanged!;
		GetWelcomeCancelButton().Click += OnCancelClick!;
		GetWelcomeNextButton().Click += OnNextClick!;
		GetWelcomeLanguageSelect().LanguageSelected += OnWelcomeLanguageSelected;
		GetAgreementBackButton().Click += OnBackClick!;
		GetAgreementNextButton().Click += OnNextClick!;
		GetFolderBackButton().Click += OnBackClick!;
		GetFolderInstallButton().Click += OnNextClick!;
		GetInstallingCancelButton().Click += OnCancelClick!;
		GetCompleteFinishButton().Click += OnNextClick!;
		GetSplashStepView().SplashCompleted += OnSplashCompleted!;
		GetAgreementStepView().AgreementScrollCompletionChanged += OnAgreementScrollCompletionChanged!;
		GetCancelModalHost().PrimaryCommand = ConfirmCancelModalCommand;
		GetCancelModalHost().SecondaryCommand = CloseCancelModalCommand;
		GetCancelModalHost().CloseCommand = CloseCancelModalCommand;

		UpdateStepUi();
	}

	protected override void OnClosed(EventArgs e)
	{
		CloseCancelModal();

		GetWelcomeCancelButton().Click -= OnCancelClick!;
		GetWelcomeNextButton().Click -= OnNextClick!;
		GetWelcomeLanguageSelect().LanguageSelected -= OnWelcomeLanguageSelected;
		GetInstallPathTextBox().TextChanged -= OnInstallPathTextChanged!;
		GetAgreementBackButton().Click -= OnBackClick!;
		GetAgreementNextButton().Click -= OnNextClick!;
		GetFolderBackButton().Click -= OnBackClick!;
		GetFolderInstallButton().Click -= OnNextClick!;
		GetInstallingCancelButton().Click -= OnCancelClick!;
		GetCompleteFinishButton().Click -= OnNextClick!;
		GetSplashStepView().SplashCompleted -= OnSplashCompleted!;
		GetAgreementStepView().AgreementScrollCompletionChanged -= OnAgreementScrollCompletionChanged!;

		base.OnClosed(e);
	}

	private void OnSplashCompleted(object? sender, EventArgs e)
	{
		if (_currentStep != InstallerStep.Splash)
		{
			return;
		}

		_currentStep = InstallerStep.Welcome;
		UpdateStepUi();
	}

	private void OnBackClick(object sender, RoutedEventArgs e)
	{
		if (_currentStep == InstallerStep.Splash)
		{
			return;
		}

		_currentStep--;
		UpdateStepUi();
	}

	private async void OnNextClick(object sender, RoutedEventArgs e)
	{
		if (_currentStep == InstallerStep.Agreement && !GetAgreementStepView().IsAgreementScrollCompleted)
		{
			ShowNativeMessageBox(
				LocalizationManager.GetString("Message_AgreementRequired"),
				LocalizationManager.GetString("Message_InfoTitle"));
			return;
		}

		if (_currentStep == InstallerStep.Folder)
		{
			LocalizationManager.PersistCurrentLanguage();

			var installPath = (GetInstallPathTextBox().Text ?? string.Empty).Trim();
			if (!await ValidateInstallPathAsync(installPath))
			{
				return;
			}

			_currentStep = InstallerStep.Installing;
			UpdateStepUi();
			await RunInstallAsync(installPath);
			return;
		}

		if (_currentStep == InstallerStep.Complete)
		{
			if (GetFinishLaunchAppCheckBox().IsChecked == true)
			{
				var installPath = (GetInstallPathTextBox().Text ?? string.Empty).Trim();
				var appExePath = ResolveInstalledAppExecutablePath(installPath);
				if (!string.IsNullOrWhiteSpace(appExePath))
				{
					Process.Start(new ProcessStartInfo
					{
						FileName = appExePath,
						UseShellExecute = true,
						WorkingDirectory = installPath
					});
				}
			}

			Close();
			return;
		}

		_currentStep++;
		UpdateStepUi();
	}

	private async void OnBrowseClick(object sender, RoutedEventArgs e)
	{
		try
		{
			var topLevel = TopLevel.GetTopLevel(this);
			if (topLevel?.StorageProvider is null)
			{
				return;
			}

			var currentPath = string.IsNullOrWhiteSpace(GetInstallPathTextBox().Text)
				? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
				: GetInstallPathTextBox().Text!;

			var startLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(currentPath);
			var options = new FolderPickerOpenOptions
			{
				Title = LocalizationManager.GetString("Folder_BrowseTitle"),
				SuggestedStartLocation = startLocation,
				AllowMultiple = false
			};

			var selectedFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
			var selectedFolder = selectedFolders.Count > 0 ? selectedFolders[0] : null;
			if (selectedFolder is null)
			{
				return;
			}

			var selectedPath = selectedFolder.Path;
			string resolvedPath;
			if (selectedPath.IsAbsoluteUri && selectedPath.IsFile)
			{
				resolvedPath = selectedPath.LocalPath;
			}
			else
			{
				resolvedPath = Uri.UnescapeDataString(selectedPath.ToString());
			}

			resolvedPath = EnsureDefaultInstallFolderSegment(resolvedPath);

			GetInstallPathTextBox().Text = resolvedPath;
		}
		catch
		{
			// Keep installer responsive if storage provider returns non-file/relative paths.
		}
	}

	private static string EnsureDefaultInstallFolderSegment(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return path;
		}

		try
		{
			var fullPath = Path.GetFullPath(path);
			var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

			if (normalizedPath.EndsWith(InstallLayout.AppDataFolderName, StringComparison.OrdinalIgnoreCase))
			{
				return normalizedPath;
			}

			var withDefaultFolder = Path.Combine(normalizedPath, InstallLayout.AppDataFolderName);
			return withDefaultFolder;
		}
		catch
		{
			return path;
		}
	}

	private void OnCancelClick(object sender, RoutedEventArgs e)
	{
		OpenCancelModal();
	}

	private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
	{
		if (_currentStep == InstallerStep.Complete)
		{
			Close();
			return;
		}

		OpenCancelModal();
	}

	private void OnMinimizeClick(object? sender, RoutedEventArgs e)
	{
		WindowState = WindowState.Minimized;
	}

	private void OnMaximizeClick(object? sender, RoutedEventArgs e)
	{
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
	}

	private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		BeginMoveDrag(e);
	}

	private void OnWelcomeLanguageSelected(string languageCode)
	{
		GetAgreementStepView().UpdateAgreementText();
		_ = ValidateInstallPathAsync((GetInstallPathTextBox().Text ?? string.Empty).Trim());
		UpdateStepUi();
	}

	private void OnAgreementScrollCompletionChanged(object? sender, bool isCompleted)
	{
		if (_currentStep != InstallerStep.Agreement)
		{
			return;
		}

		GetAgreementNextButton().IsEnabled = isCompleted;
	}

	private void UpdateStepUi()
	{
		GetSplashStepView().IsVisible = _currentStep == InstallerStep.Splash;
		GetWelcomeStepView().IsVisible = _currentStep == InstallerStep.Welcome;
		GetAgreementStepView().IsVisible = _currentStep == InstallerStep.Agreement;
		GetFolderStepView().IsVisible = _currentStep == InstallerStep.Folder;
		GetInstallingStepView().IsVisible = _currentStep == InstallerStep.Installing;
		GetCompleteStepView().IsVisible = _currentStep == InstallerStep.Complete;

		GetAgreementNextButton().IsEnabled = _currentStep != InstallerStep.Agreement || GetAgreementStepView().IsAgreementScrollCompleted;

		ApplyStepLayoutProfile(GetLayoutProfile(_currentStep));

		switch (_currentStep)
		{
			case InstallerStep.Welcome:
				GetHeaderTitleText().Text = LocalizationManager.GetString("Step1_Header_Title");
				GetHeaderSubtitleText().Text = LocalizationManager.GetString("Step1_Header_Subtitle");
				break;
			case InstallerStep.Agreement:
				GetHeaderTitleText().Text = LocalizationManager.GetString("Step2_Header_Title");
				GetHeaderSubtitleText().Text = LocalizationManager.GetString("Step2_Header_Subtitle");
				break;
			case InstallerStep.Folder:
				GetHeaderTitleText().Text = LocalizationManager.GetString("Step3_Header_Title");
				GetHeaderSubtitleText().Text = LocalizationManager.GetString("Step3_Header_Subtitle");
				break;
			case InstallerStep.Installing:
				GetHeaderTitleText().Text = LocalizationManager.GetString("Step4_Header_Title");
				GetHeaderSubtitleText().Text = LocalizationManager.GetString("Step4_Header_Subtitle");
				break;
			case InstallerStep.Complete:
				GetHeaderTitleText().Text = LocalizationManager.GetString("Step5_Header_Title");
				GetHeaderSubtitleText().Text = LocalizationManager.GetString("Step5_Header_Subtitle");
				break;
			case InstallerStep.Splash:
				GetHeaderTitleText().Text = LocalizationManager.GetString("Step0_Header_Title");
				GetHeaderSubtitleText().Text = LocalizationManager.GetString("Step0_Header_Subtitle");
				break;
		}
	}

	[DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
	private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint uType);

	private static void ShowNativeMessageBox(string text, string title) =>
		MessageBoxW(IntPtr.Zero, text, title, 0x00000040u);

	private void ApplyStepLayoutProfile(StepLayoutProfile profile)
	{
		GetHeaderTitleText().IsVisible = false;
		GetHeaderSubtitleText().IsVisible = false;

		GetRootLayoutGrid().Margin = profile.RootMargin;
		GetContentHostBorder().Margin = profile.ContentHostMargin;
		GetContentHostBorder().Padding = profile.ContentHostPadding;
	}

	private static StepLayoutProfile GetLayoutProfile(InstallerStep step) => step switch
	{
		InstallerStep.Splash => new StepLayoutProfile(
			HideShellChrome: true,
			RootMargin: new Thickness(24),
			ContentHostMargin: new Thickness(0, 24, 0, 24),
			ContentHostPadding: new Thickness(16)),

		InstallerStep.Welcome => new StepLayoutProfile(
			HideShellChrome: true,
			RootMargin: new Thickness(24, 20, 24, 40),
			ContentHostMargin: new Thickness(0),
			ContentHostPadding: new Thickness(0)),

		_ => new StepLayoutProfile(
			HideShellChrome: true,
			RootMargin: new Thickness(24, 20, 24, 40),
			ContentHostMargin: new Thickness(0),
			ContentHostPadding: new Thickness(0))
	};

	private async void OnInstallPathTextChanged(object? sender, TextChangedEventArgs e)
	{
		await ValidateInstallPathAsync((GetInstallPathTextBox().Text ?? string.Empty).Trim());
	}

	private async Task<bool> ValidateInstallPathAsync(string installPath)
	{
		if (string.IsNullOrWhiteSpace(installPath))
		{
			SetInstallPathError(LocalizationManager.GetString("Message_InvalidInstallPath"));
			return false;
		}

		if (installPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
		{
			SetInstallPathError(LocalizationManager.GetString("Message_InvalidInstallPathChars"));
			return false;
		}

		if (OperatingSystem.IsWindows() && ContainsInvalidWindowsPathSymbols(installPath))
		{
			SetInstallPathError(LocalizationManager.GetString("Message_InvalidInstallPathChars"));
			return false;
		}

		if (!Path.IsPathFullyQualified(installPath))
		{
			SetInstallPathError(LocalizationManager.GetString("Message_InvalidInstallPath"));
			return false;
		}

		try
		{
			Path.GetFullPath(installPath);
		}
		catch
		{
			SetInstallPathError(LocalizationManager.GetString("Message_InvalidInstallPath"));
			return false;
		}

		// The folder gets an administrators-only ACL and hosts a LocalSystem service, so it must be
		// ours: new, empty, or an existing Proove Gaming install. Locking down a drive root or the
		// user's Desktop would break it, and files already sitting in a foreign folder would load
		// into that service. Off the UI thread: this runs on every keystroke.
		var locationError = await Task.Run(() => GetInstallLocationErrorKey(installPath));
		if (locationError is not null)
		{
			SetInstallPathError(LocalizationManager.GetString(locationError));
			return false;
		}

		var requiredBytes = await GetRequiredInstallSizeBytesAsync();
		if (requiredBytes < MinimumRequiredInstallBytes)
		{
			requiredBytes = MinimumRequiredInstallBytes;
		}

		var availableBytes = GetAvailableBytesForInstallPath(installPath);
		if (availableBytes >= 0 && availableBytes < requiredBytes)
		{
			var requiredText = FormatSize(requiredBytes);
			var availableText = FormatSize(availableBytes);
			SetInstallPathError(string.Format(LocalizationManager.GetString("Message_InsufficientDiskSpace"), requiredText, availableText));
			return false;
		}

		SetInstallPathError(null);
		return true;
	}

	internal static string? GetInstallLocationErrorKey(string installPath) =>
		GetInstallLocationErrorKey(installPath, InstallSignature.IsProoveGamingInstall);

	// A non-empty folder is accepted when it is one of our installs, INCLUDING a damaged one (an AV
	// quarantine, a half-finished uninstall): reinstalling over it is how it gets repaired. See
	// InstallSignature for how "ours" is proven.
	internal static string? GetInstallLocationErrorKey(string installPath, Func<string, bool> isProoveGamingInstall)
	{
		if (!InstallSignature.IsAcceptableInstallLocation(installPath))
		{
			return "Message_InstallPathNotAllowed";
		}

		try
		{
			if (Directory.Exists(installPath) &&
				Directory.EnumerateFileSystemEntries(installPath).Any() &&
				!isProoveGamingInstall(installPath))
			{
				return "Message_InstallFolderNotEmpty";
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Unreadable says nothing about whether the folder is empty.
			return "Message_InvalidInstallPath";
		}

		return null;
	}

	private static bool ContainsInvalidWindowsPathSymbols(string path)
	{
		var trimmedPath = path.Trim();
		if (trimmedPath.Length == 0)
		{
			return true;
		}

		var segments = trimmedPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
		var invalidFileNameChars = Path.GetInvalidFileNameChars();

		for (var i = 0; i < segments.Length; i++)
		{
			var segment = segments[i];
			if (segment.Length == 2 && segment[1] == ':')
			{
				continue;
			}

			if (segment.IndexOfAny(invalidFileNameChars) >= 0)
			{
				return true;
			}
		}

		return false;
	}

	private void SetInstallPathError(string? errorText)
	{
		string errorTextColor = "#FF383C";
		string regularBorderColor = "#6357FF";

		var installPathError = GetInstallPathErrorText();
		var installPathBorder = GetInstallPathBorder();

		var hasError = !string.IsNullOrWhiteSpace(errorText);

		installPathError.Text = errorText ?? string.Empty;
		installPathError.Foreground = Brush.Parse(errorTextColor);
		installPathError.IsVisible = hasError;
		installPathBorder.BorderBrush = hasError ? Brush.Parse(errorTextColor) : Brush.Parse(regularBorderColor);
		installPathBorder.BorderThickness = hasError ? new Thickness(2) : new Thickness(1);

		GetFolderInstallButton().IsEnabled = !hasError;
	}
}
