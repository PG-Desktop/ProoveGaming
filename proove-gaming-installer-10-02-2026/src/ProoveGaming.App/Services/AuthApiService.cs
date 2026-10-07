using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

public sealed class AuthApiService : IAuthApiService
{
    private readonly ApiHttpClient _api;

    public AuthApiService(ApiHttpClient api)
    {
        _api = api;
    }

    // Public endpoints

    public Task<ApiResponse> CheckEmailAsync(string email, string password) =>
        _api.PostAsync<ApiResponse>("api/check_email", new { email, password });

    public Task<ApiResponse> CheckNicknameAsync(string nickname) =>
        _api.PostAsync<ApiResponse>("api/check_nickname", new { nickname });

    public Task<ApiResponse<CustomerDto>> RegisterAsync(
        string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) =>
        _api.PostAsync<ApiResponse<CustomerDto>>("api/register", new
        {
            email,
            password,
            nickname,
            agree_terms = agreeTerms,
            agree_newsletter = agreeNewsletter
        });

    public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) =>
        _api.PostAsync<ApiResponse<CustomerDto>>("api/login", new { email, password });

    public Task<ApiResponse> ForgottenPasswordAsync(string email) =>
        _api.PostAsync<ApiResponse>("api/forgotten", new { email });

    // Works for guests too — the token is attached only when a session exists, so the
    // backend can associate the request with the account without requiring one.
    public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/contact", new { email, topic, message }, token, ct);

    // Protected endpoints

    public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) =>
        _api.PostAsync<ApiResponse<CustomerDto>>("api/profile", body: null, token: token);

    public Task<ApiResponse> UpdateProfileAsync(
        string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth) =>
        _api.PostAsync<ApiResponse>("api/profile/update", new
        {
            email = email ?? string.Empty,
            nickname = nickname ?? string.Empty,
            firstname = firstName ?? string.Empty,
            lastname = lastName ?? string.Empty,
            telephone = telephone ?? string.Empty,
            country = country ?? string.Empty,
            date_birth = dateBirth ?? string.Empty
        }, token);

    public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) =>
        _api.PostAsync<ApiResponse>("api/profile/check_nickname", new { nickname }, token);

    public Task<ApiResponse> DeleteAccountAsync(string token, string password) =>
        _api.PostAsync<ApiResponse>("api/profile/delete", new { password, confirm = true }, token);

    public Task<ApiResponse> LogoutAsync(string token) =>
        _api.PostAsync<ApiResponse>("api/logout", body: null, token: token);

    public async Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "avatar", fileName);

        return await _api.PostAsync<AvatarUploadResponse>("api/profile/avatar", content, token);
    }
}
