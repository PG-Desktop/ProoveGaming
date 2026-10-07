using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ProoveGaming.Common.Classes;

namespace ProoveGaming.Installer.Tests;

// Disk fixtures shared by the installer / uninstaller safety tests.
[SupportedOSPlatform("windows")]
internal static class TestFolders
{
    // Just the two files InstallSignature treats as proof; their content is irrelevant.
    public static string CreateInstallLayout(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, InstallLayout.DeviceHostFolderName));
        File.WriteAllText(Path.Combine(path, InstallLayout.AppExeName), string.Empty);
        File.WriteAllText(Path.Combine(path, InstallLayout.DeviceHostFolderName, InstallLayout.DeviceHostExeName), string.Empty);
        return path;
    }

    // mklink /J rather than Directory.CreateSymbolicLink: a junction needs no Developer Mode or
    // administrator rights, so these tests run on any machine — and a junction is exactly what a
    // standard user can plant.
    public static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = SystemTool.PathOf("cmd.exe"),
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();

        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
        {
            throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
        }
    }

    // Removes a test tree without following junctions (Directory.Delete deletes a link as a link),
    // first giving the current user back control of anything an ACL test locked down.
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            GrantCurrentUserFullControl(path);
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort test cleanup
        }
    }

    public static void GrantCurrentUserFullControl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
