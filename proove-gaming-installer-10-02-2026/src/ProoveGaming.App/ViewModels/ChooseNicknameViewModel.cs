using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Services;

namespace ProoveGaming.App.ViewModels;

public partial class ChooseNicknameViewModel : ViewModelBase
{
	public event Action? BackRequested;
	public event Action<string>? NicknameChosen;

	private readonly IAuthApiService _authApi;
	private bool _isLoading;

	private static readonly Random Rng = new();

	// Populated by SetSuggestionsFromServer with the `nicknames` list from `api/check_email`.
	private string[] _pool = [];

	public ChooseNicknameViewModel(IAuthApiService authApi)
	{
		_authApi = authApi;
	}

	[ObservableProperty] [NotifyPropertyChangedFor(nameof(NextEnabled))] private string _nickname = string.Empty;
	[ObservableProperty] private bool _nicknameError;
	[ObservableProperty] private string _nicknameErrorMessage = string.Empty;
	[ObservableProperty] private bool _nicknameSuccess;

	[ObservableProperty] private string _suggestion1 = string.Empty;
	[ObservableProperty] private string _suggestion2 = string.Empty;
	[ObservableProperty] private string _suggestion3 = string.Empty;

	public bool NextEnabled => !_isLoading && !string.IsNullOrWhiteSpace(Nickname);

	partial void OnNicknameChanged(string value)
	{
		NicknameError = false;
		NicknameErrorMessage = string.Empty;
		NicknameSuccess = false;
	}

	[RelayCommand]
	private void Back() => BackRequested?.Invoke();

	[RelayCommand]
	private async Task NextAsync()
	{
		var nickname = Nickname.Trim();

		var localCode = ValidateNicknameLocal(nickname);
		if (localCode != null)
		{
			ApplyLocalNicknameError(localCode);
			return;
		}

		_isLoading = true;
		OnPropertyChanged(nameof(NextEnabled));

		try
		{
			var result = await _authApi.CheckNicknameAsync(nickname);
			if (!result.Success)
			{
				ApplyNicknameError(result);
				return;
			}

			NicknameChosen?.Invoke(nickname);
		}
		finally
		{
			_isLoading = false;
			OnPropertyChanged(nameof(NextEnabled));
		}
	}

	private static readonly Regex NicknameHasSpace = new(@"\s", RegexOptions.Compiled);
	private static readonly Regex NicknameAllowedChars = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

	private static string? ValidateNicknameLocal(string nickname)
	{
		if (string.IsNullOrEmpty(nickname)) return "NICKNAME_REQUIRED";
		if (NicknameHasSpace.IsMatch(nickname)) return "NICKNAME_HAS_SPACES";
		if (nickname.Length < 3) return "NICKNAME_TOO_SHORT";
		if (nickname.Length > 20) return "NICKNAME_TOO_LONG";
		if (!NicknameAllowedChars.IsMatch(nickname)) return "NICKNAME_INVALID_CHARACTERS";
		return null;
	}

	private void ApplyLocalNicknameError(string code)
	{
		var msg = ApiErrorMessages.Resolve(code)?.Message
			?? LocalizationManager.GetString("Auth_Error_NicknameRequired");
		NicknameError = true;
		NicknameErrorMessage = msg;
		NicknameSuccess = false;
	}

	[RelayCommand]
	private void ClearNicknameError()
	{
		NicknameError = false;
		NicknameErrorMessage = string.Empty;
	}

	internal void ApplyNicknameError(ApiResponse result)
	{
		string? message = null;
		foreach (var (field, msg) in ApiErrorMessages.ResolveAll(result))
		{
			if (field == AuthField.Nickname) { message = msg; break; }
		}
		NicknameError = true;
		NicknameErrorMessage = message ?? ApiErrorMessages.Describe(result, "Auth_Error_NicknameRequired");
		NicknameSuccess = false;
	}

	[RelayCommand]
	private void Refresh() => RefreshSuggestions();

	[RelayCommand]
	private void SelectSuggestion(string suggestion)
	{
		Nickname = suggestion;
		NicknameSuccess = true;
	}

	/// <summary>Seed the suggestion pool from the server's `check_email` response.</summary>
	public void SetSuggestionsFromServer(IReadOnlyList<string>? nicknames)
	{
		_pool = nicknames is { Count: > 0 } ? nicknames.ToArray() : [];
		RefreshSuggestions();
	}

	private void RefreshSuggestions()
	{
		var count = _pool.Length;
		if (count >= 3)
		{
			var indices = PickThree(count);
			Suggestion1 = _pool[indices[0]];
			Suggestion2 = _pool[indices[1]];
			Suggestion3 = _pool[indices[2]];
		}
		else
		{
			Suggestion1 = count > 0 ? _pool[0] : string.Empty;
			Suggestion2 = count > 1 ? _pool[1] : string.Empty;
			Suggestion3 = count > 2 ? _pool[2] : string.Empty;
		}
	}

	private static int[] PickThree(int max)
	{
		var a = Rng.Next(max);
		int b, c;
		do { b = Rng.Next(max); } while (b == a);
		do { c = Rng.Next(max); } while (c == a || c == b);
		return [a, b, c];
	}
}
