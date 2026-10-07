using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Views;
using Serilog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App;

public partial class App : Application
{
	private static string? _singleInstanceActivationEventName;
	private EventWaitHandle? _singleInstanceActivationEvent;
	private CancellationTokenSource? _singleInstanceActivationCts;
	private Task? _singleInstanceActivationTask;
	private TrayIcon? _trayIcon;
	private NativeMenuItem? _openMenuItem;
	private NativeMenuItem? _exitMenuItem;
	private MainWindow? _mainWindow;
	private IServiceProvider? _services;

	public static void InitializeSingleInstanceActivationSignal(string eventName) =>
		_singleInstanceActivationEventName = eventName;

	public static IServiceProvider Services =>
		((App)Current!)._services ?? throw new InvalidOperationException("Services not initialized.");

	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
#if DEBUG
		this.AttachDeveloperTools();
#endif
	}

	public override void OnFrameworkInitializationCompleted()
	{
		LocalizationManager.InitializeFromSystemCulture();

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			InitializeSingleInstanceActivation(desktop);

			_services = BuildServices();

			// Install before anything else touches the device stack: from here on, an unhandled
			// exception on the UI thread or in a fire-and-forget task is logged instead of closing
			// the app. See AppResilience for why each source is handled the way it is.
			AppResilience.Install(
				_services.GetRequiredService<ILogger<App>>(),
				(title, body) => _services!.GetRequiredService<SystemNotificationService>().Show(title, body));

			var localStorage = _services.GetRequiredService<LocalStorageService>();

			// Restore "Remember me" authentication if still valid — BEFORE touching the
			// persisted guest session: a signed-in launch must never resurrect one.
			var savedAuth = localStorage.GetSavedAuth();
			var hasValidAuth = savedAuth != null && !savedAuth.IsExpired;
			if (savedAuth != null && savedAuth.IsExpired)
			{
				localStorage.ClearSavedAuth();
			}
			else if (hasValidAuth)
			{
				var userProfile = _services.GetRequiredService<UserProfile>();
				userProfile.CustomerId = savedAuth!.CustomerId;
				userProfile.Email = savedAuth.Email;
				userProfile.DisplayName = savedAuth.Nickname;
				userProfile.SessionToken = savedAuth.Token;
				userProfile.SessionTokenExpiresAt = savedAuth.TokenExpiresAtUtc;
			}

			var (sessionState, sessionUuid, startedAt) = localStorage.GetSessionState();
			if (sessionState == SessionState.Expired)
			{
				localStorage.ClearData();
			}
			else if (sessionState == SessionState.Active && sessionUuid.HasValue)
			{
				if (hasValidAuth)
				{
					// Stale row left by a sign-in that predates ClearSession-on-sign-in:
					// drop it instead of restoring a phantom guest session next to the
					// authenticated user (guest-owned preference rows stay).
					localStorage.ClearSession();
				}
				else
				{
					var session = _services.GetRequiredService<SessionService>();
					session.RestoreSession(sessionUuid.Value, localStorage.GetDevices(), startedAt);
				}
			}

			desktop.Exit += (_, _) =>
			{
				_singleInstanceActivationCts?.Cancel();
				_singleInstanceActivationTask?.Wait(TimeSpan.FromMilliseconds(200));
				_singleInstanceActivationEvent?.Dispose();
				_singleInstanceActivationCts?.Dispose();
				_singleInstanceActivationTask?.Dispose();
				_singleInstanceActivationEvent = null;
				_singleInstanceActivationCts = null;
				_singleInstanceActivationTask = null;

				// Bounded drain of the storage executor: queued writes must reach disk before
				// the final Flush and the container dispose tear the connection down. A blocking
				// wait is acceptable here and only here — the window is gone, nothing paints,
				// and the alternative is losing the queued tail.
				localStorage.DrainStorageQueueAsync().Wait(TimeSpan.FromSeconds(3));
				var session = _services.GetRequiredService<SessionService>();
				if (session.CurrentSession != null)
					localStorage.Flush(session.CurrentSession);
				(_services as IDisposable)?.Dispose();
			};

			_mainWindow = _services.GetRequiredService<MainWindow>();
			_mainWindow.MinimizeToTrayRequested += OnMinimizeToTrayRequested;
			desktop.MainWindow = _mainWindow;
			InitializeTray(desktop);

			// After MainWindow resolution, so HomeViewModel (a MainWindow ctor dependency) is
			// already subscribed to UpdateAvailable — no missed first announcement. The service
			// is disposed with the container on exit, which stops the poll loop.
			_services.GetRequiredService<UpdateCheckService>().Start();

			// Start the live-sync coordinator (docs/plans/add-api-v2-live-sync): its
			// constructor self-subscribes to storage mutations and sync completions. It does
			// NOT push anything on construction — the reconcile that re-pushes whatever a
			// previous run left unsynced arrives with the SyncCompleted the pull below raises,
			// so the coordinator's tombstone deletes run strictly after the pull's merge.
			// Resolved BEFORE the pull for exactly that reason: the subscription must be in
			// place before the pull can finish.
			_services.GetRequiredService<SyncCoordinator>();

			// A restored "Remember me" session is a sign-in: hydrate the account's cloud data
			// in the background (docs/plans/add-api-v2-pull). After MainWindow resolution for
			// the same reason as above — its PullCompletedWithChanges subscription must be in
			// place before the pull can finish. Returns immediately (the pull runs on the
			// pool), so first paint is never delayed.
			if (hasValidAuth)
				_ = _services.GetRequiredService<ISettingsSyncService>().OnSignedInAsync(savedAuth!.CustomerId);
		}

		base.OnFrameworkInitializationCompleted();
	}

	private void InitializeSingleInstanceActivation(IClassicDesktopStyleApplicationLifetime desktop)
	{
		if (string.IsNullOrWhiteSpace(_singleInstanceActivationEventName))
		{
			return;
		}

		_singleInstanceActivationEvent = new EventWaitHandle(
			initialState: false,
			EventResetMode.AutoReset,
			_singleInstanceActivationEventName);

		_singleInstanceActivationCts = new CancellationTokenSource();
		var cancellationToken = _singleInstanceActivationCts.Token;

		_singleInstanceActivationTask = Task.Run(() =>
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				if (_singleInstanceActivationEvent is null)
				{
					break;
				}

				if (!_singleInstanceActivationEvent.WaitOne(TimeSpan.FromMilliseconds(200)))
				{
					continue;
				}

				if (cancellationToken.IsCancellationRequested)
				{
					break;
				}

				// Same restore as the tray icon's click, routed through RestoreFromTray so all
				// restore paths share one implementation — the inline copy this replaces had
				// drifted (it never reset SessionService.IsInTray and never resumed the glow
				// animation). RestoreFromTray guards _mainWindow being null itself.
				Dispatcher.UIThread.Post(RestoreFromTray);
			}
		}, cancellationToken);
	}

	// The only log destination a shipped build has: AddDebug reaches an attached debugger and
	// nothing else, so every warning the App logged used to vanish on a user's machine
	// (docs/plans/fix-repository-audit-findings, A3). Buffered with a periodic flush, so a log call
	// on the UI thread appends to memory rather than waiting on the disk; disposed with the
	// container on exit (dispose: true), which flushes the tail. No credential is ever logged.
	private static Serilog.ILogger CreateFileLogger() => new Serilog.LoggerConfiguration()
