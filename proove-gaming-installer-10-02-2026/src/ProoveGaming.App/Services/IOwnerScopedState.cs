namespace ProoveGaming.App.Services;

// The owner-scoped part of Home (device cards and the bell) as the sign-in/sign-out flow drives
// it. Implemented by HomeViewModel; an interface so AuthSessionCoordinator is testable without
// the device graph (docs/plans/refactor-auth-session-flow).
public interface IOwnerScopedState
{
    void ReloadOwnerScopedState();

    void ClearOwnerScopedState();

    void AddNotification(string title, string subtitle);
}
