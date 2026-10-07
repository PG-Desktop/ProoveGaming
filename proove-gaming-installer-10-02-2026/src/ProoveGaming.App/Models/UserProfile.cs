using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProoveGaming.App.Models;

public partial class UserProfile : ObservableObject
{
    [ObservableProperty] private int _customerId;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private DateOnly? _dateOfBirth;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _country = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private DateOnly? _joinedDate;
    [ObservableProperty] private string _sessionToken = string.Empty;
    [ObservableProperty] private DateTime? _sessionTokenExpiresAt;
    [ObservableProperty] private string? _avatarUrl;
}
