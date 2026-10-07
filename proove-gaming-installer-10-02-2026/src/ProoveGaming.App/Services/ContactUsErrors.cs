namespace ProoveGaming.App.Services;

// Maps v2.0 §20 api/contact failures onto the dialog's single error line, mirroring
// ForgottenPasswordErrors' shape. The endpoint's three anti-spam refusals (1 message per
// 60 s, 5 per 24 h, identical topic+message blocked for 10 minutes) each get their own
// wording, because "please try again" — the message the dialog used to show for all of
// them — is advice that, followed literally, re-hits the same limit.
//
// Codes are matched by priority rather than by envelope position: the server may answer with
// several entries, and a rate-limit refusal is the one the user has to act on first. Every
// check scans the top-level error_code AND every errors[] entry (HasErrorCode), so a
// recognized code is never missed because it was not the first in the list.
internal static class ContactUsErrors
{
    public static string ResolveKey(ApiResponse response)
    {
        // The server never answered, so no code can be trusted — and here retrying really is
        // the right advice, so the generic wording is the honest one.
        if (response.IsNetworkError)
            return "ContactUs_SendFailed";

        // HTTP 429 is the transport-level form of the same refusal; the contract also allows
        // the code inside the envelope, so both spellings land on one message.
        if (response.IsRateLimited || Has(response, AuthErrorCodes.RateLimitExceeded))
            return "ContactUs_Error_RateLimited";

        if (Has(response, AuthErrorCodes.DailyLimitExceeded))
            return "ContactUs_Error_DailyLimit";

        if (Has(response, AuthErrorCodes.DuplicateMessage))
            return "ContactUs_Error_Duplicate";

        // The server accepted the message and the mail relay failed — retrying is genuinely
        // useful here, unlike for every validation code below.
        if (Has(response, AuthErrorCodes.MailError))
            return "ContactUs_Error_MailError";

        if (Has(response, AuthErrorCodes.EmailRequired) || Has(response, AuthErrorCodes.InvalidEmail))
            return "ContactUs_Error_InvalidEmail";

        if (Has(response, AuthErrorCodes.TopicRequired)
            || Has(response, AuthErrorCodes.TopicTooShort)
            || Has(response, AuthErrorCodes.TopicTooLong))
            return "ContactUs_Error_TopicLength";

        if (Has(response, AuthErrorCodes.MessageRequired)
            || Has(response, AuthErrorCodes.MessageTooShort)
            || Has(response, AuthErrorCodes.MessageTooLong))
            return "ContactUs_Error_MessageLength";

        // INVALID_JSON and anything the contract adds later: nothing the user can act on
        // specifically, so the generic wording stands.
        return "ContactUs_SendFailed";
    }

    private static bool Has(ApiResponse response, string code) =>
        SettingsSyncService.HasErrorCode(response, code);
}
