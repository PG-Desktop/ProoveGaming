using System.Linq;
using System.Reflection;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Pins the constant values against the exact server error_code strings from the v1.7 / v2.0
// spec. A rename or typo in AuthErrorCodes would silently break the switch/lookup call sites;
// these asserts fail loudly instead.
//
// The theory is EXHAUSTIVE as of docs/plans/improve-api-error-surfacing: every public const on
// AuthErrorCodes has a row, and Theory_CoversEveryConstant fails if someone adds one without
// pinning it. Seven constants were unpinned before that plan; the v2.0 avatar and Contact Us
// groups arrived with it.
public class AuthErrorCodesTests
{
    [Theory]
    [InlineData("TOKEN_REQUIRED", nameof(AuthErrorCodes.TokenRequired))]
    [InlineData("INVALID_OR_EXPIRED_TOKEN", nameof(AuthErrorCodes.InvalidOrExpiredToken))]
    [InlineData("EMAIL_REQUIRED", nameof(AuthErrorCodes.EmailRequired))]
    [InlineData("INVALID_EMAIL", nameof(AuthErrorCodes.InvalidEmail))]
    [InlineData("EMAIL_ALREADY_EXISTS", nameof(AuthErrorCodes.EmailAlreadyExists))]
    [InlineData("EMAIL_NOT_FOUND", nameof(AuthErrorCodes.EmailNotFound))]
    [InlineData("MAIL_ERROR", nameof(AuthErrorCodes.MailError))]
    [InlineData("PASSWORD_REQUIRED", nameof(AuthErrorCodes.PasswordRequired))]
    [InlineData("INVALID_PASSWORD", nameof(AuthErrorCodes.InvalidPassword))]
    [InlineData("NICKNAME_ALREADY_EXISTS", nameof(AuthErrorCodes.NicknameAlreadyExists))]
    [InlineData("NICKNAME_INVALID_CHARACTERS", nameof(AuthErrorCodes.NicknameInvalidCharacters))]
    [InlineData("FILE_TOO_LARGE", nameof(AuthErrorCodes.FileTooLarge))]
    [InlineData("INVALID_FILE_TYPE", nameof(AuthErrorCodes.InvalidFileType))]
    [InlineData("PASSWORD_TOO_SHORT", nameof(AuthErrorCodes.PasswordTooShort))]
    [InlineData("PASSWORD_NO_UPPERCASE", nameof(AuthErrorCodes.PasswordNoUppercase))]
    [InlineData("PASSWORD_NO_NUMBER", nameof(AuthErrorCodes.PasswordNoNumber))]
    [InlineData("PASSWORD_HAS_SPACES", nameof(AuthErrorCodes.PasswordHasSpaces))]
    [InlineData("NICKNAME_REQUIRED", nameof(AuthErrorCodes.NicknameRequired))]
    [InlineData("NICKNAME_TOO_SHORT", nameof(AuthErrorCodes.NicknameTooShort))]
    [InlineData("NICKNAME_TOO_LONG", nameof(AuthErrorCodes.NicknameTooLong))]
    [InlineData("NICKNAME_HAS_SPACES", nameof(AuthErrorCodes.NicknameHasSpaces))]
    // v2.0 §12 avatar upload — the three codes the client used to drop into its unknown default.
    [InlineData("UPLOAD_ERROR", nameof(AuthErrorCodes.UploadError))]
    [InlineData("UPLOAD_FAILED", nameof(AuthErrorCodes.UploadFailed))]
    [InlineData("AVATAR_REQUIRED", nameof(AuthErrorCodes.AvatarRequired))]
    // v2.0 §20 api/contact.
    [InlineData("INVALID_JSON", nameof(AuthErrorCodes.InvalidJson))]
    [InlineData("TOPIC_REQUIRED", nameof(AuthErrorCodes.TopicRequired))]
    [InlineData("TOPIC_TOO_SHORT", nameof(AuthErrorCodes.TopicTooShort))]
    [InlineData("TOPIC_TOO_LONG", nameof(AuthErrorCodes.TopicTooLong))]
    [InlineData("MESSAGE_REQUIRED", nameof(AuthErrorCodes.MessageRequired))]
    [InlineData("MESSAGE_TOO_SHORT", nameof(AuthErrorCodes.MessageTooShort))]
    [InlineData("MESSAGE_TOO_LONG", nameof(AuthErrorCodes.MessageTooLong))]
    [InlineData("RATE_LIMIT_EXCEEDED", nameof(AuthErrorCodes.RateLimitExceeded))]
    [InlineData("DAILY_LIMIT_EXCEEDED", nameof(AuthErrorCodes.DailyLimitExceeded))]
    [InlineData("DUPLICATE_MESSAGE", nameof(AuthErrorCodes.DuplicateMessage))]
    [InlineData("TERMS_REQUIRED", nameof(AuthErrorCodes.TermsRequired))]
    public void Constant_MatchesServerString(string expected, string constantName)
    {
        var actual = typeof(AuthErrorCodes)
            .GetField(constantName)!
            .GetValue(null);
        Assert.Equal(expected, actual);
    }

    // Keeps the theory above honest: a constant added without a row is exactly the silent gap
    // this file exists to prevent, and it would otherwise stay invisible (the theory only
    // knows about the codes someone remembered to list).
    [Fact]
    public void Theory_CoversEveryConstant()
    {
        var pinned = typeof(AuthErrorCodesTests)
            .GetMethod(nameof(Constant_MatchesServerString))!
            .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
            .Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).Single()[1]!)
            .ToHashSet();

        var declared = typeof(AuthErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => f.Name);

        var unpinned = declared.Where(name => !pinned.Contains(name)).ToList();

        Assert.True(
            unpinned.Count == 0,
            "AuthErrorCodes constants with no InlineData row: " + string.Join(", ", unpinned));
    }
}
