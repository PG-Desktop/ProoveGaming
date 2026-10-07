using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.Logging;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Views;

public partial class MainWindow : Window
{
	private enum AppStep { Welcome, AgreementTerms, ChooseNickname, Home, Profile, DeviceDetails, Settings, Information }

	private AppStep _currentStep;
	private readonly SessionService _session;
	private readonly LocalStorageService _localStorage;
	private readonly UserProfile _userProfile;
	private readonly WelcomeViewModel _welcomeViewModel;
	private readonly ProfileViewModel _profileViewModel;
	private readonly DeviceDetailsViewModel _deviceDetailsViewModel;
	private readonly AuthSessionCoordinator _auth;

	private sealed record StepLayoutProfile(
		Thickness RootMargin,
		Thickness ContentHostMargin,
		Thickness ContentHostPadding);

	internal bool IsRestoringFromTray;

	public event Action? MinimizeToTrayRequested;

	private readonly NotificationService _notification;
	private readonly HomeViewModel _homeViewModel;
	private readonly ILogger<MainWindow> _logger;

	// Keys currently held during a macro recording — lets us drop KeyDown auto-repeat.
	private readonly System.Collections.Generic.HashSet<Key> _recordingPressedKeys = new();

	// Same idea for the Key Assignment Dialog's type-to-remap capture: holding a key must pick
	// its target once, not once per auto-repeat (each repeat would be another device write on
	// the immediate-apply keyboards).
	private readonly System.Collections.Generic.HashSet<Key> _keyDialogPressedKeys = new();

