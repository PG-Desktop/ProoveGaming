using Avalonia;
using System;
using System.Threading;

namespace ProoveGaming.App
{
	internal sealed class Program
	{
		// Initialization code. Don't use any Avalonia, third-party APIs or any
		// SynchronizationContext-reliant code before AppMain is called: things aren't initialized
		// yet and stuff might break.
		[STAThread]
		public static void Main(string[] args)
		{
			using var singleInstanceMutex = new Mutex(initiallyOwned: true, AppInstanceConstants.SingleInstanceMutexName, out var isFirstInstance);
			if (!isFirstInstance)
			{
				TrySignalExistingInstance();
				return;
			}

			App.InitializeSingleInstanceActivationSignal(AppInstanceConstants.SingleInstanceActivationEventName);

			AppSettings.Load();
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		}

		private static void TrySignalExistingInstance()
		{
			if (!OperatingSystem.IsWindows())
				return;

			try
			{
				using var activationEvent = EventWaitHandle.OpenExisting(AppInstanceConstants.SingleInstanceActivationEventName);
				activationEvent.Set();
			}
			catch
			{
				// Existing instance may still be starting up or signal is unavailable.
			}
		}

		// Render popups inside the main window (single HWND / focus scope). With the default
		// separate popup windows, a deactivate→activate cycle of the main window restores focus
		// into the window's own scope, so TextBoxes inside any open Popup silently stop
		// receiving keys (Backspace/typing dead until the dialog is reopened).
		//
		// This is a GLOBAL rendering switch: every Popup, Flyout, ToolTip and ComboBox dropdown
		// is now positioned within the window bounds (Avalonia flips/slides a popup that would
		// overflow), and light dismiss only reacts to clicks inside the window. Regression-
		// checked 2026-08-14: guest header popup at 1440x900 and at the 1200x880 minimum;
		// Presets dropdown, New Preset dialog and the keyboard-layout RGB color picker at
		// 1440x900. NOT yet checked (need a signed-in account / connected mouse): DatePicker
		// and CountrySelect dropdowns in Profile, RGB picker in the mouse right-hand column,
		// and the Device Details popups at the 1200x880 minimum — see the QA steps in
		// docs/plans/fix-profile-and-auth-validation/.
		//
		// Exposed as a field so ProgramPlatformOptionsTests can pin OverlayPopups = true; losing
		// the flag in a refactor would silently bring the popup focus bug back.
		internal static readonly Win32PlatformOptions WindowsPlatformOptions = new()
		{
			OverlayPopups = true,
		};

		// Avalonia configuration, don't remove; also used by visual designer.
		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.With(WindowsPlatformOptions)
				.WithInterFont()
				.LogToTrace();
	}
}