#if DEBUG
		.MinimumLevel.Debug()
#else
		.MinimumLevel.Information()
#endif
		.WriteTo.File(
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProoveGaming", "Logs", "app-.txt"),
			rollingInterval: Serilog.RollingInterval.Day,
			retainedFileCountLimit: 7,
			fileSizeLimitBytes: 10 * 1024 * 1024,
			rollOnFileSizeLimit: true,
			buffered: true,
			flushToDiskInterval: TimeSpan.FromSeconds(1))
		.CreateLogger();

	private static IServiceProvider BuildServices()
	{
		var services = new ServiceCollection();

		services.AddLogging(builder => builder.AddDebug().AddSerilog(CreateFileLogger(), dispose: true));

		services.AddSingleton<ConfigurationManager>();
		services.AddSingleton<SessionService>();
		services.AddSingleton<LocalStorageService>();
		services.AddSingleton<IUsbHidDeviceService, UsbHidDeviceService>();
		// The one HttpClient both API clients share — disposed with the container.
		services.AddSingleton<ApiHttpClient>();
		services.AddSingleton<IAuthApiService, AuthApiService>();
		services.AddSingleton<ISyncApiService, SyncApiService>();
		// Catalog lookup for the sign-in pull's roster hydration — own lazy gRPC channel,
		// disposed with the container (docs/plans/add-api-v21-device-roster).
		services.AddSingleton<IDeviceCatalogResolver, DeviceCatalogResolver>();
		services.AddSingleton<PresetConfigurationBuilder>();
		services.AddSingleton<PresetConfigurationApplier>();

		services.AddSingleton<UserProfile>();
		services.AddSingleton<NotificationService>();
		services.AddSingleton<SystemNotificationService>();
		services.AddSingleton<UpdateCheckService>();

		services.AddSingleton<ICurrentOwnerProvider, CurrentOwnerProvider>();
		services.AddSingleton<ISettingsRepository, SettingsRepository>();
		// Stateless: it resolves, uses and releases its COM objects inside each call, so a singleton
		// holds no endpoint reference between calls.
		services.AddSingleton<IAudioEndpointService, AudioEndpointService>();
		// Keeps the mute icons on the home card and the details bar in sync from one source.
		services.AddSingleton<AudioMuteMonitor>();
		services.AddSingleton<ISettingsSyncService, SettingsSyncService>();
		// Debounced background push of local edits to API v2.0 (docs/plans/add-api-v2-live-sync);
		// self-subscribes to storage / sync events in its constructor, disposed with the container.
		services.AddSingleton<SyncCoordinator>();

		services.AddSingleton<WelcomeViewModel>();
		services.AddSingleton<ChooseNicknameViewModel>();
		services.AddSingleton<HomeViewModel>();
		services.AddSingleton<ProfileViewModel>();
		services.AddSingleton<DeviceDetailsViewModel>();
		services.AddTransient<AddDeviceCardViewModel>();
		services.AddTransient<SingleCardViewModel>();
		services.AddTransient<DoubleCardViewModel>();
		services.AddTransient<InactiveDeviceCardViewModel>();

		AddAuthSessionServices(services);

		services.AddSingleton<MainWindow>();

		return services.BuildServiceProvider();
	}

	// The sign-in/sign-out flow (docs/plans/refactor-auth-session-flow). Each narrow interface
	// forwards to the ViewModel singleton already registered above: AddSingleton<IX, X>() would
	// build a SECOND HomeViewModel/ProfileViewModel/DeviceDetailsViewModel for the coordinator.
	internal static void AddAuthSessionServices(IServiceCollection services)
	{
		services.AddSingleton<IOwnerScopedState>(sp => sp.GetRequiredService<HomeViewModel>());
		services.AddSingleton<IProfileSession>(sp => sp.GetRequiredService<ProfileViewModel>());
		services.AddSingleton<IDeviceDetailsExit>(sp => sp.GetRequiredService<DeviceDetailsViewModel>());
		services.AddSingleton<AuthSessionCoordinator>();
	}

	private void InitializeTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		_openMenuItem = new NativeMenuItem();
		_openMenuItem.Click += (_, _) => RestoreFromTray();

		_exitMenuItem = new NativeMenuItem();
		_exitMenuItem.Click += (_, _) => desktop.Shutdown();

		var menu = new NativeMenu
		{
			_openMenuItem,
			new NativeMenuItemSeparator(),
			_exitMenuItem
		};

		_trayIcon = new TrayIcon
		{
			ToolTipText = LocalizationManager.GetString("Window_Title"),
			Icon = new WindowIcon(
				AssetLoader.Open(new Uri("avares://ProoveGaming.App/Assets/PG_Favicon_128x128.ico"))
			),
			Menu = menu,
			IsVisible = false
		};

		// Registered with the application (Avalonia's TrayIcon.Icons). From Avalonia 12.1.3 a TrayIcon
		// that is only constructed never reaches the notification area, visible or not, so closing to
		// tray left no icon.
		TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
		_trayIcon.Clicked += (_, _) => RestoreFromTray();
		LocalizationManager.LanguageChanged += UpdateTrayLocalization;
		UpdateTrayLocalization();
	}

	private void OnMinimizeToTrayRequested()
	{
		if (_mainWindow is null || _trayIcon is null)
			return;

		_mainWindow.Hide();
		_trayIcon.IsVisible = true;

		if (_services is null) return;
		// Device Details may still be open behind the hidden window (hide-to-tray does not change
		// the view's IsVisible), so stop its 33 ms glow timers (audit C2); RestoreFromTray resumes.
		_services.GetRequiredService<DeviceDetailsViewModel>().PauseGlowAnimation();
		var session = _services.GetRequiredService<SessionService>();
		session.GoToTray(
			allDevicesDisconnected: session.IsGuest &&
				_services.GetRequiredService<IUsbHidDeviceService>().CheckIfAllDevicesAreDisconnected()
		);
	}

	private void RestoreFromTray()
	{
		if (_mainWindow is null)
			return;

		_mainWindow.IsRestoringFromTray = true;
		_mainWindow.Show();
		_mainWindow.WindowState = WindowState.Normal;
		_mainWindow.Activate();
		_mainWindow.IsRestoringFromTray = false;

		if (_trayIcon is not null)
			_trayIcon.IsVisible = false;

		_services?.GetRequiredService<SessionService>().IsInTray = false;
		// The counterpart of OnMinimizeToTrayRequested's glow pause — a no-op unless Device
		// Details is still open with lighting enabled (restoring onto Home is the common case).
		_services?.GetRequiredService<DeviceDetailsViewModel>().ResumeGlowAnimation();
	}

	private void UpdateTrayLocalization()
	{
		if (_trayIcon is not null)
		{
			_trayIcon.ToolTipText = LocalizationManager.GetString("Window_Title");
		}

		if (_openMenuItem is not null)
		{
			_openMenuItem.Header = LocalizationManager.GetString("Tray_Show");
		}

		if (_exitMenuItem is not null)
		{
			_exitMenuItem.Header = LocalizationManager.GetString("Tray_Exit");
		}
	}
}