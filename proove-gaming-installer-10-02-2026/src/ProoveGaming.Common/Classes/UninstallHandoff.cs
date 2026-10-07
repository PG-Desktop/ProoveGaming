using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ProoveGaming.Common.Classes;

/// <summary>
/// The contract between the uninstaller's <b>detached self-delete process</b> and any later
/// installer run, so the two cannot fight over the same folder.
/// </summary>
/// <remarks>
/// <para>
/// The uninstaller cannot delete its own EXE while it is running, so it spawns a detached
/// PowerShell (falling back to <c>cmd.exe</c>) that waits for it to exit and then removes the
/// install folder, retrying for up to ~2 minutes because a file may still be locked.
/// </para>
/// <para>
/// That loop deletes <i>a path</i>. It has no idea whether the folder now at that path is the old
/// install or a <b>freshly reinstalled one</b>. Uninstall, reinstall immediately (what a tester
/// does), and the loop's next pass deletes part of the new install — producing an app that is
/// missing arbitrary files and dies on whichever native DLL it lost. Diagnosis:
/// <c>docs/support/missing-files-crash-analysis.md</c>, cause 0.
/// </para>
/// <para>
/// This marker fixes that. The loop deletes only while the marker exists; the installer removes it
/// (and stops the process named in it) before copying, so any surviving loop stops on its next
/// iteration.
/// </para>
/// <para>
/// <b>The marker lives outside the install folder, deliberately.</b> Inside, the loop's own
/// recursive delete would remove it partway through a legitimate cleanup, the loop would then stop
/// early, and an uninstall that just needed another retry would leave files behind — trading this
/// bug for a worse one.
/// </para>
/// </remarks>
public static class UninstallHandoff
{
	/// <summary>
	/// How long a marker stays meaningful. Past this it is treated as debris from an interrupted
	/// self-delete (a reboot mid-loop leaves one behind, because the loop's own cleanup never runs)
	/// and never used to stop a process: by then Windows may have recycled the recorded process id
	/// onto something unrelated. The loop itself only lives ~2 minutes, so this is generous.
	/// </summary>
	public static readonly TimeSpan MarkerMaxAge = TimeSpan.FromMinutes(30);

