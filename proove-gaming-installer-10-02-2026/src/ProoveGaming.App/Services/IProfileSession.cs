using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

// The profile as the sign-in/sign-out flow drives it. Implemented by ProfileViewModel
// (docs/plans/refactor-auth-session-flow).
public interface IProfileSession
{
    Task LoadFromServerAsync();

    void ClearLocalState();
}
