namespace ProoveGaming.App.Services;

// Maps v1.7 §14 api/forgotten error_code values onto localized inline text, shared by the
// Welcome forgot-password panel and the Profile change-password dialog. INVALID_EMAIL /
// EMAIL_REQUIRED map to the field-level error under the email input (empty message); the
// rest render as inline API-error text. Anything unrecognized falls back to the generic message.
internal static class ForgottenPasswordErrors
{
    public static (bool IsEmailFieldError, string Message) Resolve(ApiResponse response)
    {
        switch (RecognizedCode(response))
        {
            case AuthErrorCodes.EmailRequired:
            case AuthErrorCodes.InvalidEmail:
                return (true, string.Empty);
            case AuthErrorCodes.EmailNotFound:
                return (false, LocalizationManager.GetString("WelcomeView_Forgot_Error_NotFound"));
            case AuthErrorCodes.MailError:
                return (false, LocalizationManager.GetString("WelcomeView_Forgot_Error_Mail"));
            default:
                return (false, LocalizationManager.GetString("WelcomeView_Forgot_Error_Generic"));
        }
    }

    // The envelope may carry several entries and the recognized one is not necessarily the
    // first: [{UNKNOWN_CODE}, {EMAIL_NOT_FOUND}] used to resolve to the generic message
    // because only entry 0 was read. Scan top-level first (the single-code shape), then every
    // entry, and return the first code this resolver actually handles; when none is
    // recognized fall back to the old behaviour so the generic branch still gets a code to
    // log against.
    private static string? RecognizedCode(ApiResponse response)
    {
        if (IsHandled(response.ErrorCode))
            return response.ErrorCode;
        if (response.Errors is { } errors)
        {
            foreach (var error in errors)
            {
                if (IsHandled(error?.ErrorCode))
                    return error!.ErrorCode;
            }
        }
        return response.ErrorCode
            ?? (response.Errors is { Count: > 0 } ? response.Errors[0]?.ErrorCode : null);
    }

    private static bool IsHandled(string? code) => code is
        AuthErrorCodes.EmailRequired
        or AuthErrorCodes.InvalidEmail
        or AuthErrorCodes.EmailNotFound
        or AuthErrorCodes.MailError;
}
