using Avalonia.Controls;
using ProoveGaming.App.Views.DeviceDetailsDialogs;
using ProoveGaming.App.Views.Dialogs;
using ProoveGaming.Common.Layouts;

namespace ProoveGaming.App.Views;

public partial class MainWindow
{
	private Grid GetRootLayoutGrid() => this.FindControl<Grid>("RootLayoutGrid")!;
	private Border GetContentHostBorder() => this.FindControl<Border>("ContentHostBorder")!;
	private StackPanel GetLanguagePanel() => this.FindControl<StackPanel>("LanguagePanel")!;
	private TextBlock GetHeaderTitleText() => this.FindControl<TextBlock>("HeaderTitleText")!;
	private TextBlock GetHeaderSubtitleText() => this.FindControl<TextBlock>("HeaderSubtitleText")!;
	private WelcomeView GetWelcomeView() => this.FindControl<WelcomeView>("WelcomeView")!;
	private HomeView GetHomeView() => this.FindControl<HomeView>("HomeView")!;
	private AgreementTermsView GetAgreementTermsView() => this.FindControl<AgreementTermsView>("AgreementTermsView")!;
	private ChooseNicknameView GetChooseNicknameView() => this.FindControl<ChooseNicknameView>("ChooseNicknameView")!;
	private StepFrameLayoutView GetStepLayoutView() => this.FindControl<StepFrameLayoutView>("StepLayoutView")!;
	private ProfileView GetProfileView() => this.FindControl<ProfileView>("ProfileView")!;
	private InformationView GetInformationView() => this.FindControl<InformationView>("InformationView")!;
	private SettingsView GetSettingsView() => this.FindControl<SettingsView>("SettingsView")!;
	private DeviceDetailsView GetDeviceDetailsView() => this.FindControl<DeviceDetailsView>("DeviceDetailsView")!;
	private Grid GetNotificationGrid() => this.FindControl<Grid>("NotificationGrid")!;
	private TextBlock GetNotificationText() => this.FindControl<TextBlock>("NotificationText")!;
	private Button GetNotificationCloseButton() => this.FindControl<Button>("NotificationCloseButton")!;
	private Panel GetNotificationsPanel() => this.FindControl<Panel>("NotificationsPanel")!;
	private BackgroundLayoutView GetBackgroundLayoutHost() => this.FindControl<BackgroundLayoutView>("BackgroundLayoutHost")!;
	private Grid GetDialogOverlay() => this.FindControl<Grid>("DialogOverlay")!;
	private Panel GetLogoutDialogPanel() => this.FindControl<Panel>("LogoutDialogPanel")!;
	private Panel GetContactUsDialogPanel() => this.FindControl<Panel>("ContactUsDialogPanel")!;
	private Panel GetDeleteDialogPanel() => this.FindControl<Panel>("DeleteDialogPanel")!;
	private Panel GetCreateMacroDialogPanel() => this.FindControl<Panel>("CreateMacroDialogPanel")!;
	private Panel GetDeleteMacroDialogPanel() => this.FindControl<Panel>("DeleteMacroDialogPanel")!;
	private NewPresetDialog GetNewPresetDialogPanel() => this.FindControl<NewPresetDialog>("NewPresetDialogPanel")!;
	private ConfirmDialog GetResetPresetDialogPanel() => this.FindControl<ConfirmDialog>("ResetPresetDialogPanel")!;
	private ConfirmDialog GetDeletePresetDialogPanel() => this.FindControl<ConfirmDialog>("DeletePresetDialogPanel")!;
	private ConfirmDialog GetDeleteDpiPointDialogPanel() => this.FindControl<ConfirmDialog>("DeleteDpiPointDialogPanel")!;
	private ConfirmDialog GetResetExtendedKeysDialogPanel() => this.FindControl<ConfirmDialog>("ResetExtendedKeysDialogPanel")!;
	private SavePresetDialog GetSavePresetDialogPanel() => this.FindControl<SavePresetDialog>("SavePresetDialogPanel")!;
	private UnsavedChangesDialog GetUnsavedChangesDialogPanel() => this.FindControl<UnsavedChangesDialog>("UnsavedChangesDialogPanel")!;
	private KeyAssignmentDialog GetKeyAssignmentDialogPanel() => this.FindControl<KeyAssignmentDialog>("KeyAssignmentDialogPanel")!;
	private Button? GetMacroRecordButton() => this.FindControl<Button>("MacroRecordButton");
}
