namespace ProoveGaming.Infrastructure.Classes;

// A physical, device-agnostic mouse button (which button), independent of any device's
// key-matrix slot numbering. Each device service maps the buttons it supports to its own
// physical slots (e.g. X43KeyMatrix.ButtonToSlot), so adding a new device or button
// is just a new enum member — no cross-device index collisions. Persisted by NAME.
// Values mirror the gRPC MouseButton enum 1:1 so DeviceService can map by numeric value.
public enum MouseButton
{
	Unspecified = 0,
	Left = 1,
	Right = 2,
	Wheel = 3,
	Forward = 4,
	Back = 5,
}

// Assignable actions for a mouse button (what it does). Values mirror the gRPC
// MouseButtonAction enum 1:1 so DeviceService can map by numeric value.
// X43KeyMatrix.EncodeAction turns each into a 4-byte key-matrix entry
// (source: docs/x43_protocol_904a4f8.md §V).
public enum MouseButtonAction
{
	Unspecified = 0,
	// Standard mouse buttons (also used to revert to a normal function)
	LeftClick = 1,
	RightClick = 2,
	WheelClick = 3,
	Forward = 4,
	Back = 5,
	// Recommended (media)
	PlayPause = 6,
	NextTrack = 7,
	PreviousTrack = 8,
	Stop = 9,
	Mute = 10,
	VolumeUp = 11,
	VolumeDown = 12,
	MediaPlayer = 13,
	WwwHome = 14,
	// System
	MyComputer = 15,
	Desktop = 16,
	WwwSearch = 17,
	// A recorded macro (definition carried on ButtonBinding.Macro).
	Macro = 18,
}

// One button assignment: which physical button does what. Device-agnostic — the device
// service resolves Button to its own matrix slot at write time. Macro is set only when
// Action == Macro (the definition travels with the binding so the device service can
// upload it before binding).
public readonly record struct ButtonBinding(
	MouseButton Button,
	MouseButtonAction Action,
	MacroDefinition? Macro = null);
