namespace ProoveGaming.Infrastructure.Classes;

// How a bound macro repeats — maps to the device key-matrix macro "type" byte.
// Values mirror the gRPC MacroRepeatMode enum 1:1.
public enum MacroRepeatMode
{
	PlayCount = 0,    // play RepeatCount times
	LoopForever = 1,  // loop until any button press
	WhileHeld = 2,    // loop while the assigned button is held
}

// One macro step: a key/mouse make or break plus the delay after it.
// KeyCode = USB HID keyboard code, or mouse-button code F0-F4. DelayMs quantised to 10 ms.
public readonly record struct MacroEvent(byte KeyCode, bool IsPress, int DelayMs);

// A device-agnostic macro. The App library (SQLite) owns these; the device service encodes
// one to the on-wire byte format and uploads it (command 0x04). See docs/mouse/blaze/macros.md.
public sealed record MacroDefinition(
	string Name,
	int RepeatCount,
	MacroRepeatMode Mode,
	IReadOnlyList<MacroEvent> Events);
