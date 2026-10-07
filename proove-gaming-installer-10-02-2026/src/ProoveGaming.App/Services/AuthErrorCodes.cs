namespace ProoveGaming.App.Services;

// Server error_code strings shared across the auth/profile flows. Centralized so
// switches and lookups can reference constants instead of magic strings — see the
// v1.7 Launcher API spec for the full catalog.
public static class AuthErrorCodes
{
    // Token / session
    public const string TokenRequired = "TOKEN_REQUIRED";
    public const string InvalidOrExpiredToken = "INVALID_OR_EXPIRED_TOKEN";

    // Email
    public const string EmailRequired = "EMAIL_REQUIRED";
    public const string InvalidEmail = "INVALID_EMAIL";
    public const string EmailAlreadyExists = "EMAIL_ALREADY_EXISTS";
    public const string EmailNotFound = "EMAIL_NOT_FOUND";
    public const string MailError = "MAIL_ERROR";

    // Password
    public const string PasswordRequired = "PASSWORD_REQUIRED";
    public const string PasswordTooShort = "PASSWORD_TOO_SHORT";
    public const string PasswordNoUppercase = "PASSWORD_NO_UPPERCASE";
    public const string PasswordNoNumber = "PASSWORD_NO_NUMBER";
    public const string PasswordHasSpaces = "PASSWORD_HAS_SPACES";
    public const string InvalidPassword = "INVALID_PASSWORD";

    // Nickname
    public const string NicknameRequired = "NICKNAME_REQUIRED";
    public const string NicknameTooShort = "NICKNAME_TOO_SHORT";
    public const string NicknameTooLong = "NICKNAME_TOO_LONG";
    public const string NicknameHasSpaces = "NICKNAME_HAS_SPACES";
    public const string NicknameInvalidCharacters = "NICKNAME_INVALID_CHARACTERS";
    public const string NicknameAlreadyExists = "NICKNAME_ALREADY_EXISTS";

    // Avatar upload (v2.0 §12 api/profile/avatar)
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string InvalidFileType = "INVALID_FILE_TYPE";
    public const string UploadError = "UPLOAD_ERROR";
    public const string UploadFailed = "UPLOAD_FAILED";
    public const string AvatarRequired = "AVATAR_REQUIRED";

    // Contact Us (v2.0 §20 api/contact). EmailRequired / InvalidEmail / MailError above are
    // reused; the rate-limit trio is the endpoint's anti-spam contract (1 per 60 s, 5 per 24 h,
    // identical topic+message blocked for 10 minutes).
    public const string InvalidJson = "INVALID_JSON";
    public const string TopicRequired = "TOPIC_REQUIRED";
    public const string TopicTooShort = "TOPIC_TOO_SHORT";
    public const string TopicTooLong = "TOPIC_TOO_LONG";
    public const string MessageRequired = "MESSAGE_REQUIRED";
    public const string MessageTooShort = "MESSAGE_TOO_SHORT";
    public const string MessageTooLong = "MESSAGE_TOO_LONG";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string DailyLimitExceeded = "DAILY_LIMIT_EXCEEDED";
    public const string DuplicateMessage = "DUPLICATE_MESSAGE";

    // Terms
    public const string TermsRequired = "TERMS_REQUIRED";
}
