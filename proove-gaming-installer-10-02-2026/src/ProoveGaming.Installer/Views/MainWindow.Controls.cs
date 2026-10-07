using Avalonia.Controls;
using ProoveGaming.Common.Controls;
using ProoveGaming.Common.Layouts;

namespace ProoveGaming.Installer.Views;

internal partial class MainWindow
{
	private Grid GetMainContentGrid() => this.FindControl<Grid>("MainContentGrid")!;
	// Background image + content: the whole subtree the modal blurs behind itself.
	private BackgroundLayoutView GetBackgroundLayoutRoot() => this.FindControl<BackgroundLayoutView>("BackgroundLayoutRoot")!;
	private TextBlock GetHeaderTitleText() => this.FindControl<TextBlock>("HeaderTitleText")!;
	private TextBlock GetHeaderSubtitleText() => this.FindControl<TextBlock>("HeaderSubtitleText")!;
	private Grid GetRootLayoutGrid() => this.FindControl<Grid>("RootLayoutGrid")!;
	private Border GetCancelModalOverlay() => this.FindControl<Border>("CancelModalOverlay")!;
	private Controls.ModalCancelView GetCancelModalHost() => this.FindControl<Controls.ModalCancelView>("CancelModalHost")!;
	private Border GetContentHostBorder() => this.FindControl<Border>("ContentHostBorder")!;
	private SplashStepView GetSplashStepView() => this.FindControl<SplashStepView>("SplashStepView")!;
	private WelcomeStepView GetWelcomeStepView() => this.FindControl<WelcomeStepView>("WelcomeStepView")!;
	private AgreementStepView GetAgreementStepView() => this.FindControl<AgreementStepView>("AgreementStepView")!;
	private FolderStepView GetFolderStepView() => this.FindControl<FolderStepView>("FolderStepView")!;
	private InstallingStepView GetInstallingStepView() => this.FindControl<InstallingStepView>("InstallingStepView")!;
	private CompleteStepView GetCompleteStepView() => this.FindControl<CompleteStepView>("CompleteStepView")!;

	private Button GetWelcomeLanguageButton() => GetWelcomeStepView().FindControl<Button>("WelcomeLanguageButton")!;
	private LanguageSelectView GetWelcomeLanguageSelect() => GetWelcomeStepView().FindControl<LanguageSelectView>("WelcomeLanguageSelect")!;
	private Button GetWelcomeCancelButton() => GetWelcomeStepView().FindControl<Button>("WelcomeCancelButton")!;
	private Button GetWelcomeNextButton() => GetWelcomeStepView().FindControl<Button>("WelcomeNextButton")!;
	private Button GetAgreementBackButton() => GetAgreementStepView().FindControl<Button>("AgreementBackButton")!;
	private Button GetAgreementNextButton() => GetAgreementStepView().FindControl<Button>("AgreementNextButton")!;
	private Button GetFolderBackButton() => GetFolderStepView().FindControl<Button>("FolderBackButton")!;
	private Button GetFolderInstallButton() => GetFolderStepView().FindControl<Button>("FolderInstallButton")!;
	private Button GetInstallingCancelButton() => GetInstallingStepView().FindControl<Button>("InstallingCancelButton")!;
	private Button GetCompleteFinishButton() => GetCompleteStepView().FindControl<Button>("CompleteFinishButton")!;
	private TextBox GetInstallPathTextBox() => GetFolderStepView().FindControl<TextBox>("InstallPathTextBox")!;
	private Border GetInstallPathBorder() => GetFolderStepView().FindControl<Border>("InstallPathBorder")!;
	private TextBlock GetInstallPathErrorText() => GetFolderStepView().FindControl<TextBlock>("InstallPathErrorText")!;
	private Button GetBrowseButton() => GetFolderStepView().FindControl<Button>("BrowseButton")!;
	private CheckBox GetFinishLaunchAppCheckBox() => GetCompleteStepView().FindControl<CheckBox>("FinishLaunchAppCheckBox")!;
	private CheckBox GetCreateDesktopShortcutCheckBox() => GetFolderStepView().FindControl<CheckBox>("CreateDesktopShortcutCheckBox")!;
	private CheckBox GetCreateStartMenuShortcutCheckBox() => GetFolderStepView().FindControl<CheckBox>("CreateStartMenuShortcutCheckBox")!;
	private TextBlock GetStatusText() => GetInstallingStepView().FindControl<TextBlock>("StatusText")!;
	private ProgressWithPercentageView GetInstallProgressView() => GetInstallingStepView().FindControl<ProgressWithPercentageView>("InstallProgressView")!;
}
