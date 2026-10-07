using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

public interface IAuthApiService
{
    // Public endpoints
    Task<ApiResponse> CheckEmailAsync(string email, string password);
    Task<ApiResponse> CheckNicknameAsync(string nickname);
    Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter);
    Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password);
    Task<ApiResponse> ForgottenPasswordAsync(string email);
    Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default);

    // Protected endpoints
    Task<ApiResponse<CustomerDto>> GetProfileAsync(string token);
    Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth);
    Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType);
    Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname);
    Task<ApiResponse> DeleteAccountAsync(string token, string password);
    Task<ApiResponse> LogoutAsync(string token);
}
