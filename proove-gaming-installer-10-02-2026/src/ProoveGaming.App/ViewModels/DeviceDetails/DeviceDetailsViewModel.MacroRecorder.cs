using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// ── Macro recorder ─────────────────────────────────────────────────────────
	// Live state for the Create Macro dialog. Captures keyboard keys AND mouse-button clicks
	// (device codes F0–F4); each step is timed from the previous one. The window forwards
	// make/break events here while IsRecording (see MainWindow input handlers).
	private readonly Stopwatch _recordStopwatch = new();

	// The mouse stores at most this many make/break events per macro; the device silently
	// drops anything beyond it on upload. The App can't reference Infrastructure, so the
	// ceilings are mirrored here; keep them in sync with X43MacroEncoder.MaxEvents (BLAZE,
	// delay rides inside the event byte) and TaipanMacroEncoder.MaxEvents (TAIPAN, a delay
	// costs a separate 3-byte action, halving the capacity).
	private const int BlazeMaxMacroEvents = 62;
	private const int TaipanMaxMacroEvents = 32;

	private int MaxMacroEvents =>
		string.Equals(Device?.Name, "PG TAIPAN", StringComparison.OrdinalIgnoreCase)
			? TaipanMaxMacroEvents
			: BlazeMaxMacroEvents;

	// Longest inter-event pause the recorder keeps, from the device catalog's Macros config
	// (0/unset → DefaultMaxDelayMs). BLAZE packs the delay into a 7-bit field whose high values
	// misbehave, so its encoder caps at ~1 s (X43MacroEncoder); TAIPAN stores the delay as its own
	// 16-bit action and can hold much longer pauses.
	private const int DefaultMaxDelayMs = 1000;
	private int MaxDelayMs => Device is { MacroMaxDelayMs: > 0 } d ? d.MacroMaxDelayMs : DefaultMaxDelayMs;

	// Click numbering: a press starts a new click number; its release reuses it, so both rows
	// of one click show the same "N." on the left.
	private int _recordClickCount;
	private readonly System.Collections.Generic.Dictionary<byte, int> _pressClickByCode = new();

	// Whether the current device advertises hardware-macro support (host catalog flag).
	public bool IsMacrosSupported => Device?.SupportsMacros == true;

	public ObservableCollection<RecordedStepViewModel> RecordedSteps { get; } = [];

	private bool _isRecording;
	public bool IsRecording
	{
		get => _isRecording;
		private set
		{
			if (SetProperty(ref _isRecording, value))
				OnPropertyChanged(nameof(RecordButtonLabel));
		}
	}

	public bool HasRecordedSteps => RecordedSteps.Count > 0;

	// "Strings: N" summary above the recorded-steps list, N = number of captured steps.
	public string RecordedStepsCountLabel =>
		string.Format(LocalizationManager.GetString("Macros_RecordedCount"), RecordedSteps.Count);

	// Set when recording auto-stops because the hardware macro buffer is full, so the UI can
	// explain why capture ended instead of silently dropping further steps.
	private bool _isRecordLimitReached;
	public bool IsRecordLimitReached
	{
		get => _isRecordLimitReached;
		private set => SetProperty(ref _isRecordLimitReached, value);
	}

	public string RecordLimitLabel =>
		string.Format(LocalizationManager.GetString("Macros_LimitReached"), MaxMacroEvents);

	public string RecordButtonLabel => LocalizationManager.GetString(
		IsRecording ? "Macros_StopRecording" : "Macros_StartRecording");

	[RelayCommand]
	private void ToggleRecording()
	{
		if (IsRecording) StopRecording();
		else StartRecording();
	}

	private void StartRecording()
	{
		_recordStopwatch.Restart();
		IsRecordLimitReached = false;
		IsRecording = true;
	}

	private void StopRecording()
	{
		IsRecording = false;
		_recordStopwatch.Stop();
	}

	// Forwarded from the window for each keyboard make/break while recording.
	internal void CaptureKeyEvent(byte hidCode, bool isPress, string label)
		=> AddRecordedStep(hidCode, isPress, isKeyboard: true, label);

	// Forwarded from the window for each mouse-button make/break while recording (codes F0–F4).
	internal void CaptureMouseEvent(byte code, bool isPress, string label)
		=> AddRecordedStep(code, isPress, isKeyboard: false, label);

	private void AddRecordedStep(byte code, bool isPress, bool isKeyboard, string label)
	{
		if (!IsRecording) return;

		// Buffer full — the device would drop anything past MaxMacroEvents on upload, so stop
		// capturing and flag it rather than recording steps that will never play back.
		if (_recordedEvents.Count >= MaxMacroEvents)
		{
			StopRecording();
			// The label is computed from the OPEN device's limit, but nothing else notifies it, so
			// its binding kept whatever it read first — 62 from a BLAZE or no device at all — and a
			// TAIPAN stopping at 32 strings announced "maximum of 62". Refreshed here, the only
			// moment it becomes visible.
			OnPropertyChanged(nameof(RecordLimitLabel));
			IsRecordLimitReached = true;
			return;
		}

		var delayMs = 0;
		// The first step carries no delay — otherwise the idle time before the user's first
		// input would make the assigned button feel laggy. Later steps use real timing.
		if (NewMacroAutoRecordDelays && RecordedSteps.Count > 0)
		{
			var elapsed = (int)_recordStopwatch.ElapsedMilliseconds;
			// Device unit is 10 ms; cap per device (MaxDelayMs). BLAZE stays ~1 s because its
			// top-of-range delay values misbehave (see docs/mouse/blaze/macros.md); TAIPAN's
			// separate 16-bit delay action allows much longer pauses. Keep the UI in step.
			delayMs = Math.Clamp(elapsed / 10 * 10, 0, MaxDelayMs);
		}
		_recordStopwatch.Restart();

		int iteration;
		if (isPress)
		{
			iteration = ++_recordClickCount;
			_pressClickByCode[code] = iteration;
		}
		else if (!_pressClickByCode.Remove(code, out iteration))
		{
			iteration = _recordClickCount;
		}

		_recordedEvents.Add(new MacroEventRecord { KeyCode = code, IsPress = isPress, DelayMs = delayMs });
		RecordedSteps.Add(new RecordedStepViewModel
		{
			IsKeyboard = isKeyboard,
			Label = label,
			DelayMs = delayMs,
			IsPress = isPress,
			Iteration = iteration,
			RemoveCommand = RemoveRecordedStepCommand,
		});
		OnPropertyChanged(nameof(HasRecordedSteps));
		OnPropertyChanged(nameof(RecordedStepsCountLabel));
	}

	// CLEAR link in the recorder summary row: drops every captured step so the user can start the
	// recording over. Leaves the recording on/off state alone.
	[RelayCommand]
	private void ClearRecordedSteps()
	{
		RecordedSteps.Clear();
		_recordedEvents.Clear();
		_recordClickCount = 0;
		_pressClickByCode.Clear();
		IsRecordLimitReached = false;
		OnPropertyChanged(nameof(HasRecordedSteps));
		OnPropertyChanged(nameof(RecordedStepsCountLabel));
	}

	[RelayCommand]
	private void RemoveRecordedStep(RecordedStepViewModel step)
	{
		if (step is null) return;
		var index = RecordedSteps.IndexOf(step);
		if (index < 0) return;

		RecordedSteps.RemoveAt(index);
		if (index < _recordedEvents.Count)
			_recordedEvents.RemoveAt(index);
		OnPropertyChanged(nameof(HasRecordedSteps));
		OnPropertyChanged(nameof(RecordedStepsCountLabel));
	}

	// Describes a stored macro code for display (used when editing an existing macro).
	// Mouse buttons are device codes F0–F4; everything else is a keyboard HID usage.
	internal static (bool IsKeyboard, string Label) DescribeMacroCode(byte code) => code switch
	{
		0xF0 => (false, "Left Click"),
		0xF1 => (false, "Right Click"),
		0xF2 => (false, "Middle Click"),
		0xF3 => (false, "Back"),
		0xF4 => (false, "Forward"),
		_    => (true, HidKeyCodeMap.LabelFor(code)),
	};

	// Called when the Create Macro dialog opens/closes so a new recording starts clean.
	private void ResetRecorder()
	{
		StopRecording();
		RecordedSteps.Clear();
		_recordedEvents.Clear();
		_recordClickCount = 0;
		_pressClickByCode.Clear();
		IsRecordLimitReached = false;
		OnPropertyChanged(nameof(HasRecordedSteps));
		OnPropertyChanged(nameof(RecordedStepsCountLabel));
	}
}
