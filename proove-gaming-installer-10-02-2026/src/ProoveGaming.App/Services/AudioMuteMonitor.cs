using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Services;

// Keeps DeviceCardViewModel.IsOutputSilent / IsMicrophoneSilent current for every audio-capable
// device, so the home card and the details connectivity bar show the same thing.
//
// Polled rather than pushed: mute is changed by things this app never sees — the Windows volume
// flyout, another application — and Core Audio gives us no cheap change notification here. One
// control-transfer read per endpoint every couple of seconds is not a hot path.
//
// The timer is created on the UI thread and its tick body marshals its own writes back there. That
// is deliberate: an earlier version created a DispatcherTimer from inside an async continuation
// that had already hopped to the thread pool, which is a thread-affinity violation waiting to
// throw. Anything that touches Avalonia here starts from the dispatcher.
public sealed class AudioMuteMonitor
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

	private readonly IAudioEndpointService _endpoints;
	private readonly ILogger<AudioMuteMonitor>? _logger;
	private readonly List<DeviceCardViewModel> _cards = [];
	private DispatcherTimer? _timer;
	private bool _polling;

	public AudioMuteMonitor(IAudioEndpointService endpoints, ILogger<AudioMuteMonitor>? logger = null)
	{
		_endpoints = endpoints;
		_logger = logger;
	}

	// Paired with Track. HomeViewModel.Stop() runs when the Home view unloads — including minimise
	// to tray — and without this the COM poll ran forever, resolving endpoints every two seconds to
	// refresh icons nobody can see.
	public void Stop()
	{
		Dispatcher.UIThread.VerifyAccess();
		_cards.Clear();
		_timer?.Stop();
		_timer = null;
	}

	// Replaces the watched set. Called whenever the device list changes; cards with no audio are
	// ignored, so this is a no-op for a machine with only mice and keyboards.
	public void Track(IEnumerable<DeviceCardViewModel> cards)
	{
		Dispatcher.UIThread.VerifyAccess();

		_cards.Clear();
		foreach (var card in cards)
		{
			if (card.HasAudioIndicators && card.AudioVolumeTarget == AudioVolumeTarget.OperatingSystem)
				_cards.Add(card);
		}

		if (_cards.Count == 0)
		{
			_timer?.Stop();
			_timer = null;
			return;
		}

		if (_timer is not null) return;

		_timer = new DispatcherTimer { Interval = PollInterval };
		_timer.Tick += (_, _) => _ = PollAsync();
		_timer.Start();

		// Poll immediately so the icons are right on the first frame rather than up to 2 s later.
		_ = PollAsync();
	}

	// "Silent" is what the icon actually communicates: nothing will be heard. Windows tracks mute
	// and level INDEPENDENTLY, so dragging a slider to 0 leaves the endpoint unmuted and GetMute
	// keeps returning false — which is why a zero-volume microphone kept showing the live Mic icon.
	// Either condition silences the endpoint, so either must light the off icon.
	//
	// A null reading means no endpoint resolved; that is "unknown", not "silent", so it reads as
	// audible rather than putting a false warning on screen.
	internal static bool IsSilent(bool? muted, int? volumePercent)
		=> muted == true || volumePercent == 0;

	private async Task PollAsync()
	{
		// Endpoint resolution is slower than the tick under load; overlapping polls would stack up.
		if (_polling) return;
		_polling = true;

		try
		{
			// Snapshot on the UI thread — _cards is only ever touched there.
			var targets = _cards.ToArray();

			foreach (var card in targets)
			{
				// One resolution per direction, not one per value: each call is a full
				// CoCreateInstance + EnumAudioEndpoints + a property-store read per endpoint, so
				// asking separately for level and mute doubled the COM work for no benefit.
				var output = await _endpoints.GetOutputStateAsync(card.AudioContainerId);
				var mic = await _endpoints.GetMicrophoneStateAsync(card.AudioContainerId);

				// Null means no endpoint resolved (headset off, or unplugged). Treat that as
				// audible rather than leaving a stale indicator describing a device that is gone.
				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					card.IsOutputSilent = IsSilent(output.Muted, output.VolumePercent);
					card.IsMicrophoneSilent = IsSilent(mic.Muted, mic.VolumePercent);
				});
			}
		}
		catch (Exception ex)
		{
			// Narrowed from a bare catch and logged: the body marshals onto the dispatcher, so a
			// genuine UI-thread fault used to vanish here indistinguishably from a COM hiccup. An
			// indicator is still not worth surfacing to the user — the next tick retries — but it
			// must be visible to whoever is debugging.
			_logger?.LogDebug(ex, "[Audio] mute/level poll failed; retrying on the next tick");
		}
		finally
		{
			_polling = false;
		}
	}
}