	public MainWindow(
		SessionService session,
		LocalStorageService localStorage,
		UserProfile userProfile,
		WelcomeViewModel welcomeViewModel,
		HomeViewModel homeViewModel,
		ProfileViewModel profileViewModel,
		ChooseNicknameViewModel chooseNicknameViewModel,
		DeviceDetailsViewModel deviceDetailsViewModel,
		NotificationService notification,
		AuthSessionCoordinator auth,
		ILogger<MainWindow> logger)
	{
		_logger = logger;
		_session = session;
		_localStorage = localStorage;
		_userProfile = userProfile;
		_welcomeViewModel = welcomeViewModel;
		_profileViewModel = profileViewModel;
		_deviceDetailsViewModel = deviceDetailsViewModel;
		_notification = notification;
		_homeViewModel = homeViewModel;
		_auth = auth;
		_session.SessionExpired += OnSessionExpired;
		_profileViewModel.SessionTokenInvalid += OnSessionTokenInvalid;
		_auth.TokenRejected += OnTokenRejected;
		_auth.PullCompletedWithChanges += OnPullCompletedWithChanges;
		_profileViewModel.PropertyChanged += OnProfileViewModelPropertyChanged;
		_homeViewModel.PropertyChanged += OnHomeViewModelPropertyChanged;
		_deviceDetailsViewModel.PropertyChanged += OnDeviceDetailsViewModelPropertyChanged;
		_deviceDetailsViewModel.ExitConfirmedAfterUnsavedChanges += PerformExitDeviceDetails;
		var hasRestoredAuth = !string.IsNullOrEmpty(_userProfile.SessionToken);
		var lockoutUntil = _localStorage.GetLockoutUntil();
		var lockoutActive = lockoutUntil.HasValue && lockoutUntil.Value > DateTime.UtcNow;
		
		_currentStep = hasRestoredAuth || (session.IsGuest && !lockoutActive)
			? AppStep.Home
			: AppStep.Welcome;
		InitializeComponent();
		GetWelcomeView().DataContext = welcomeViewModel;
		GetHomeView().DataContext = homeViewModel;
		GetProfileView().DataContext = profileViewModel;
		GetChooseNicknameView().DataContext = chooseNicknameViewModel;
		GetDeviceDetailsView().DataContext = deviceDetailsViewModel;
		notification.PropertyChanged += OnNotificationPropertyChanged;
		GetNotificationCloseButton().Click += (_, _) => notification.HideCommand.Execute(null);
		GetWelcomeView().GuestContinueRequested += OnGuestContinueRequested;
		GetWelcomeView().SignInSucceeded += OnSignInSucceeded;
		GetWelcomeView().AgreementTermsRequested += OnAgreementTermsRequested;
		GetWelcomeView().SignUpSucceeded += OnSignUpSucceeded;
		GetAgreementTermsView().BackRequested += OnAgreementTermsBack;
		GetChooseNicknameView().BackRequested += OnChooseNicknameBack;
		GetChooseNicknameView().NicknameChosen += OnNicknameChosen;
		GetHomeView().GuestRequested += OnGuestRequested;
		GetHomeView().ProfileRequested += OnProfileRequested;
		GetHomeView().InformationRequested += OnInformationRequested;
		GetHomeView().SettingsRequested += OnSettingsRequested;
		GetHomeView().LogoutRequested += OnLogoutRequested;
		GetHomeView().DeviceDetailsRequested += OnDeviceDetailsRequested;
		GetProfileView().BackRequested += OnProfileBack;
		GetInformationView().BackRequested += OnInformationBack;
		GetSettingsView().BackRequested += OnSettingsBack;
		GetDeviceDetailsView().BackRequested += OnDeviceDetailsBack;

		// Capture keystrokes for the macro recorder. Tunnel + handledEventsToo so we see keys
		// before any focused control (e.g. the macro-name TextBox) consumes them; we only act
		// while a recording is active and swallow the key so it doesn't also type into the UI.
		AddHandler(KeyDownEvent, OnRecorderKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
		AddHandler(KeyUpEvent, OnRecorderKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);

		// Type-to-remap for the Key Assignment Dialog: while its KEYS tab is up, a key pressed
		// on the physical keyboard picks the remap target, same as clicking it on the picker.
		// Tunneled for the recorder's reason — the press must not also reach a focused control.
		AddHandler(KeyDownEvent, OnKeyDialogKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
		AddHandler(KeyUpEvent, OnKeyDialogKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);

		// A key still held while the window loses focus (Alt+Tab, Win, a UAC prompt) never delivers
		// its KeyUp here, and the stale entry would make the Contains-first guard above swallow that
		// key's next make ANYWHERE in the app — one silently eaten character in a password field.
		// Focus loss releases every key as far as this window can observe, so drop the whole set;
		// the recorder's twin already self-heals the same way (OnRecorderKeyUp clears when idle).
		Deactivated += (_, _) => _keyDialogPressedKeys.Clear();

		// Same idea for mouse-button clicks (device codes F0–F4). While recording, every click
		// is captured and swallowed EXCEPT on the record button itself, so STOP still works.
		AddHandler(PointerPressedEvent, OnRecorderPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
		AddHandler(PointerReleasedEvent, OnRecorderPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

		UpdateStepUi();

		_homeViewModel.AvatarImage = _profileViewModel.AvatarImage;

		if (hasRestoredAuth)
			_auth.ResumeRestoredSession();
	}

	// Opened is raised on every Show(), not only the first — and closing this app HIDES the window
	// to tray by default (AppSettings.MinimizeToTrayEnabled), so clicking the tray icon runs this
	// again. Anything here therefore has to be either idempotent or explicitly once-only.
	//
	// Once-only: centring the window and subscribing to PositionChanged. Re-centring on every tray
	// restore would throw away the position the user chose, and re-subscribing would leave one extra
	// handler per restore, each re-querying the screen on every move.
	//
	// Every time: the frame attributes (harmless to reapply, and useful if the handle was recreated)
	// and the fit/scale, which only ever shrink and are worth redoing in case the display changed
	// while the window sat in the tray.
	protected override void OnOpened(EventArgs e)
	{
		base.OnOpened(e);

		ApplyWindowFrameAppearance();
		FitToScreen();
		ApplyRootScale();

		if (_openedOnce) return;
		_openedOnce = true;

		CenterOnCurrentScreen();
		PositionChanged += OnWindowPositionChanged;
		ScalingChanged += (_, _) => QueueRefitToScreen();
		// A resolution or taskbar change never moves the window and never changes its scaling, so
		// neither signal above covers it - this is the platform saying the display layout itself was
		// reconfigured.
		Screens.Changed += (_, _) => QueueRefitToScreen();
	}

	private bool _openedOnce;

	// The size the layout is built for. Below this the UI is scaled down rather than clipped:
	// DeviceDetailsView alone pins 26 blocks to a fixed height and the cards position icons by
	// absolute Canvas coordinates, so there is nothing to reflow — shrinking has to be uniform.
	private const double DesignWidth = 1200;
	private const double DesignHeight = 880;

	// Below this the UI stops being readable, so it clips instead of shrinking further.
	private const double MinimumRootScale = 0.5;

	// Never open bigger than the screen can show.
	//
	// The window asks for 1440x900 with a 1200x880 minimum, and a minimum is a floor the user cannot
	// drag below — so on a screen with less room than that, the window overflowed and the title bar
	// (with minimise and close) sat off-screen with no way to bring it back. Display scaling is the
	// common way to hit this rather than a genuinely small monitor: at 200 % a 1920x1080 screen is
	// only 960x540 of layout space, well under the minimum.
	//
	// WorkingArea is in physical pixels and everything above is logical, so it is converted by the
	// screen's own scaling before being compared to anything. Getting that wrong would "fix" the
	// window to twice the size of the screen on exactly the machines this is for.
	private void FitToScreen()
	{
		var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
		if (screen is null) return;

		// Remember the room either way, so the first refit after this compares against what was
		// actually on screen — but leave a non-normal window alone. See QueueRefitToScreen: sizing
		// a maximised window rewrites its restore rectangle instead of its current size.
		RememberScreenRoom(screen);
		if (WindowState != WindowState.Normal) return;

		ShrinkToFit(screen);
	}

	// What the window had to live in last time it was fitted. Compared by GEOMETRY, not by Screen
	// identity: the case that matters most is the same monitor changing under the window, and there
	// the Screen is trivially "the same" however you compare the objects.
	private PixelRect _lastWorkingArea;
	private double _lastScaling;

	private void RememberScreenRoom(Screen screen)
	{
		_lastWorkingArea = screen.WorkingArea;
		_lastScaling = screen.Scaling;
	}

	// Startup only. A window shrunk from its centred position would otherwise sit part-way off the
	// edge it was centred against — but once the user has placed it, moving it is never ours to do.
	private void CenterOnCurrentScreen()
	{
		var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
		if (screen is null) return;

		var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

		// Position places the OUTER window, so the frame counts here too — centring the client area
		// pushes the right and bottom borders past the working area by half the frame each.
		var overhead = FrameOverhead;
		var outerWidth = (Width + overhead.Width) * scaling;
		var outerHeight = (Height + overhead.Height) * scaling;

		Position = new PixelPoint(
			screen.WorkingArea.X + (int)Math.Max(0, (screen.WorkingArea.Width - outerWidth) / 2),
			screen.WorkingArea.Y + (int)Math.Max(0, (screen.WorkingArea.Height - outerHeight) / 2));
	}

	/// <summary>Clamps the window to what a screen can show. Returns that screen's scaling.</summary>
	private double ShrinkToFit(Screen screen)
	{
		// WorkingArea is in PHYSICAL pixels while Width/Height/MinWidth are logical, so it is
		// converted before any comparison. Getting this backwards would size the window to twice the
		// screen on exactly the high-DPI machines it exists for.
		var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
		var available = ResolveUsableClientSize(
			new Size(screen.WorkingArea.Width / scaling, screen.WorkingArea.Height / scaling),
			FrameOverhead);

		// The floor has to come down first: MinWidth/MinHeight would otherwise stop the window
		// shrinking to fit. The layout survives it because the root scales (ApplyRootScale).
		//
		// It comes down to the READABLE floor, not to the working area. Setting it to the working
		// area looks right and does not work: the platform enforces a window's minimum using a
		// larger frame than this window actually has — measured at 150 % on Windows 11, 22 physical
		// px against a real 12 — so a minimum equal to the target size blocks the window from ever
		// reaching that size, by exactly that difference. The symptom was the last 10 physical px
		// of the app staying under the taskbar however often the fit re-ran (PG-683). A minimum is
		// only there to stop the window being dragged into illegibility, and the size below which
		// this UI stops being readable is already named: the root-scale floor.
		MinWidth = ResolveMinimumExtent(MinWidth, available.Width, DesignWidth);
		MinHeight = ResolveMinimumExtent(MinHeight, available.Height, DesignHeight);

		Width = Math.Min(Width, available.Width);
		Height = Math.Min(Height, available.Height);

		return scaling;
	}

	/// <summary>
	/// The minimum one dimension may be dragged to, given what the screen can currently show.
	/// </summary>
	/// <remarks>
	/// Pinned by a test because both ways of getting it wrong are silent. Leave the minimum at the
	/// design size and a window on a smaller screen cannot shrink to fit it — it hangs off the
	/// edge. Set it to what the screen offers and the window cannot reach even that: the platform
	/// enforces a minimum using a larger frame than this window actually has, so the last few
	/// pixels stay under the taskbar (PR #142 m4 / PG-683).
	///
	/// So it only ever comes DOWN, and it comes down to the readable floor — the size at which the
	/// root scale bottoms out and the UI stops being legible — rather than to the screen. Keeping
	/// the design minimum wherever it still fits is what leaves roomy screens exactly as they were.
	/// </remarks>
	internal static double ResolveMinimumExtent(double current, double available, double design) =>
		available >= current ? current : Math.Min(available, design * MinimumRootScale);

	/// <summary>
	/// The chrome the OS draws around the client area, logical units — what <see cref="FrameSize"/>
	/// adds on top of <see cref="Layoutable.Width"/> / <see cref="Layoutable.Height"/>. Zero before
	/// the platform window exists, which leaves every caller at the unadjusted size it used before.
	/// </summary>
	private Size FrameOverhead
	{
		get
		{
			if (FrameSize is not { } frame) return default;

			var client = Bounds.Size.Width > 0 && Bounds.Size.Height > 0
				? Bounds.Size
				: new Size(Width, Height);
			if (client.Width <= 0 || client.Height <= 0) return default;

			return new Size(
				Math.Max(0, frame.Width - client.Width),
				Math.Max(0, frame.Height - client.Height));
		}
	}

	/// <summary>
	/// How much CLIENT area fits inside a working area once the OS frame is paid for.
	/// </summary>
	/// <remarks>
	/// Separated from the control so the rule can be pinned by a test, because getting it wrong is
	/// invisible until someone runs the app on a small screen. Width/Height on a Window are the
	/// CLIENT size — `WindowDecorations="BorderOnly"` keeps a real OS frame around it (that frame is
	/// what makes the window resizable), and on Windows 11 it measures 16x9 logical pixels. Clamping
	/// the client size straight to the working area therefore produced a window 16 px too wide and
	/// 9 px too tall for the screen: the right border went off the edge and the bottom slid under
	/// the taskbar, taking the resize grips with them. That is the "app is cut off on every edge"
	/// QA reported at 1280x720 (PG-683), and display scaling reaches it on a 1920x1080 screen too —
	/// 150 % leaves exactly 1280x688 of logical room.
	///
	/// Floored at 1 rather than 0: a degenerate working area must not hand back a negative size that
	/// would then be assigned to MinWidth.
	/// </remarks>
	internal static Size ResolveUsableClientSize(Size workingArea, Size frameOverhead) =>
		new(Math.Max(1, workingArea.Width - frameOverhead.Width),
			Math.Max(1, workingArea.Height - frameOverhead.Height));

	// Resizing has to re-evaluate the scale, not just startup — this is the whole point of making
	// the window resizable in the first place.
	protected override void OnSizeChanged(SizeChangedEventArgs e)
	{
		base.OnSizeChanged(e);
		ApplyRootScale();
	}

	// The room a window has to live in can shrink under it, which reintroduces the very problem
	// FitToScreen solves: the title bar — with minimise and close — ends up off-screen, and
	// startup-only fitting would leave it that way until the next launch. Two ways in:
	//
	//   - the window MOVES to another monitor, one that is smaller or more heavily scaled;
	//   - the monitor changes under a window that never moved. Raising the display scale in Windows
	//     Settings is the everyday case, and the one QA hit (PG-683): at 150 % a 1920x1080 screen
	//     goes from 1920x1032 of logical room to 1280x688 while the window still asks for its
	//     1440x900, so 200 px of it drop below the taskbar. A resolution change and a taskbar resize
	//     do the same thing.
	//
	// Both are the same event — "the room changed" — so every signal runs through here.
	//
	// POSTED, not run inline, and this is the whole reason the first attempt at this did nothing:
	// inside the notification for a display change the screen still reports its OLD geometry. Set
	// 1920x1080 to 1280x720 with the app open and the handler reads 1920x1032 of working area,
	// finds the window already fits, and leaves it hanging 220 px below the taskbar — while the
	// very same call a moment later (nudging the window was how this was caught) shrinks it
	// correctly. Deferring to Background priority lets the platform finish the transition and
	// refresh what it reports before anything is measured.
	//
	// Coalesced, because a drag raises PositionChanged per step and one refit per idle turn is
	// enough.
	//
	// Shrink-only, always. Growing the window back when the screen gets roomier again is not ours
	// to decide: the user may have sized it small deliberately since.
	private bool _refitQueued;

	private void QueueRefitToScreen()
	{
		if (_refitQueued) return;
		_refitQueued = true;

		Dispatcher.UIThread.Post(() =>
		{
			_refitQueued = false;

			// Only a NORMAL window has a size of its own to fit. A maximised one is sized by the
			// OS to exactly the working area, and its GetWindowRect is larger than that by the
			// frame — so ShrinkToFit reads "does not fit" and shrinks it. Avalonia hands that to
			// Win32 WindowImpl.Resize, which for a maximised window rewrites the RESTORE rectangle
			// instead of the current size: the window looks fine until you press restore, and then
			// comes back at nearly screen size with its right edge and the close button off the
			// screen. Which is the very bug this whole path exists to prevent (PR #142 M1).
			if (WindowState != WindowState.Normal) return;

			var screen = Screens.ScreenFromWindow(this);
			if (screen is null) return;

			// And only when the ROOM actually changed. ShrinkToFit is shrink-only, but it is not
			// free of side effects — it lowers MinWidth/MinHeight — so running it on every step of
			// every drag is both wasted work and a way to weaken the floor for no reason. The test
			// is the working area and the scaling, never Screen identity: the case that matters
			// most is the same monitor changing under a window that never moved, and there the
			// Screen is trivially "the same" however the objects are compared. Reading it here,
			// inside the posted callback, is what makes it trustworthy — inline it would still be
			// the pre-change geometry.
			if (screen.WorkingArea == _lastWorkingArea && Math.Abs(screen.Scaling - _lastScaling) < 0.001)
				return;

			RememberScreenRoom(screen);
			ShrinkToFit(screen);
			ApplyRootScale();
		}, DispatcherPriority.Background);
	}

	private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e) =>
		QueueRefitToScreen();

	/// <summary>The uniform scale for a window of this size, never above 1 and never below the floor.</summary>
	/// <remarks>
	/// Separated from the control so the rule can be pinned by a test: it decides whether the app is
	/// usable at 150-200 % display scaling, and every failure mode is silent — too small and the UI
	/// is illegible, above 1 and a large window would blow the layout up past its design.
	///
	/// Never scales UP. Above the design size the layout stretches on its own, the way it always
	/// has; this exists only for windows that cannot fit it.
	/// </remarks>
	internal static double ResolveRootScale(double width, double height)
	{
		if (width <= 0 || height <= 0) return 1.0;

		var scale = Math.Min(1.0, Math.Min(width / DesignWidth, height / DesignHeight));

		// Uniform, so nothing distorts, and floored so a pathologically small window cannot scale
		// the UI into illegibility.
		return Math.Max(MinimumRootScale, scale);
	}

	// Scales the whole UI down when the window is smaller than the layout needs, and never up.
	private void ApplyRootScale()
	{
		if (RootScaler is null) return;

		var width = Bounds.Width > 0 ? Bounds.Width : Width;
		var height = Bounds.Height > 0 ? Bounds.Height : Height;
		if (width <= 0 || height <= 0) return;

		var scale = ResolveRootScale(width, height);

		if (RootScaler.LayoutTransform is ScaleTransform current &&
			Math.Abs(current.ScaleX - scale) < 0.001)
		{
			return;
		}

		RootScaler.LayoutTransform = new ScaleTransform(scale, scale);
	}

	// Squares off the OS frame and hides its border.
	//
	// Windows 11 both rounds a window's frame and draws a line around it, through DWM, and a
	// square-cornered borderless design has to opt out of each explicitly. CornerRadius="0" on the
	// Window does not do it: that is Avalonia drawing its own content, while both effects belong to
	// the OS frame around it.
	//
	// It only started showing when WindowDecorations moved from None to BorderOnly to restore
	// resizing — None had no OS frame to round. The two go together: keeping the frame is what makes
	// the window resizable, and this is what keeps that frame square.
	//
	// Silent no-op anywhere it does not apply. The attribute is Windows 11 22000+; Windows 10 fails
	// the call with E_INVALIDARG and rounds nothing anyway, which is the result we want regardless.
	private void ApplyWindowFrameAppearance()
	{
		if (!OperatingSystem.IsWindows()) return;

		try
		{
			if (TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;

			var preference = DwmwcpDoNotRound;
			DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));

			// And hide the frame's own border. BorderOnly keeps a real OS frame — that is what makes
			// the window resizable — but Windows draws a visible line around it, which reads as a
			// grey outline against the app's dark chrome. DWMWA_COLOR_NONE removes the line while
			// leaving the frame (and therefore the resize grips) in place.
			var borderColour = DwmwaColorNone;
			DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColour, sizeof(int));

			// And the caption strip. BorderOnly still reserves one, and DWMWA_COLOR_NONE is only
			// documented for the BORDER, so the caption is painted to match the artwork behind it
			// instead of hidden — it showed as a white line across the top of an otherwise dark
			// window. The background is an image whose top edge runs #1D0F32 to #0A0A22 left to
			// right, so a single flat colour cannot match it exactly; this sits in the middle of
			// that gradient, where the strip stops being distinguishable.
			var captionColour = ColorRef(0x15, 0x0B, 0x2B);
			DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColour, sizeof(int));
		}
		catch (DllNotFoundException)
		{
			// No dwmapi.dll — nothing to square off.
		}
		catch (EntryPointNotFoundException)
		{
			// Older shell without the attribute; it does not round either.
		}
	}

