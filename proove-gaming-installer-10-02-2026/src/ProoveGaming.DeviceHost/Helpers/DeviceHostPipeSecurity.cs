using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ProoveGaming.DeviceHost.Helpers;

/// <summary>
/// Who may connect to the DeviceHost pipe (docs/plans/migrate-devicehost-named-pipe).
/// </summary>
/// <remarks>
/// <para>
/// This service runs as LocalSystem and can program a device (a macro on a mouse button types
/// arbitrary keystrokes), so the ACL admits only the people using this machine: SYSTEM and
/// administrators, and INTERACTIVE, the users signed in at it. Services, batch jobs and network
/// logons get nothing. NETWORK is additionally denied; .NET's server pipe also rejects remote
/// clients. AppContainers get nothing (no ALL APPLICATION PACKAGES rule), and low-integrity
/// processes are refused by the pipe's default medium integrity label.
/// </para>
/// <para>
/// The account the host itself runs under always gets full control. Kestrel keeps several pipe
/// instances listening, and creating every instance after the first needs the CreateNewInstance
/// right on this very DACL. For the installed service that account is SYSTEM, which the list already
/// covers; for a host started from a developer's console it is the developer, who would otherwise
/// have read/write only and the host would fail to start. The DACL is protected, so nothing is
/// inherited.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DeviceHostPipeSecurity
{
	public static PipeSecurity Build()
	{
		var security = new PipeSecurity();
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

		security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
		security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
		security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
		security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

		using var self = WindowsIdentity.GetCurrent();
		if (self.User is { } account)
		{
			security.AddAccessRule(new PipeAccessRule(account, PipeAccessRights.FullControl, AccessControlType.Allow));
		}

		return security;
	}
}
