using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;

namespace ProoveGaming.App.ViewModels;

public partial class WelcomeViewModel : ViewModelBase, IDisposable
{
	// ── Navigation events ─────────────────────────────────────────────────
	public event Action? SignInSucceeded;
	public event Action<IReadOnlyList<string>>? SignUpSucceeded;
	public event Action<LegalDocument>? AgreementTermsRequested;
	public event Action? GuestContinueRequested;

	private readonly IAuthApiService _authApi;
	private readonly UserProfile _userProfile;
	private readonly LocalStorageService _localStorage;
	private readonly NotificationService _notification;

	public WelcomeViewModel(
		IAuthApiService authApi,
		UserProfile userProfile,
		LocalStorageService localStorage,
		NotificationService notification)
	{
		_authApi = authApi;
		_userProfile = userProfile;
		_localStorage = localStorage;
		_notification = notification;

		var last = _localStorage.GetLastSignIn();
		if (last.HasValue && !string.IsNullOrEmpty(last.Value.Email))
		{
			SignInEmail = last.Value.Email;
			IsRememberMeChecked = last.Value.RememberMe;
			IsSignIn = true;
		}

		var until = _localStorage.GetLockoutUntil();
		if (until.HasValue)
		{
			var remainingSeconds = (int)Math.Ceiling((until.Value - DateTime.UtcNow).TotalSeconds);
			if (remainingSeconds > 0)
			{
				_remainingAttempts = 0;
				UsedAttempts = MaxAttempts;
				AttemptsBlockVisible = true;
				_countdownSeconds = remainingSeconds;
				BeginCountdown();
				IsSignIn = true;
			}
			else
			{
				EnqueueStorageWrite(_localStorage.ClearLockout);
			}
		}
	}

	// Writes go to the storage executor, never inline on the UI thread
	// (docs/plans/fix-repository-audit-findings, A4). FIFO keeps a save followed by a clear in
	// order, and the App drains the queue on exit, so a lockout saved just before closing still
	// lands. The two reads in the constructor stay synchronous on purpose: they decide the first
	// screen, and a lockout read that arrived late would let a sign-in start before it applied.
	// A faulted write surfaces through AppResilience's unobserved-task logging.
	private void EnqueueStorageWrite(Action write) => _ = _localStorage.RunOnStorageAsync(write);