	private const int DwmwaWindowCornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
	private const int DwmwcpDoNotRound = 1;               // DWMWCP_DONOTROUND
	private const int DwmwaBorderColor = 34;              // DWMWA_BORDER_COLOR
	private const int DwmwaCaptionColor = 35;            // DWMWA_CAPTION_COLOR
	private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE

	// COLORREF is 0x00BBGGRR — blue in the HIGH byte, the reverse of the #RRGGBB the design uses.
	// Passing a hex colour straight through silently swaps red and blue, which looks like a colour
	// choice rather than a bug, so the conversion lives here and is done once.
	private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

	[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(
		IntPtr hwnd, int attribute, ref int value, int valueSize);

	// Account deleted, or the server rejected the token. Either way the user is signed out and
	// lands on Welcome, so reset it the same way logout does: empty sign-in/sign-up fields on the
	// Sign In tab. Without PrepareForFreshSignIn the deleted account's email stayed in the form.
	// No "session expired" notification here: this path also fires on a successful account
	// deletion, where the sign-out is expected — the notification rides OnTokenRejected only.
	private void OnSessionTokenInvalid()
	{
		Avalonia.Threading.Dispatcher.UIThread.Post(() => SignOutToFreshSignIn(notifySessionExpired: false));
	}

	// Global token-rejection path (AuthSessionCoordinator.TokenRejected, raised by the shared HTTP
	// client): any token-bearing request —
	// profile, sync push/pull — answered TOKEN_REQUIRED / INVALID_OR_EXPIRED_TOKEN. The server
	// wiped the session (30-day expiry or a password change on the web), so unlike the profile
	// path above the sign-out is unprompted — tell the user why.
	private void OnTokenRejected(string rejectedToken)
	{
		Avalonia.Threading.Dispatcher.UIThread.Post(() =>
		{
			// Compared here so the SessionToken read is UI-thread-affine; a stale rejection must
			// not sign the user out of a session the server never rejected.
			if (!_auth.IsCurrentToken(rejectedToken))
				return;
			SignOutToFreshSignIn(notifySessionExpired: true);
		});
	}

	// A sign-in pull (docs/plans/add-api-v2-pull) landed at least one cloud record in the local
	// database. Raised on a pool thread; ReloadOwnerScopedState touches the card collection and
	// the bell, so marshal. Owner-aware by itself: if the user signed out while the pull was in
	// flight, the reload sees no user owner and only refreshes the (now empty) bell.
	private void OnPullCompletedWithChanges()
	{
		Avalonia.Threading.Dispatcher.UIThread.Post(_homeViewModel.ReloadOwnerScopedState);
	}

	// The coordinator's sign-out is idempotent (the profile path and the global path can both
	// observe the same dead token); when it had nothing to do, the Sign In form the user may already
	// be typing into must be left alone too.
	private void SignOutToFreshSignIn(bool notifySessionExpired)
	{
		if (_auth.SignOut(notifySessionExpired, onWelcomeScreen: _currentStep == AppStep.Welcome) == SignOutOutcome.Nothing)
			return;

		_welcomeViewModel.PrepareForFreshSignIn();
		_currentStep = AppStep.Welcome;
		UpdateStepUi();

		if (notifySessionExpired)
			_notification.Show(LocalizationManager.GetString("Session_ExpiredToast"));
	}

	private async void OnLogoutRequested()
	{
		await _auth.LogoutAsync();
		_welcomeViewModel.PrepareForFreshSignIn();
		_currentStep = AppStep.Welcome;
		UpdateStepUi();
	}

	protected override void OnClosed(EventArgs e)
	{
		GetWelcomeView().GuestContinueRequested -= OnGuestContinueRequested;
		GetWelcomeView().SignInSucceeded -= OnSignInSucceeded;
		GetWelcomeView().AgreementTermsRequested -= OnAgreementTermsRequested;
		GetWelcomeView().SignUpSucceeded -= OnSignUpSucceeded;
		GetAgreementTermsView().BackRequested -= OnAgreementTermsBack;
		GetChooseNicknameView().BackRequested -= OnChooseNicknameBack;
		GetChooseNicknameView().NicknameChosen -= OnNicknameChosen;
		GetHomeView().GuestRequested -= OnGuestRequested;
		GetHomeView().ProfileRequested -= OnProfileRequested;
		GetHomeView().InformationRequested -= OnInformationRequested;
		GetHomeView().SettingsRequested -= OnSettingsRequested;
		GetHomeView().LogoutRequested -= OnLogoutRequested;
		GetHomeView().DeviceDetailsRequested -= OnDeviceDetailsRequested;
		_session.SessionExpired -= OnSessionExpired;
		_profileViewModel.SessionTokenInvalid -= OnSessionTokenInvalid;
		_auth.TokenRejected -= OnTokenRejected;
		_auth.PullCompletedWithChanges -= OnPullCompletedWithChanges;
		_profileViewModel.PropertyChanged -= OnProfileViewModelPropertyChanged;
		GetProfileView().BackRequested -= OnProfileBack;
		GetInformationView().BackRequested -= OnInformationBack;
		GetSettingsView().BackRequested -= OnSettingsBack;
		GetDeviceDetailsView().BackRequested -= OnDeviceDetailsBack;
		_notification.PropertyChanged -= OnNotificationPropertyChanged;
		_homeViewModel.PropertyChanged -= OnHomeViewModelPropertyChanged;
		_deviceDetailsViewModel.PropertyChanged -= OnDeviceDetailsViewModelPropertyChanged;
		_deviceDetailsViewModel.ExitConfirmedAfterUnsavedChanges -= PerformExitDeviceDetails;
		base.OnClosed(e);
	}

	private void OnHomeViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(HomeViewModel.IsLogoutConfirmOpen)
			&& e.PropertyName != nameof(HomeViewModel.IsContactUsOpen)
			&& e.PropertyName != nameof(HomeViewModel.PendingDeleteCard))
			return;

