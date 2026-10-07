using System.Collections.Generic;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Services;

public enum AuthField { None, Email, Password, Nickname, Terms }

public static class ApiErrorMessages
{
    private static readonly Dictionary<string, (AuthField Field, string ResourceKey)> Map = new()
    {
        [AuthErrorCodes.EmailRequired] = (AuthField.Email, "Auth_Error_EmailRequired"),
        [AuthErrorCodes.InvalidEmail] = (AuthField.Email, "Auth_Error_InvalidEmail"),
        [AuthErrorCodes.EmailAlreadyExists] = (AuthField.Email, "Auth_Error_EmailAlreadyExists"),

        [AuthErrorCodes.PasswordRequired] = (AuthField.Password, "Auth_Error_PasswordRequired"),
        [AuthErrorCodes.PasswordTooShort] = (AuthField.Password, "Auth_Error_PasswordTooShort"),
        [AuthErrorCodes.PasswordNoUppercase] = (AuthField.Password, "Auth_Error_PasswordNoUppercase"),
        [AuthErrorCodes.PasswordNoNumber] = (AuthField.Password, "Auth_Error_PasswordNoNumber"),
        [AuthErrorCodes.PasswordHasSpaces] = (AuthField.Password, "Auth_Error_PasswordHasSpaces"),

        [AuthErrorCodes.NicknameRequired] = (AuthField.Nickname, "Auth_Error_NicknameRequired"),
        [AuthErrorCodes.NicknameTooShort] = (AuthField.Nickname, "Auth_Error_NicknameTooShort"),
        [AuthErrorCodes.NicknameTooLong] = (AuthField.Nickname, "Auth_Error_NicknameTooLong"),
        [AuthErrorCodes.NicknameHasSpaces] = (AuthField.Nickname, "Auth_Error_NicknameHasSpaces"),
        [AuthErrorCodes.NicknameInvalidCharacters] = (AuthField.Nickname, "Auth_Error_NicknameInvalidCharacters"),
        [AuthErrorCodes.NicknameAlreadyExists] = (AuthField.Nickname, "Auth_Error_NicknameAlreadyExists"),

        [AuthErrorCodes.TermsRequired] = (AuthField.Terms, "Auth_Error_TermsRequired"),
    };

    public static (AuthField Field, string Message)? Resolve(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (!Map.TryGetValue(code, out var entry)) return null;
        return (entry.Field, LocalizationManager.GetString(entry.ResourceKey));
    }

    public static IEnumerable<(AuthField Field, string Message)> ResolveAll(ApiResponse response)
    {
        if (response.Errors != null)
        {
            foreach (var e in response.Errors)
            {
                var r = Resolve(e?.ErrorCode);
                if (r.HasValue) yield return r.Value;
            }
        }
        var top = Resolve(response.ErrorCode);
        if (top.HasValue) yield return top.Value;
    }

    private static readonly Dictionary<string, string> ClientErrorKeys = new()
    {
        [ClientErrorCodes.NetworkError] = "Api_Error_Network",
        [ClientErrorCodes.Timeout] = "Api_Error_Timeout",
        [ClientErrorCodes.InvalidResponse] = "Api_Error_Generic",
    };

    /// <summary>
    /// The localized text for a failed call that no field-specific message covers: rate limiting,
    /// then a transport failure ApiHttpClient recorded (<see cref="ClientErrorCodes"/>), then
    /// <paramref name="fallbackKey"/>. The server's own <c>message</c> and exception text are
    /// never returned: they are English whatever the UI language, and exception text can name
    /// hosts and sockets (docs/plans/fix-repository-audit-findings, S10/A8).
    /// </summary>
    public static string Describe(ApiResponse response, string fallbackKey)
    {
        if (response.IsRateLimited)
            return LocalizationManager.GetString("Api_Error_TooManyRequests");

        if (response.ErrorCode is { } code && ClientErrorKeys.TryGetValue(code, out var key))
            return LocalizationManager.GetString(key);

        return LocalizationManager.GetString(fallbackKey);
    }
}
