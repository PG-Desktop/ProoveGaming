using System;
using System.Globalization;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Services;

// User beats guest: after a successful sign-in the UserProfile carries the
// customer id and we key writes to ("user", customerId) even if the SessionService
// hasn't been reset yet.
public sealed class CurrentOwnerProvider : ICurrentOwnerProvider
{
    private readonly SessionService _session;
    private readonly UserProfile _userProfile;

    public CurrentOwnerProvider(SessionService session, UserProfile userProfile)
    {
        _session = session;
        _userProfile = userProfile;
    }

    public SettingsOwner? GetCurrent()
    {
        if (_userProfile.CustomerId > 0)
            return new SettingsOwner(
                LocalStorageService.OwnerKindUser,
                _userProfile.CustomerId.ToString(CultureInfo.InvariantCulture));

        if (_session.IsGuest && _session.SessionId != Guid.Empty)
            return new SettingsOwner(
                LocalStorageService.OwnerKindGuest,
                _session.SessionId.ToString("D"));

        return null;
    }
}