		RefreshDialogOverlay();
	}

	private void OnDeviceDetailsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(DeviceDetailsViewModel.IsCreateMacroOpen)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.PendingDeleteMacro)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.IsNewPresetOpen)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.IsResetPresetOpen)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.IsDeletePresetOpen)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.PendingDeleteDpiPoint)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.IsResetExtendedKeysOpen)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.IsSavePresetOpen)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.IsUnsavedChangesOpen)
			&& e.PropertyName != nameof(DeviceDetailsViewModel.IsKeyAssignmentDialogOpen))
			return;

		RefreshDialogOverlay();
	}

	private void OnProfileViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(ProfileViewModel.IsEditNicknameOpen):
				RefreshDialogOverlay();
				break;
			case nameof(ProfileViewModel.IsEditEmailOpen):
				RefreshDialogOverlay();
				break;
			case nameof(ProfileViewModel.IsDeleteAccountOpen):
				RefreshDialogOverlay();
				break;
			case nameof(ProfileViewModel.IsChangePasswordOpen):
				RefreshDialogOverlay();
				break;
			case nameof(ProfileViewModel.AvatarImage):
				_homeViewModel.AvatarImage = _profileViewModel.AvatarImage;
				break;
		}
	}

	private void RefreshDialogOverlay()
	{
		var isLogout = _homeViewModel.IsLogoutConfirmOpen;
		var isContactUs = _homeViewModel.IsContactUsOpen;
		var isDeleting = _homeViewModel.PendingDeleteCard != null;
		var isCreateMacro = _deviceDetailsViewModel.IsCreateMacroOpen;
		var isDeleteMacro = _deviceDetailsViewModel.PendingDeleteMacro != null;
		var isNewPreset = _deviceDetailsViewModel.IsNewPresetOpen;
		var isResetPreset = _deviceDetailsViewModel.IsResetPresetOpen;
		var isDeletePreset = _deviceDetailsViewModel.IsDeletePresetOpen;
		var isDeleteDpiPoint = _deviceDetailsViewModel.PendingDeleteDpiPoint != null;
		var isResetExtendedKeys = _deviceDetailsViewModel.IsResetExtendedKeysOpen;
		var isSavePreset = _deviceDetailsViewModel.IsSavePresetOpen;
		var isUnsavedChanges = _deviceDetailsViewModel.IsUnsavedChangesOpen;
		var isKeyAssignment = _deviceDetailsViewModel.IsKeyAssignmentDialogOpen;
		var isNicknamePopup = _profileViewModel.IsEditNicknameOpen;
		var isEmailPopup = _profileViewModel.IsEditEmailOpen;
		var isDeleteAccountPopup = _profileViewModel.IsDeleteAccountOpen;
		var isChangePasswordPopup = _profileViewModel.IsChangePasswordOpen;
		var dialogOpen = isLogout || isContactUs || isDeleting || isCreateMacro || isDeleteMacro || isNewPreset || isResetPreset || isDeletePreset || isDeleteDpiPoint || isResetExtendedKeys || isSavePreset || isUnsavedChanges || isKeyAssignment || isNicknamePopup || isEmailPopup || isDeleteAccountPopup || isChangePasswordPopup;

		GetBackgroundLayoutHost().Effect = dialogOpen ? new BlurEffect { Radius = 12 } : null;
		GetDialogOverlay().IsVisible = isLogout || isContactUs || isDeleting || isCreateMacro || isDeleteMacro || isNewPreset || isResetPreset || isDeletePreset || isDeleteDpiPoint || isResetExtendedKeys || isSavePreset || isUnsavedChanges || isKeyAssignment;
		GetLogoutDialogPanel().IsVisible = isLogout;
		GetContactUsDialogPanel().IsVisible = isContactUs;
		GetDeleteDialogPanel().IsVisible = isDeleting;
		GetCreateMacroDialogPanel().IsVisible = isCreateMacro;
		GetDeleteMacroDialogPanel().IsVisible = isDeleteMacro;
		GetNewPresetDialogPanel().IsVisible = isNewPreset;
		GetResetPresetDialogPanel().IsVisible = isResetPreset;
		GetDeletePresetDialogPanel().IsVisible = isDeletePreset;
		GetDeleteDpiPointDialogPanel().IsVisible = isDeleteDpiPoint;
		GetResetExtendedKeysDialogPanel().IsVisible = isResetExtendedKeys;
		GetSavePresetDialogPanel().IsVisible = isSavePreset;
		GetUnsavedChangesDialogPanel().IsVisible = isUnsavedChanges;
		GetKeyAssignmentDialogPanel().IsVisible = isKeyAssignment;
	}

	private void OnNotificationPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(NotificationService.IsVisible))
			GetNotificationGrid().IsVisible = _notification.IsVisible;
		else if (e.PropertyName == nameof(NotificationService.Text))
			GetNotificationText().Text = _notification.Text;
	}

	private void OnGuestContinueRequested()
	{
		_auth.StartGuestSession();
		_currentStep = AppStep.Home;
		UpdateStepUi();
	}

	private void OnSessionExpired()
	{
		if (_auth.HandleGuestSessionExpired() == GuestExpiryOutcome.StayWhereYouAre)
			return;

		GetWelcomeView().ShowSignUpTab();
		_currentStep = AppStep.Welcome;
		UpdateStepUi();
	}

	private async void OnSignInSucceeded()
	{
		await _auth.CompleteSignInStateAsync(_welcomeViewModel.IsRememberMeChecked);
		_currentStep = AppStep.Home;
		UpdateStepUi();
		// After the navigation, so the profile request never delays the Home screen.
		await _auth.FinishSignInAsync();
	}

	private void OnAgreementTermsRequested(LegalDocument document)
	{
		GetAgreementTermsView().ShowDocument(document);
		_currentStep = AppStep.AgreementTerms;
		UpdateStepUi();
	}

	private void OnAgreementTermsBack()
	{
		_currentStep = AppStep.Welcome;
		UpdateStepUi();
	}

	private void OnSignUpSucceeded(IReadOnlyList<string> nicknames)
	{
		if (GetChooseNicknameView().DataContext is ChooseNicknameViewModel cvm)
			cvm.SetSuggestionsFromServer(nicknames);
		_currentStep = AppStep.ChooseNickname;
		UpdateStepUi();
	}

	private void OnChooseNicknameBack()
	{
		_currentStep = AppStep.Welcome;
		UpdateStepUi();
	}

	private async void OnNicknameChosen(string nickname)
	{
		var result = await _auth.RegisterAsync(
			_welcomeViewModel.SignUpEmail,
			_welcomeViewModel.SignUpPassword,
			nickname,
			agreeTerms: _welcomeViewModel.IsTermsChecked,
			agreeNewsletter: _welcomeViewModel.IsMarketingChecked);

		if (!result.Succeeded)
		{
			// Nickname errors → highlight the field in place, no toast (the inline red box is enough).
			if (result.NicknameError != null
				&& GetChooseNicknameView().DataContext is ChooseNicknameViewModel cvm)
			{
				cvm.ApplyNicknameError(result.NicknameError);
				return;
			}

			_notification.Show(result.ToastText);
			return;
		}

		_currentStep = AppStep.Home;
		UpdateStepUi();
		// After the navigation, as for sign-in.
		await _auth.FinishRegistrationAsync();
	}

	private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
	{
		if (AppSettings.MinimizeToTrayEnabled)
		{
			MinimizeToTrayRequested?.Invoke();
			return;
		}

		Close();
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

	private void OnNotificationsOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		var panel = GetNotificationsPanel();
		if (e.Source is Visual source && (source == panel || panel.IsVisualAncestorOf(source)))
			return;

		if (GetHomeView().DataContext is HomeViewModel vm)
			vm.CloseNotificationsCommand.Execute(null);
	}

	private void OnGuestRequested()
	{
		GetWelcomeView().ShowSignUpTab();
		_currentStep = AppStep.Welcome;
		UpdateStepUi();
	}

	private async void OnProfileRequested()
	{
		_currentStep = AppStep.Profile;
		UpdateStepUi();
		// Probe the token — if the user changed their password on the web (v1.7 §11) the
		// server has wiped their Session Token, and GetProfileAsync will bounce them to Welcome.
		await _profileViewModel.LoadFromServerAsync();
	}

	private void OnProfileBack()
	{
		_currentStep = AppStep.Home;
		UpdateStepUi();
	}

	private void OnSettingsRequested()
	{
		_currentStep = AppStep.Settings;
		UpdateStepUi();
	}

	private void OnInformationRequested()
	{
		GetInformationView().ResetToFirstSection();
		_currentStep = AppStep.Information;
		UpdateStepUi();
	}

	private void OnInformationBack()
	{
		_currentStep = AppStep.Home;
		UpdateStepUi();
	}

	private void OnSettingsBack()
	{
		_currentStep = AppStep.Home;
		UpdateStepUi();
	}

	// DeviceDetailsView is never destroyed — only IsVisible-toggled. The device-only glow bitmap is
	// cleared on exit (PerformExitDeviceDetails) and re-populated here from HomeViewModel's prewarmed
	// cache (see DeviceDetailsViewModel.PrewarmDeviceOnlyBitmapAsync) — by the time the user can click
	// a device card, its image was already rasterized when the card first appeared in the list, so
	// SetDeviceAsync picks up a synchronously-ready bitmap with nothing stale left to paint.
	//
	// The view is shown BEFORE the load task is awaited: SetDeviceAsync's synchronous prologue has
	// already set Device (name, image, nav) by the time the call returns its task, and its storage
	// work runs off the UI thread — so navigation renders on the next frame instead of stalling
	// until every SQLite read completes.
	private async void OnDeviceDetailsRequested(DeviceCardViewModel device)
	{
		var load = SetDeviceAndLogAsync(device);

		// Flip the view once the visible-critical part of the open (RGB + presets) has applied,
		// so the FIRST rendered frame carries the preset list and Current-preset selection — on a
		// keyboard the page's heavy initial layout otherwise wedges itself between the flip and
		// the presets landing, and the list visibly pops in late. Typically a few tens of ms; the
		// timeout guarantees a stuck or failing load can never pin the user to the Home view.
		await Task.WhenAny(_deviceDetailsViewModel.OpenCoreLoaded, Task.Delay(300));

		_currentStep = AppStep.DeviceDetails;
		UpdateStepUi();
		_ = load;
	}

	// Observes the load so a storage failure mid-open (locked/corrupt DB, full disk) is logged
	// instead of either vanishing as an unobserved task or — the async-void alternative —
	// crashing the app on the dispatcher after the page has already rendered. The page shows
	// whatever loaded; the log names what didn't.
	private async Task SetDeviceAndLogAsync(DeviceCardViewModel device)
	{
		try { await _deviceDetailsViewModel.SetDeviceAsync(device); }
		catch (Exception ex)
		{
			_logger.LogError(ex, "Loading Device Details for {Device} failed", device.Name);
		}
	}

	private void OnDeviceDetailsBack()
	{
		// Only prompt when the user could actually save. On a transport that cannot carry
		// configuration the Save Preset button is disabled, so the dialog would offer a dead choice.
		if (_deviceDetailsViewModel.HasPromptableUnsavedChanges)
		{
			_deviceDetailsViewModel.OpenUnsavedChangesForLeaving();
			return;
		}
		PerformExitDeviceDetails();
	}

	private void PerformExitDeviceDetails()
	{
		// Drop the device-only glow bitmap AND null out Device here, on the way OUT, not when the
		// next device's SetDevice runs. Clearing the bitmap alone wasn't enough: Device stayed
		// pointing at the device just left, so the next SetDevice moved DeviceDetailImagePath
		// straight from the old path to the new one with no cleared state in between — and the
		// bound Svg/Image controls (never destroyed, only IsVisible-toggled) kept whatever picture
		// they last painted until their own load of the new path finished, which is what actually
		// showed as "old device, brief flash, new device". Routing through null first forces a
		// genuine clear before the next device loads. See ClearDeviceForExit's own comment.
		_deviceDetailsViewModel.ClearDeviceForExit();
		_currentStep = AppStep.Home;
		UpdateStepUi();
	}

	private void OnRecorderKeyDown(object? sender, KeyEventArgs e)
	{
		if (!_deviceDetailsViewModel.IsRecording)
			return;

		// Ignore auto-repeat: only the first make per physical key until it is released.
		if (!_recordingPressedKeys.Add(e.Key))
		{
			e.Handled = true;
			return;
		}

		if (HidKeyCodeMap.TryMap(e.Key, out var code, out var label))
			_deviceDetailsViewModel.CaptureKeyEvent(code, isPress: true, label);

		e.Handled = true;
	}

	private void OnRecorderKeyUp(object? sender, KeyEventArgs e)
	{
		if (!_deviceDetailsViewModel.IsRecording)
		{
			_recordingPressedKeys.Clear();
			return;
		}

		_recordingPressedKeys.Remove(e.Key);
		if (HidKeyCodeMap.TryMap(e.Key, out var code, out var label))
			_deviceDetailsViewModel.CaptureKeyEvent(code, isPress: false, label);

		e.Handled = true;
	}

	private void OnKeyDialogKeyDown(object? sender, KeyEventArgs e)
	{
		// The recorder owns the keyboard while a recording runs; the two dialogs cannot be up
		// at once, but the guard costs nothing and makes the ownership explicit.
		if (_deviceDetailsViewModel.IsRecording)
			return;

		// A press this handler already saw stays swallowed until it is released — checked BEFORE
		// the dialog-open gate, because a successful pick closes the dialog while the picked key
		// is still physically down. Its auto-repeat would otherwise bubble to whatever kept
		// focus: the key tile that opened the dialog, which Space keydown+keyup "click",
		// reopening the dialog right after the remap. Hit most on the 2.4G transports, whose
		// slow chunked write leaves the dialog closed long enough for repeats to start.
		if (_keyDialogPressedKeys.Contains(e.Key))
		{
			e.Handled = true;
			return;
		}

		if (!_deviceDetailsViewModel.IsKeyAssignmentDialogOpen
			|| !_deviceDetailsViewModel.IsKeyDialogKeysTabActive)
			return;

		// Only the first make per physical key acts; repeats are eaten above until key-up.
		_keyDialogPressedKeys.Add(e.Key);

		// Unmapped keys (media, PrintScreen, …) are left alone: nothing on the KEYS tab takes
		// text input, so letting them bubble is harmless and swallowing them hides nothing.
		if (HidKeyCodeMap.TryMap(e.Key, out var code, out _)
			&& _deviceDetailsViewModel.TryPickRemapTargetFromKeyboard(code))
			e.Handled = true;
	}

	private void OnKeyDialogKeyUp(object? sender, KeyEventArgs e)
	{
		// Unconditional removal: the dialog may have closed (or the tab switched) while the key
		// was held, and a stale entry would eat that key's next press as an "auto-repeat". The
		// release of a tracked press is swallowed for the keydown path's reason — Space keyup on
		// a focused Button raises Click.
		if (_keyDialogPressedKeys.Remove(e.Key))
			e.Handled = true;
	}

	private void OnRecorderPointerPressed(object? sender, PointerPressedEventArgs e)
		=> HandleRecorderPointer(e, e.GetCurrentPoint(this).Properties.PointerUpdateKind);

	private void OnRecorderPointerReleased(object? sender, PointerReleasedEventArgs e)
		=> HandleRecorderPointer(e, e.GetCurrentPoint(this).Properties.PointerUpdateKind);

	private void HandleRecorderPointer(PointerEventArgs e, PointerUpdateKind kind)
	{
		if (!_deviceDetailsViewModel.IsRecording)
			return;

		// Leave the record button alone so the user can always click STOP.
		if (IsWithinRecordButton(e.Source))
			return;

		if (TryMapMouseButton(kind, out var code, out var label, out var isPress))
		{
			_deviceDetailsViewModel.CaptureMouseEvent(code, isPress, label);
			e.Handled = true;
		}
	}

	// Default repetitions cap when the device catalog doesn't specify one.
	private const int DefaultMacroMaxRepetitions = 255;

	// Repetitions field is digits-only with a 1..max range (max comes from the current device's
	// Macros catalog config, else DefaultMacroMaxRepetitions). Sanitize on every change and write the
	// cleaned value straight back to the control so bad input (typed or pasted) is dropped immediately
	// — setting Text directly refreshes the display regardless of binding equality. A lone "0"
	// collapses to empty ("play once"), so the effective minimum is 1; values above the cap are clamped.
	private void OnRepetitionsTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (sender is not TextBox box || string.IsNullOrEmpty(box.Text))
			return;

		var device = _deviceDetailsViewModel.Device;
		var max = device is { MacroMaxRepetitions: > 0 } ? device.MacroMaxRepetitions : DefaultMacroMaxRepetitions;
		var maxText = max.ToString();
		var digits = new string(box.Text.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
		// Leading zeros are stripped, so more digits than the cap always means a larger number;
		// the length check also guards long.TryParse from ever seeing an overflowing string.
		if (digits.Length > 0 && (digits.Length > maxText.Length || (long.TryParse(digits, out var value) && value > max)))
			digits = maxText;

		if (digits == box.Text)
			return;

		var caret = box.CaretIndex;
		box.Text = digits;
		box.CaretIndex = System.Math.Min(caret, digits.Length);
	}

	private bool IsWithinRecordButton(object? source)
	{
		if (source is not Visual visual)
			return false;
		var button = GetMacroRecordButton();
		return button is not null && (ReferenceEquals(visual, button) || button.IsVisualAncestorOf(visual));
	}

	// Avalonia pointer button → BLAZE macro code (F0–F4) + label + press/release.
	// See docs/mouse/blaze/key-matrix.md: F0=L, F1=R, F2=M, F3=B4 (back), F4=B5 (forward).
	private static bool TryMapMouseButton(PointerUpdateKind kind, out byte code, out string label, out bool isPress)
	{
		int c;
		switch (kind)
		{
			case PointerUpdateKind.LeftButtonPressed:    c = 0xF0; label = "Left Click";   isPress = true;  break;
			case PointerUpdateKind.LeftButtonReleased:   c = 0xF0; label = "Left Click";   isPress = false; break;
			case PointerUpdateKind.RightButtonPressed:   c = 0xF1; label = "Right Click";  isPress = true;  break;
			case PointerUpdateKind.RightButtonReleased:  c = 0xF1; label = "Right Click";  isPress = false; break;
			case PointerUpdateKind.MiddleButtonPressed:  c = 0xF2; label = "Middle Click"; isPress = true;  break;
			case PointerUpdateKind.MiddleButtonReleased: c = 0xF2; label = "Middle Click"; isPress = false; break;
			case PointerUpdateKind.XButton1Pressed:      c = 0xF3; label = "Back";         isPress = true;  break;
			case PointerUpdateKind.XButton1Released:     c = 0xF3; label = "Back";         isPress = false; break;
			case PointerUpdateKind.XButton2Pressed:      c = 0xF4; label = "Forward";      isPress = true;  break;
			case PointerUpdateKind.XButton2Released:     c = 0xF4; label = "Forward";      isPress = false; break;
			default:                                     code = 0; label = string.Empty;  isPress = false; return false;
		}
		code = (byte)c;
		return true;
	}

	private void UpdateStepUi()
	{
		GetWelcomeView().IsVisible = _currentStep == AppStep.Welcome;
		GetAgreementTermsView().IsVisible = _currentStep == AppStep.AgreementTerms;
		GetChooseNicknameView().IsVisible = _currentStep == AppStep.ChooseNickname;
		GetHomeView().IsVisible = _currentStep == AppStep.Home;
		GetProfileView().IsVisible = _currentStep == AppStep.Profile;
		GetInformationView().IsVisible = _currentStep == AppStep.Information;
		GetSettingsView().IsVisible = _currentStep == AppStep.Settings;
		GetDeviceDetailsView().IsVisible = _currentStep == AppStep.DeviceDetails;
		GetStepLayoutView().IsVisible = _currentStep is not AppStep.Home and not AppStep.Profile and not AppStep.DeviceDetails and not AppStep.Settings and not AppStep.Information;

		ApplyStepLayoutProfile(GetLayoutProfile(_currentStep));
	}

	private void ApplyStepLayoutProfile(StepLayoutProfile profile)
	{
		GetLanguagePanel().IsVisible = false;
		GetHeaderTitleText().IsVisible = false;
		GetHeaderSubtitleText().IsVisible = false;

		GetRootLayoutGrid().Margin = profile.RootMargin;
		GetContentHostBorder().Margin = profile.ContentHostMargin;
		GetContentHostBorder().Padding = profile.ContentHostPadding;
	}

	private static StepLayoutProfile GetLayoutProfile(AppStep step) => step switch
	{
		// The Home RootMargin (24) is numerically baked into HomeView.axaml: the card-grid
		// ScrollViewer's negative margins compensate exactly this value to dock the design
		// scrollbar at the window edge (see the comment above that ScrollViewer). Changing
		// this Thickness silently shifts the whole card grid and pushes the bar off-window —
		// change both places together.
		AppStep.Home => new StepLayoutProfile(
			RootMargin: new Thickness(24),
			ContentHostMargin: new Thickness(0),
			ContentHostPadding: new Thickness(0)),

		AppStep.Profile or AppStep.DeviceDetails or AppStep.Settings or AppStep.Information => new StepLayoutProfile(
			RootMargin: new Thickness(24, 0, 24, 24),
			ContentHostMargin: new Thickness(0),
			ContentHostPadding: new Thickness(0)),

		_ => new StepLayoutProfile(
			RootMargin: new Thickness(24, 0, 24, 40),
			ContentHostMargin: new Thickness(0),
			ContentHostPadding: new Thickness(0))
	};
}