	// ── Tab state ─────────────────────────────────────────────────────────
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSignInPanelVisible))]
	[NotifyPropertyChangedFor(nameof(IsSignUpPanelVisible))]
	private bool _isSignIn;

	// ── Forgot-password state ─────────────────────────────────────────────
	// Third mutually-exclusive panel of the Welcome screen. When IsForgotPassword is true
	// the sign-in and sign-up panels are hidden; IsForgotSent flips the sub-view from the
	// email input to the "check your email" confirmation.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSignInPanelVisible))]
	[NotifyPropertyChangedFor(nameof(IsSignUpPanelVisible))]
	private bool _isForgotPassword;

	[ObservableProperty] private bool _isForgotSent;
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SendPasswordResetCommand))]
	private string _forgotEmail = string.Empty;
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SendPasswordResetCommand))]
	private bool _isForgotSubmitting;
	[ObservableProperty] private bool _forgotEmailError;
	[ObservableProperty] private string _forgotApiError = string.Empty;

	// Panel visibility gates — SignIn/SignUp hide during the forgot-password step so all
	// three siblings inside the panel Grid are mutually exclusive.
	public bool IsSignInPanelVisible => IsSignIn && !IsForgotPassword;
	public bool IsSignUpPanelVisible => !IsSignIn && !IsForgotPassword;

	// ── Sign-in inputs ────────────────────────────────────────────────────
	[ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SignInCommand))] private string _signInEmail = string.Empty;
	[ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SignInCommand))] private string _signInPassword = string.Empty;

	// ── Sign-up inputs ────────────────────────────────────────────────────
	[ObservableProperty] [NotifyPropertyChangedFor(nameof(SignUpButtonEnabled))] private string _signUpEmail = string.Empty;
	[ObservableProperty] [NotifyPropertyChangedFor(nameof(SignUpButtonEnabled))] private string _signUpPassword = string.Empty;
	[ObservableProperty] [NotifyPropertyChangedFor(nameof(SignUpButtonEnabled))] private bool _isTermsChecked;
	[ObservableProperty] private bool _isRememberMeChecked;
	[ObservableProperty] private bool _isMarketingChecked;

	private bool _isSignUpLoading;
	public bool SignUpButtonEnabled =>
		!_isSignUpLoading &&
		!string.IsNullOrEmpty(SignUpEmail) &&
		!string.IsNullOrEmpty(SignUpPassword) &&
		IsTermsChecked;

	// ── Validation error flags ────────────────────────────────────────────
	[ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SignInCommand))] private bool _signInEmailError;
	[ObservableProperty] private bool _signInCredentialsError;
	[ObservableProperty] private string _signInApiError = string.Empty;
	[ObservableProperty] private bool _signUpEmailError;
	[ObservableProperty] private string _signUpEmailErrorMessage = string.Empty;
	[ObservableProperty] private bool _signUpPasswordError;
	[ObservableProperty] private string _signUpPasswordErrorMessage = string.Empty;
	[ObservableProperty] private bool _termsError;
	[ObservableProperty] private string _signUpApiError = string.Empty;

	// ── Password visibility ───────────────────────────────────────────────
	[ObservableProperty] private bool _isSignInPasswordVisible;
	[ObservableProperty] private bool _isSignUpPasswordVisible;

	// ── Lockout / attempts ────────────────────────────────────────────────
	[ObservableProperty] private bool _attemptsBlockVisible;
	[ObservableProperty] private string _attemptsText = string.Empty;
	[ObservableProperty] private int _usedAttempts;

	private bool _isLockedOut;
	public bool SignInButtonEnabled =>
		!_isLockedOut &&
		!SignInEmailError &&
		!string.IsNullOrEmpty(SignInEmail) &&
		!string.IsNullOrEmpty(SignInPassword);

	private int _remainingAttempts = MaxAttempts;
	private int _countdownSeconds;
	private DispatcherTimer? _countdownTimer;

	private const int MaxAttempts = 10;
	private const int LockoutSeconds = 10 * 60;

	// ── Tab commands ──────────────────────────────────────────────────────

	[RelayCommand]
	private void SignInTab()
	{
		if (IsSignIn && !IsForgotPassword) return;
		ExitForgotPassword();
		IsSignIn = true;
	}

	[RelayCommand]
	private void SignUpTab()
	{
		if (!IsSignIn && !IsForgotPassword) return;
		ExitForgotPassword();
		IsSignIn = false;
	}

	// ── Password visibility commands ──────────────────────────────────────

	[RelayCommand]
	private void ToggleSignInPassword() => IsSignInPasswordVisible = !IsSignInPasswordVisible;

	[RelayCommand]
	private void ToggleSignUpPassword() => IsSignUpPasswordVisible = !IsSignUpPasswordVisible;

	// ── Validation commands ───────────────────────────────────────────────

	[RelayCommand]
	private void ValidateSignInEmail()
	{
		if (!string.IsNullOrEmpty(SignInEmail))
			SignInEmailError = !EmailRegex.IsMatch(SignInEmail);
	}

	[RelayCommand]
	private void ClearSignInEmailError() => SignInEmailError = false;

	[RelayCommand]
	private void ClearSignInPasswordError()
	{
		SignInCredentialsError = false;
		SignInApiError = string.Empty;
	}

	[RelayCommand]
	private void ValidateSignUpEmail()
	{
		if (string.IsNullOrEmpty(SignUpEmail)) return;
		var ok = EmailRegex.IsMatch(SignUpEmail);
		SignUpEmailError = !ok;
		SignUpEmailErrorMessage = ok ? string.Empty : LocalizationManager.GetString("WelcomeView_Error_Email");
	}

	[RelayCommand]
	private void ClearSignUpEmailError()
	{
		SignUpEmailError = false;
		SignUpEmailErrorMessage = string.Empty;
		SignUpApiError = string.Empty;
	}

	[RelayCommand]
	private void ValidateSignUpPassword()
	{
		if (string.IsNullOrEmpty(SignUpPassword)) return;
		var ok = PasswordRegex.IsMatch(SignUpPassword);
		SignUpPasswordError = !ok;
		SignUpPasswordErrorMessage = ok ? string.Empty : LocalizationManager.GetString("WelcomeView_Error_Password");
	}

	[RelayCommand]
	private void ClearSignUpPasswordError()
	{
		SignUpPasswordError = false;
		SignUpPasswordErrorMessage = string.Empty;
		SignUpApiError = string.Empty;
	}

	// ── Sign-in submit ────────────────────────────────────────────────────

	[RelayCommand(CanExecute = nameof(SignInButtonEnabled))]
	private async Task SignInAsync()
	{
		if (!SignInButtonEnabled) return;

		var result = await _authApi.LoginAsync(SignInEmail, SignInPassword);

		if (result.Success)
		{
			SignInCredentialsError = false;
			SignInApiError = string.Empty;
			if (result.Data != null)
			{
				_userProfile.CustomerId = result.Data.CustomerId;
				_userProfile.Email = result.Data.Email;
				_userProfile.DisplayName = result.Data.Nickname;
			}
			_userProfile.SessionToken = result.Token ?? string.Empty;
			_userProfile.SessionTokenExpiresAt = DateTime.UtcNow.AddDays(result.TokenExpiresInDays ?? 30);
			var (email, rememberMe) = (SignInEmail, IsRememberMeChecked);
			EnqueueStorageWrite(() => _localStorage.SaveLastSignIn(email, rememberMe));
			ResetAttempts();
			SignInSucceeded?.Invoke();
		}
		else if (result.IsNetworkError)
		{
			SignInApiError = ApiErrorMessages.Describe(result, "Api_Error_Network");
		}
		else
		{
			SignInApiError = string.Empty;
			SignInCredentialsError = true;
			if (_remainingAttempts > 0)
				_remainingAttempts--;
			UpdateAttemptsBlock();
		}
	}

	// ── Sign-up submit ────────────────────────────────────────────────────

	[RelayCommand]
	private async Task SignUpAsync()
	{
		var emailValid = !string.IsNullOrEmpty(SignUpEmail) && EmailRegex.IsMatch(SignUpEmail);
		var passwordValid = !string.IsNullOrEmpty(SignUpPassword) && PasswordRegex.IsMatch(SignUpPassword);

		SignUpEmailError = !emailValid;
		if (!emailValid)
			SignUpEmailErrorMessage = LocalizationManager.GetString("WelcomeView_Error_Email");
		SignUpPasswordError = !passwordValid;
		if (!passwordValid)
			SignUpPasswordErrorMessage = LocalizationManager.GetString("WelcomeView_Error_Password");
		TermsError = !IsTermsChecked;

		if (!emailValid || !passwordValid || !IsTermsChecked)
			return;

		SignUpApiError = string.Empty;
		SignUpEmailErrorMessage = string.Empty;
		SignUpPasswordErrorMessage = string.Empty;
		_isSignUpLoading = true;
		OnPropertyChanged(nameof(SignUpButtonEnabled));

		try
		{
			var result = await _authApi.CheckEmailAsync(SignUpEmail, SignUpPassword);
			if (!result.Success)
			{
				if (TryApplySignUpFieldErrors(result))
					return;
				SignUpApiError = ApiErrorMessages.Describe(result, "Api_Error_Generic");
				return;
			}

			SignUpSucceeded?.Invoke((IReadOnlyList<string>?)result.Nicknames ?? Array.Empty<string>());
		}
		finally
		{
			_isSignUpLoading = false;
			OnPropertyChanged(nameof(SignUpButtonEnabled));
		}
	}

	// Maps v1.1 spec error_code values onto the appropriate field-level error UI.
	// Returns true if any error was handled at field level (caller skips the generic banner).
	private bool TryApplySignUpFieldErrors(ApiResponse result)
	{
		var handled = false;
		foreach (var (field, message) in ApiErrorMessages.ResolveAll(result))
		{
			switch (field)
			{
				case AuthField.Email:
					SignUpEmailError = true;
					SignUpEmailErrorMessage = message;
					handled = true;
					break;
				case AuthField.Password:
					SignUpPasswordError = true;
					SignUpPasswordErrorMessage = message;
					handled = true;
					break;
				case AuthField.Terms:
					TermsError = true;
					handled = true;
					break;
			}
		}
		return handled;
	}

	// ── Navigation commands ───────────────────────────────────────────────

	[RelayCommand]
	private void OpenPrivacyPolicy() => AgreementTermsRequested?.Invoke(LegalDocument.PrivacyPolicy);

	[RelayCommand]
	private void OpenTermsOfService() => AgreementTermsRequested?.Invoke(LegalDocument.TermsOfService);

	[RelayCommand]
	private void OpenLicenseAgreement() => AgreementTermsRequested?.Invoke(LegalDocument.LicenseAgreement);

	[RelayCommand]
	private void GuestContinue() => GuestContinueRequested?.Invoke();

	// ── Forgot-password commands ──────────────────────────────────────────

	// Opens the in-app forgot-password step from the sign-in tab. Seeds the email input
	// with SignInEmail so the common case (user typed their email, then remembered they
	// forgot the password) requires zero re-typing.
	[RelayCommand]
	private void EnterForgotPassword()
	{
		ForgotEmail = string.IsNullOrEmpty(SignInEmail) ? string.Empty : SignInEmail;
		ForgotEmailError = false;
		ForgotApiError = string.Empty;
		IsForgotSent = false;
		IsForgotSubmitting = false;
		IsSignIn = false;
		IsForgotPassword = true;
	}

	[RelayCommand]
	private void BackToSignIn()
	{
		ExitForgotPassword();
		IsSignIn = true;
	}

	[RelayCommand(CanExecute = nameof(CanSendPasswordReset))]
	private async Task SendPasswordResetAsync()
	{
		ForgotApiError = string.Empty;
		if (!EmailRegex.IsMatch(ForgotEmail))
		{
			ForgotEmailError = true;
			return;
		}
		ForgotEmailError = false;
		IsForgotSubmitting = true;
		try
		{
			ApiResponse response;
			try
			{
				response = await _authApi.ForgottenPasswordAsync(ForgotEmail);
			}
			catch
			{
				ForgotApiError = LocalizationManager.GetString("WelcomeView_Forgot_Error_Generic");
				return;
			}

			if (response.Success)
			{
				IsForgotSent = true;
				_notification.Show(LocalizationManager.GetString("WelcomeView_Notification_ResetLinkSent"));
				return;
			}

			var (isFieldError, message) = ForgottenPasswordErrors.Resolve(response);
			ForgotEmailError = isFieldError;
			ForgotApiError = message;
		}
		finally
		{
			IsForgotSubmitting = false;
		}
	}

	private bool CanSendPasswordReset() =>
		!IsForgotSubmitting && !string.IsNullOrEmpty(ForgotEmail);

	// Resets forgot-panel state so a subsequent entry starts on the input step.
	private void ExitForgotPassword()
	{
		IsForgotPassword = false;
		IsForgotSent = false;
		ForgotEmailError = false;
		ForgotApiError = string.Empty;
	}

	// ── Lockout logic ─────────────────────────────────────────────────────

	private void UpdateAttemptsBlock()
	{
		AttemptsBlockVisible = true;
		UsedAttempts = MaxAttempts - _remainingAttempts;

		if (_remainingAttempts == 0 && _countdownTimer == null)
			StartCountdown();
		else
			AttemptsText = FormatAttemptsText();
	}

	private void StartCountdown()
	{
		_countdownSeconds = LockoutSeconds;
		var lockoutUntil = DateTime.UtcNow.AddSeconds(LockoutSeconds);
		EnqueueStorageWrite(() => _localStorage.SaveLockoutUntil(lockoutUntil));
		BeginCountdown();
	}

	private void BeginCountdown()
	{
		_isLockedOut = true;
		SignInCommand.NotifyCanExecuteChanged();
		AttemptsText = FormatAttemptsText();

		_countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_countdownTimer.Tick += OnCountdownTick;
		_countdownTimer.Start();
	}

	private void OnCountdownTick(object? sender, EventArgs e)
	{
		_countdownSeconds--;
		AttemptsText = FormatAttemptsText();

		if (_countdownSeconds > 0) return;

		ResetAttempts();
	}

	/// <summary>Clears the failed-attempts counter, lockout state, and any running countdown.</summary>
	private void ResetAttempts()
	{
		if (_countdownTimer != null)
		{
			_countdownTimer.Stop();
			_countdownTimer.Tick -= OnCountdownTick;
			_countdownTimer = null;
		}
		_remainingAttempts = MaxAttempts;
		UsedAttempts = 0;
		AttemptsBlockVisible = false;
		AttemptsText = string.Empty;
		_isLockedOut = false;
		EnqueueStorageWrite(_localStorage.ClearLockout);
		SignInCommand.NotifyCanExecuteChanged();
	}

	private string FormatAttemptsText()
	{
		if (_remainingAttempts > 0)
			return string.Format(LocalizationManager.GetString("WelcomeView_Attempts_Left"), _remainingAttempts);

		var mins = _countdownSeconds / 60;
		var secs = _countdownSeconds % 60;
		return string.Format(LocalizationManager.GetString("WelcomeView_Attempts_Locked"), $"{mins:D2}", $"{secs:D2}");
	}

	// ── Logout reset ──────────────────────────────────────────────────────

	public void PrepareForFreshSignIn()
	{
		SignInEmail = string.Empty;
		SignInPassword = string.Empty;
		SignInEmailError = false;
		SignInCredentialsError = false;
		SignInApiError = string.Empty;
		IsSignInPasswordVisible = false;

		SignUpEmail = string.Empty;
		SignUpPassword = string.Empty;
		SignUpEmailError = false;
		SignUpEmailErrorMessage = string.Empty;
		SignUpPasswordError = false;
		SignUpPasswordErrorMessage = string.Empty;
		SignUpApiError = string.Empty;
		IsTermsChecked = false;
		IsMarketingChecked = false;
		TermsError = false;
		IsSignUpPasswordVisible = false;

		ForgotEmail = string.Empty;
		ForgotEmailError = false;
		ForgotApiError = string.Empty;
		IsForgotSent = false;
		IsForgotSubmitting = false;
		IsForgotPassword = false;

		ResetAttempts();

		IsSignIn = true;
	}

	// ── Disposal ──────────────────────────────────────────────────────────

	public void Dispose()
	{
		if (_countdownTimer == null) return;
		_countdownTimer.Stop();
		_countdownTimer.Tick -= OnCountdownTick;
		_countdownTimer = null;
	}

	private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
	// Client-side gate is deliberately loose: 8+ printable ASCII, no spaces. The server
	// (v1.6 spec §1) is the source of truth for uppercase/digit rules and returns
	// PASSWORD_NO_UPPERCASE / PASSWORD_NO_NUMBER / PASSWORD_HAS_SPACES / PASSWORD_TOO_SHORT
	// — those codes are mapped to inline errors in ApiErrorMessages. Stricter client rules
	// wrongly reject server-issued reset passwords (which can be lowercase-only).
	private static readonly Regex PasswordRegex = new(@"^[\x21-\x7E]{8,}$", RegexOptions.Compiled);
}
