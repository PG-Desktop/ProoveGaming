using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace ProoveGaming.App.Services;

// Keeps a single unhandled exception from taking the whole app down.
//
// A gaming peripheral app sits in the tray for days at a time. Losing it because one device event
// handler dereferenced a null — a real crash we hit when a mouse write completed after the user had
// already navigated away — costs the user their session for no good reason: the failure is almost
// always confined to the operation that threw, and everything else still works.
//
// Three sources are covered, because they fail in different ways:
//
//   1. Dispatcher (UI thread). The one that matters. Avalonia raises this for anything thrown out of
//      a UI callback — a command, a binding, a Dispatcher.Post continuation. Marking it Handled is
//      what actually prevents the shutdown.
//   2. Unobserved Task exceptions. The app uses fire-and-forget `_ =` in several device paths; if
//      one of those faults, the exception surfaces here at GC time and would otherwise be raised on
//      the finalizer thread.
//   3. AppDomain unhandled. The last-resort net for a background thread. It CANNOT be cancelled —
//      the process is already going down — so this only logs, which is the difference between a
//      silent disappearance and a diagnosable one.
//
// Deliberately NOT a blanket "ignore all errors": every exception is logged at Error with its
// source, and in DEBUG it is also surfaced to the user so it cannot be missed during development.
// Swallowing failures invisibly is how a broken app looks merely flaky.
public static class AppResilience
{
	private static ILogger? _logger;
	private static Action<string, string>? _notify;

	// Call once, as early as services exist. `notify` is optional and only used in DEBUG builds.
	public static void Install(ILogger logger, Action<string, string>? notify = null)
	{
		_logger = logger;
		_notify = notify;

		Dispatcher.UIThread.UnhandledException += (_, e) =>
		{
			Report("UI thread", e.Exception);
			// The whole point: without this Avalonia tears the app down.
			e.Handled = true;
		};

		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Report("background task", e.Exception);
			e.SetObserved();
		};

		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			// Not preventable — log it so the disappearance is at least explicable.
			Report("unhandled (app is terminating)", e.ExceptionObject as Exception);
		};
	}

	private static void Report(string source, Exception? ex)
	{
		if (ex is null) return;

		try
		{
			_logger?.LogError(ex, "Unhandled exception on {Source} — app kept running", source);
#if DEBUG
			// Debug only: during development a swallowed exception that leaves the UI subtly wrong
			// is worse than a crash, so make it impossible to miss. Never shown in Release.
			_notify?.Invoke($"Error ({source})", ex.Message);
#endif
		}
		catch
		{
			// Reporting must never itself bring the app down — that would defeat the purpose.
		}
	}
}