	// How far around the marker's own timestamp the self-delete process may have started. It is
	// launched immediately before the marker is rewritten with its id, so the real one is always
	// within seconds; the window is wide enough to absorb a slow machine and no wider.
	private static readonly TimeSpan StartedBeforeMarkerAllowance = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan StartedAfterMarkerAllowance = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Marker path for one install folder.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Kept in <c>%ProgramData%</c>, not <c>%TEMP%</c>: temp is profile-relative, and the
	/// uninstaller, its detached loop and the installer each elevate independently. A standard user
	/// who uninstalls with one administrator's credentials and reinstalls with another's would get
	/// two different temp folders, the marker would never be found, and the whole safety net would
	/// silently do nothing. <c>ProoveGaming.Uninstaller.Program.EnsureElevated</c> documents the
	/// same hazard for <c>%LocalAppData%</c> and the shell folders.
	/// </para>
	/// <para>
	/// The name is a SHA-256 of the normalised path so both processes compute the same one —
	/// <c>string.GetHashCode</c> is randomised per process in .NET Core and would give the installer
	/// a different answer than the uninstaller.
	/// </para>
	/// </remarks>
	public static string GetMarkerPath(string installPath)
	{
		var hash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(installPath))))[..16].ToLowerInvariant();

		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
			$"ProoveGaming.uninstall.{hash}.marker");
	}

	// The install path reaches the two sides by different routes - a text box in the installer, the
	// registry in the uninstaller - so it is fully resolved before hashing. Without GetFullPath,
	// "C:/Games/PG", a trailing space or a ".." segment hash differently from the same folder
	// written plainly, and the two sides never meet.
	private static string Normalise(string installPath)
	{
		try
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath)).ToLowerInvariant();
		}
		catch (ArgumentException)
		{
			// Not a resolvable path. Both sides run the same fallback, so they still agree.
			return installPath.Trim().ToLowerInvariant();
		}
		catch (NotSupportedException)
		{
			return installPath.Trim().ToLowerInvariant();
		}
		catch (PathTooLongException)
		{
			return installPath.Trim().ToLowerInvariant();
		}
	}

	/// <summary>
	/// Whether a process found at the marker's recorded id is really that uninstall's self-delete
	/// loop, and therefore safe to stop.
	/// </summary>
	/// <remarks>
	/// Every clause here exists to stop the installer — which runs <b>elevated</b> — killing
	/// something it did not start. A recorded id outlives the process it named, Windows recycles
	/// ids, and <c>powershell.exe</c> is a common thing for a user to have open, so identity is
	/// established from three independent signals: the marker is recent, the process looks like the
	/// shell we launch, and it started when the marker says it did. Bounded on <b>both</b> sides:
	/// a lower bound alone admits every process started later, which is exactly the stale-marker
	/// case.
	/// </remarks>
	public static bool ShouldStopSelfDeleteProcess(
		string processName, DateTime processStartUtc, DateTime markerStartedUtc, DateTime nowUtc)
	{
		// No usable timestamp means no way to tell the real process from a recycled id. Previously
		// this case skipped the age check altogether, which made it the most dangerous input rather
		// than the safest.
		if (markerStartedUtc == default)
		{
			return false;
		}

		if (nowUtc - markerStartedUtc > MarkerMaxAge)
		{
			return false;
		}

		var isSelfDeleteShell =
			processName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
			processName.Equals("cmd", StringComparison.OrdinalIgnoreCase);
		if (!isSelfDeleteShell)
		{
			return false;
		}

		return processStartUtc >= markerStartedUtc - StartedBeforeMarkerAllowance
			&& processStartUtc <= markerStartedUtc + StartedAfterMarkerAllowance;
	}

	/// <summary>
	/// Records that a self-delete process owns <paramref name="installPath"/>. Returns whether the
	/// marker actually landed.
	/// </summary>
	/// <remarks>
	/// The caller <b>must</b> honour a <see langword="false"/> result by emitting the unguarded
	/// self-delete script. A guarded loop tests this marker before every pass, so if the marker is
	/// missing it exits immediately having deleted nothing — turning a failed best-effort write into
	/// a silently skipped uninstall. Losing the race protection is the intended degradation; losing
	/// the cleanup is not.
	/// </remarks>
	public static bool Write(string installPath, int selfDeleteProcessId)
	{
		try
		{
			var path = GetMarkerPath(installPath);
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllText(
				path,
				string.Join(
					Environment.NewLine,
					$"pid={selfDeleteProcessId.ToString(CultureInfo.InvariantCulture)}",
					$"startedUtc={DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}",
					$"installPath={installPath}"));

			return true;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// Reads a marker left by a previous uninstall. False when there is none, or it is unreadable
	/// or malformed — in every one of those cases there is nothing for the caller to act on.
	/// </summary>
	public static bool TryRead(string installPath, out int selfDeleteProcessId, out DateTime startedUtc)
	{
		selfDeleteProcessId = 0;
		startedUtc = default;

		try
		{
			var path = GetMarkerPath(installPath);
			if (!File.Exists(path))
			{
				return false;
			}

			foreach (var line in File.ReadAllLines(path))
			{
				if (line.StartsWith("pid=", StringComparison.Ordinal))
				{
					_ = int.TryParse(
						line["pid=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture,
						out selfDeleteProcessId);
				}
				else if (line.StartsWith("startedUtc=", StringComparison.Ordinal))
				{
					// Only normalised on success. Calling ToUniversalTime() on a failed parse would
					// shift default(DateTime) off zero on any machine west of UTC, and `== default`
					// is what ShouldStopSelfDeleteProcess uses to recognise "no usable timestamp".
					if (DateTime.TryParse(
							line["startedUtc=".Length..], CultureInfo.InvariantCulture,
							DateTimeStyles.RoundtripKind, out var parsed))
					{
						startedUtc = parsed.ToUniversalTime();
					}
				}
			}

			return selfDeleteProcessId > 0;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// Drops the marker, which is what tells a still-running self-delete loop to stop. Best-effort
	/// for the same reason as <see cref="Write"/>.
	/// </summary>
	public static void Remove(string installPath)
	{
		try
		{
			File.Delete(GetMarkerPath(installPath));
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
