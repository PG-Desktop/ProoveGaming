using System;
using System.Linq;
using System.Reflection;
using ProoveGaming.Uninstaller;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins the decision that the uninstaller never defers work to a reboot
// (docs/support/missing-files-crash-analysis.md, cause 3).
//
// MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT) condemns a PATH, not a file. The uninstaller's own EXE
// and the three native DLLs it loads out of the install folder are the only files it can never
// delete while running, so those four were scheduled on every uninstall; the detached self-delete
// loop then removed them seconds later and the condemnations outlived them. A reinstall into the
// same folder wrote fresh files at those paths and the next boot deleted them.
//
// Reintroducing that call "for safety" would look harmless and break nothing any other test
// checks, so the absence of the P/Invoke is asserted directly. This is an architectural test on
// purpose: the deletion helpers are private statics that call File/Directory straight through, so a
// behavioural test would cost far more than the decision is expensive to protect.
public class NoRebootDeferralTests
{
    [Fact]
    public void UninstallerAssembly_DeclaresNoMoveFileExPInvoke()
    {
        var offenders = PInvokeMethodsIn(typeof(UninstallService).Assembly)
            .Where(m => m.Name.Contains("MoveFile", StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The uninstaller must not schedule file operations for the next boot. Found: " +
            string.Join(", ", offenders) +
            ". See docs/support/missing-files-crash-analysis.md, cause 3.");
    }

    [Fact]
    public void UninstallerAssembly_DeclaresNoPInvokeAtAll()
    {
        // Broader net, and currently true: MoveFileEx was the only one. A new P/Invoke is not
        // forbidden, but it should be a deliberate decision rather than something that slips in
        // next to the deletion helpers — which is exactly how the reboot fallback got here.
        var declared = PInvokeMethodsIn(typeof(UninstallService).Assembly)
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .ToList();

        Assert.True(
            declared.Count == 0,
            "New P/Invoke in the uninstaller: " + string.Join(", ", declared) +
            ". If it is intentional, widen this test; do not delete it.");
    }

    [Fact]
    public void TheDetectorItself_FindsAKnownPInvoke()
    {
        // Positive control. Both assertions above are "found nothing", which is exactly what a
        // broken reflection walk also reports — a silently vacuous test would leave the decision
        // unprotected while looking green. The installer declares MessageBoxW
        // (MainWindow.axaml.cs), so the same walk must find it there.
        var found = PInvokeMethodsIn(typeof(InstallerConstants).Assembly)
            .Select(m => m.Name)
            .ToList();

        Assert.Contains(found, name => name.StartsWith("MessageBox", StringComparison.Ordinal));
    }

    private static MethodInfo[] PInvokeMethodsIn(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .ToArray();
}
