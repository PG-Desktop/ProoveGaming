namespace ProoveGaming.App.Services;

// A resolved owner used to key settings rows in local storage. Null means the
// process is in the pre-guest state (no session yet); callers should skip writes
// and treat reads as "not found".
public readonly record struct SettingsOwner(string Kind, string Id);

public interface ICurrentOwnerProvider
{
    SettingsOwner? GetCurrent();
}
