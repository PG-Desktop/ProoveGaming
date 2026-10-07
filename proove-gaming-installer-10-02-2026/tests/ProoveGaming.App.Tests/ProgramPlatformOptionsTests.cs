using Xunit;

namespace ProoveGaming.App.Tests;

// Pins the Win32 platform options BuildAvaloniaApp registers. OverlayPopups is what keeps
// TextBoxes inside open Popups (nickname/email/delete-account dialogs, New Preset) receiving
// keyboard input after the window is deactivated and reactivated — with the default separate
// popup windows, activation restores focus into the main window's scope and the popup goes
// deaf. A refactor of Program.cs that drops the flag would bring that bug back with no
// compile-time signal; this test fails loudly instead.
public class ProgramPlatformOptionsTests
{
    [Fact]
    public void WindowsPlatformOptions_KeepOverlayPopupsEnabled()
    {
        Assert.True(Program.WindowsPlatformOptions.OverlayPopups);
    }
}
