using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The one-shot upgrade push (docs/plans/add-api-v2-sync): the REAL SettingsSyncService is
// driven against a real LocalStorageService and a recording fake ISyncApiService — HTTP is
// the only boundary faked, per the repo's test policy. Tests await PushTask (the internal
// seam, same pattern as HomeViewModel.NotificationsLoaded) to observe the background push.
public class SettingsSyncServiceTests : IDisposable
{
	private const int CustomerId = 42;
	private const string OwnerId = "42";
	private const string User = LocalStorageService.OwnerKindUser;
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string NoKey = "";

	private readonly LocalStorageService _storage = new("Data Source=:memory:");
	private readonly RecordingSyncApi _api = new();
	private readonly CapturingLogger<SettingsSyncService> _log = new();

	public void Dispose() => _storage.Dispose();

	private SettingsSyncService NewService(string token = "token-1") =>
		new(_api, _storage,
			new UserProfile { CustomerId = CustomerId, SessionToken = token },
			new PresetConfigurationBuilder(_storage), new PresetConfigurationApplier(_storage),
			new NullCatalogResolver(), _log);

	// Seeds rows directly under the user scope — the push runs after MigrateGuestToUserAsync
	// has rekeyed guest rows, so post-migration state is the input the service actually sees
	// (the rekey itself is covered by the LocalStorageService uid tests).
	private void SeedDevice(string deviceKey = NoKey) =>
		_storage.AddDevice(User, OwnerId, new DeviceRecord
		{
			Name = "BLAZE",
			DeviceTypeLabel = "MOUSE",
			VendorId = Vid,
			ProductId = Pid,
			DeviceKey = deviceKey,
		});

	private MacroRecord SeedMacro(string name = "M1")
	{
		var macro = new MacroRecord
		{
			Name = name,
			RepeatCount = 2,
			Mode = MacroRepeatMode.MacroPlayCount,
			Events =
			{
				new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 10 },
				new MacroEventRecord { KeyCode = 0x04, IsPress = false, DelayMs = 20 },
			},
		};
		_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro);
		return macro;
	}

	private PresetRecord SeedPreset(string name = "P1", bool isDefault = false)
	{
		var preset = new PresetRecord { Name = name, IsDefault = isDefault };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, preset);
		return preset;
	}

	private async Task RunPushAsync(SettingsSyncService service)
	{
		await service.OnGuestUpgradedAsync(CustomerId);
		await service.PushTask;
	}

	// ── Ordering + payload ───────────────────────────────────────────────────

	[Fact]
	public async Task Push_OrdersMacrosBeforePresetsBeforePreferencesBeforeDevices()
	{
		SeedDevice();
		var m1 = SeedMacro("M1");
		var m2 = SeedMacro("M2");
		var p1 = SeedPreset("P1", isDefault: true);
		var p2 = SeedPreset("P2");
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 3);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, p1.Id);

		await RunPushAsync(NewService());

		Assert.Equal(
			new[] { "macros/create", "macros/create", "presets/create", "presets/create", "preferences/update", "devices/update" },
			_api.Endpoints);
		// Local uids travel as the sync identity.
		Assert.Equal(new[] { m1.Uid, m2.Uid }, _api.MacroRequests.Select(r => r.Uid).ToArray());
		Assert.Equal(new[] { p1.Uid, p2.Uid }, _api.PresetRequests.Select(r => r.Uid).ToArray());
		Assert.True(_api.PresetRequests[0].IsDefault);
		Assert.False(_api.PresetRequests[1].IsDefault);
		// devices/update carries the applied preset's uid.
		Assert.Equal((Vid, Pid, p1.Uid), Assert.Single(_api.DeviceRequests));
		// The preferences payload always carries the five documented keys.
		var prefs = Assert.Single(_api.PreferencesRequests);
		Assert.Equal(5, prefs.Count);
		Assert.Equal(3d, Assert.IsType<double>(prefs[SettingsRepository.PrefScrollLines]));
		Assert.IsType<bool>(prefs[SettingsRepository.PrefScrollOneScreen]);
	}

	// ── server_updated_at write-back ─────────────────────────────────────────

	[Fact]
	public async Task Push_SuccessWithUpdatedAt_WritesServerUpdatedAtBack()
	{
		SeedDevice();
		SeedMacro();
		SeedPreset();
		_api.MacroResponse = _ => Task.FromResult(new MacroCreateResponse
		{
			Success = true, StatusCode = 200,
			Macro = new SyncRecordDto { UpdatedAt = "2026-08-25 10:11:12" },
		});
		_api.PresetResponse = _ => Task.FromResult(new PresetCreateResponse
		{
			Success = true, StatusCode = 200,
			Preset = new SyncRecordDto { UpdatedAt = "2026-08-25 10:11:13" },
		});

		await RunPushAsync(NewService());

		Assert.Equal("2026-08-25 10:11:12",
			Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
		Assert.Equal("2026-08-25 10:11:13",
			Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid)).ServerUpdatedAt);
	}

	// ── *_UID_ALREADY_EXISTS tolerance ───────────────────────────────────────

	// The record is already on the server (idempotent retry semantics): the push continues to
	// the later stages and logs nothing at Warning or above. One code arrives as the top-level
	// error_code, the other inside errors[] — the service accepts both envelope shapes.
	[Fact]
	public async Task Push_UidAlreadyExists_CountsAsSuccessAndContinues()
	{
		SeedDevice();
		SeedMacro();
		var preset = SeedPreset();
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, preset.Id);
		_api.MacroResponse = _ => Task.FromResult(new MacroCreateResponse
		{
			Success = false, StatusCode = 409, ErrorCode = "MACRO_UID_ALREADY_EXISTS",
		});
		_api.PresetResponse = _ => Task.FromResult(new PresetCreateResponse
		{
			Success = false, StatusCode = 409,
			Errors = [new ApiErrorEntry { ErrorCode = "PRESET_UID_ALREADY_EXISTS" }],
		});

		await RunPushAsync(NewService());

		Assert.Equal(new[] { "macros/create", "presets/create", "devices/update" }, _api.Endpoints);
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
		// ALREADY_EXISTS echoes no record, so nothing is written back.
		Assert.Null(Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
		Assert.Null(Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid)).ServerUpdatedAt);
	}

	// ── Malformed-2xx success path ───────────────────────────────────────────

	// The PHP backend's shape quirks (e.g. "macro": [] where an object is documented) make a
	// 2xx body fail to bind: Success=false but MalformedPayload=true. Per the plan's risk
	// note that counts as success — the push continues to the later stages and logs nothing
	// at Warning or above — but no server_updated_at is written back because no updated_at
	// was readable from the body. (Review Round 1, finding S1.)
	[Fact]
	public async Task Push_Malformed2xxOnCreate_CountsAsSuccessWithoutWriteBack()
	{
		SeedDevice();
		SeedMacro();
		var preset = SeedPreset();
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, preset.Id);
		_api.MacroResponse = _ => Task.FromResult(new MacroCreateResponse
		{
			Success = false, StatusCode = 200, MalformedPayload = true,
		});
		_api.PresetResponse = _ => Task.FromResult(new PresetCreateResponse
		{
			Success = false, StatusCode = 200, MalformedPayload = true,
		});

		await RunPushAsync(NewService());

		Assert.Equal(new[] { "macros/create", "presets/create", "devices/update" }, _api.Endpoints);
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
		// The body never bound, so there is no updated_at to write back.
		Assert.Null(Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
		Assert.Null(Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid)).ServerUpdatedAt);
	}

	// ── No-token early exit ──────────────────────────────────────────────────

	// A registration that somehow yields no session token cannot push anything: the method
	// exits before scheduling a push (PushTask stays completed) and no API call is made.
	[Fact]
	public async Task OnGuestUpgradedAsync_EmptySessionToken_MakesZeroApiCalls()
	{
		SeedDevice();
		SeedMacro();
		var service = NewService(token: "");

		await service.OnGuestUpgradedAsync(CustomerId);

		Assert.True(service.PushTask.IsCompleted);
		Assert.Empty(_api.Endpoints);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("skipped"));
	}

	// ── PushTask replacement diagnostics ─────────────────────────────────────

	// A second guest→register cycle in one session replaces the seam's task while the first
	// push may still be running; the first push keeps running to completion, and the
	// replacement is logged at Information so the lost observability is diagnosable.
	[Fact]
	public async Task OnGuestUpgradedAsync_SecondCallWhileFirstPushRuns_LogsReplacementAtInformation()
	{
		SeedDevice();
		SeedMacro();
		using var gate = new SemaphoreSlim(0);
		_api.MacroResponse = async _ =>
		{
			await gate.WaitAsync();
			return new MacroCreateResponse { Success = true, StatusCode = 200 };
		};

		var service = NewService();
		await service.OnGuestUpgradedAsync(CustomerId);
		var first = service.PushTask;
		// The first push is parked on the gate, so it cannot have completed yet.
		await service.OnGuestUpgradedAsync(CustomerId);

		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Information && e.Message.Contains("previous push is still running"));

		gate.Release(2);
		await first;
		await service.PushTask;
	}

	// ── Network-error abort ──────────────────────────────────────────────────

	[Fact]
	public async Task Push_NetworkErrorOnFirstCall_AbortsWithoutFurtherCallsOrThrow()
	{
		SeedDevice();
		SeedMacro();
		var preset = SeedPreset();
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 3);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, preset.Id);
		_api.MacroResponse = _ => Task.FromResult(new MacroCreateResponse { IsNetworkError = true });

		var service = NewService();
		var returned = service.OnGuestUpgradedAsync(CustomerId);
		// Completing both awaits without throwing IS the "never surfaces" assertion.
		await returned;
		await service.PushTask;

		Assert.Equal(new[] { "macros/create" }, _api.Endpoints);
	}

	// ── Token-rejection quiescence ───────────────────────────────────────────

	// docs/plans/add-global-token-expiry: a token-rejection envelope mid-push aborts the
	// pipeline at the rejected item (same policy as a network error — Aborts) so no later
	// stage makes a doomed call with the dead token; the abort warns, nothing throws or logs
	// at Error. The same response trips ApiHttpClient.TokenRejected, whose MainWindow handler
	// clears the signed-in state — observable here as the profile token emptying — and from
	// then on the no-token guard makes every later push attempt a zero-call no-op.
	[Fact]
	public async Task Push_TokenRejectedMidPush_AbortsAndNoPushRunsAfterTokenCleared()
	{
		SeedDevice();
		SeedMacro();
		SeedPreset();
		var profile = new UserProfile { CustomerId = CustomerId, SessionToken = "token-1" };
		var service = new SettingsSyncService(
			_api, _storage, profile, new PresetConfigurationBuilder(_storage),
			new PresetConfigurationApplier(_storage), new NullCatalogResolver(), _log);
		_api.MacroResponse = _ =>
		{
			profile.SessionToken = "";
			return Task.FromResult(new MacroCreateResponse
			{
				Success = false, StatusCode = 401,
				ErrorCode = AuthErrorCodes.InvalidOrExpiredToken,
			});
		};

		await service.OnGuestUpgradedAsync(CustomerId);
		await service.PushTask;

		// The rejected item is the last call the push makes — the abort warns, no error-level noise.
		Assert.Equal(new[] { "macros/create" }, _api.Endpoints);
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("aborted at macros/create"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);

		// Quiescence: with the token cleared, a later push attempt makes zero further calls.
		await service.OnGuestUpgradedAsync(CustomerId);
		await service.PushTask;
		Assert.Single(_api.Endpoints);
	}

	// ── No data → no calls ───────────────────────────────────────────────────

	[Fact]
	public async Task Push_NoLocalData_MakesZeroApiCalls()
	{
		await RunPushAsync(NewService());

		Assert.Empty(_api.Endpoints);
	}

	// ── Synchronous-return criterion ─────────────────────────────────────────

	// OnGuestUpgradedAsync must complete without waiting for any storage read or HTTP call:
	// the fake blocks the first API call on a gate, and the method has already returned —
	// with the push visibly still in flight — before the gate opens.
	[Fact]
	public async Task OnGuestUpgradedAsync_ReturnsWhileTheFirstApiCallIsStillBlocked()
	{
		SeedDevice();
		SeedMacro();
		using var gate = new SemaphoreSlim(0);
		_api.MacroResponse = async _ =>
		{
			await gate.WaitAsync();
			return new MacroCreateResponse { Success = true, StatusCode = 200 };
		};

		var service = NewService();
		var returned = service.OnGuestUpgradedAsync(CustomerId);

		Assert.True(returned.IsCompleted);
		// The push cannot have finished: its only macro call is parked on the gate.
		Assert.False(service.PushTask.IsCompleted);

		gate.Release();
		await service.PushTask;
		Assert.Equal(new[] { "macros/create" }, _api.Endpoints);
	}

	// ── Per-unit applied-preset collapse ─────────────────────────────────────

	// API v2.0's devices/update keys on VID/PID only (plan Open question 4): two physical
	// units with different applied presets collapse to ONE devices/update — the first row by
	// device_key ordinal wins — and the loss is logged as a warning.
	[Fact]
	public async Task Push_TwoUnitsWithDifferentAppliedPresets_SendsOneDevicesUpdateAndWarns()
	{
		SeedDevice("unit-a");
		SeedDevice("unit-b");
		var a = SeedPreset("A");
		var b = SeedPreset("B");
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, "unit-a", a.Id);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, "unit-b", b.Id);

		await RunPushAsync(NewService());

		Assert.Equal((Vid, Pid, a.Uid), Assert.Single(_api.DeviceRequests));
		// The warning names the winning preset by uid and name so the log line is actionable
		// against the server (review Round 1 fix pass).
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning
			&& e.Message.Contains("disagree") && e.Message.Contains(a.Uid) && e.Message.Contains("('A')"));
	}

	// The same collapse rule the coordinator's device-state pass uses
	// (ChooseAppliedPresetRow, shared since the PR #120 review's M1): a row under a real unit
	// key is an explicit choice made on this machine and outranks the unit-agnostic '' row,
	// which a sign-in pull may have left behind for the whole device type. Ordering alone
	// would pick '' — it sorts first — and push a stale preset.
	[Fact]
	public async Task Push_SharedScopeRowAndUnitKeyedRow_SendsTheUnitKeyedPreset()
	{
		SeedDevice(NoKey);
		SeedDevice("unit-a");
		var shared = SeedPreset("SHARED");
		var unit = SeedPreset("UNIT");
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, shared.Id);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, "unit-a", unit.Id);

		await RunPushAsync(NewService());

		Assert.Equal((Vid, Pid, unit.Uid), Assert.Single(_api.DeviceRequests));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning
			&& e.Message.Contains("disagree") && e.Message.Contains(unit.Uid) && e.Message.Contains("('UNIT')"));
	}

	// ── Test doubles ─────────────────────────────────────────────────────────

	// Records every call in arrival order (the push is sequential by design, so plain lists
	// suffice) and answers from swappable factories; defaults to a bare success envelope with
	// no echoed record, i.e. no updated_at write-back.
	private sealed class RecordingSyncApi : ISyncApiService
	{
		public List<string> Endpoints { get; } = [];
		public List<MacroCreateRequest> MacroRequests { get; } = [];
		public List<PresetCreateRequest> PresetRequests { get; } = [];
		public List<Dictionary<string, object>> PreferencesRequests { get; } = [];
		public List<(int VendorId, int ProductId, string? ActivePresetUid)> DeviceRequests { get; } = [];

		public Func<MacroCreateRequest, Task<MacroCreateResponse>> MacroResponse { get; set; } =
			_ => Task.FromResult(new MacroCreateResponse { Success = true, StatusCode = 200 });

		public Func<PresetCreateRequest, Task<PresetCreateResponse>> PresetResponse { get; set; } =
			_ => Task.FromResult(new PresetCreateResponse { Success = true, StatusCode = 200 });

		public Task<MacroCreateResponse> CreateMacroAsync(string token, MacroCreateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("macros/create");
			MacroRequests.Add(request);
			return MacroResponse(request);
		}

		public Task<PresetCreateResponse> CreatePresetAsync(string token, PresetCreateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("presets/create");
			PresetRequests.Add(request);
			return PresetResponse(request);
		}

		public Task<PreferencesUpdateResponse> UpdatePreferencesAsync(string token, Dictionary<string, object> preferences, string? updatedAt = null, CancellationToken ct = default)
		{
			Endpoints.Add("preferences/update");
			PreferencesRequests.Add(preferences);
			return Task.FromResult(new PreferencesUpdateResponse { Success = true, StatusCode = 200 });
		}

		public Task<DeviceStateUpdateResponse> UpdateDeviceStateAsync(string token, int vendorId, int productId, string? activePresetUid, string? updatedAt = null, CancellationToken ct = default)
		{
			Endpoints.Add("devices/update");
			DeviceRequests.Add((vendorId, productId, activePresetUid));
			return Task.FromResult(new DeviceStateUpdateResponse { Success = true, StatusCode = 200 });
		}

		// Live-sync endpoints (docs/plans/add-api-v2-live-sync) — the upgrade-push tests
		// never reach them; recorded so an accidental call shows up in an order assertion.
		public Task<MacroCreateResponse> UpdateMacroAsync(string token, MacroUpdateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("macros/update");
			return Task.FromResult(new MacroCreateResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeleteMacroAsync(string token, string uid, string? updatedAt, CancellationToken ct = default)
		{
			Endpoints.Add("macros/delete");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<PresetCreateResponse> UpdatePresetAsync(string token, PresetUpdateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("presets/update");
			return Task.FromResult(new PresetCreateResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeletePresetAsync(string token, string uid, string? updatedAt, CancellationToken ct = default)
		{
			Endpoints.Add("presets/delete");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		// Pull endpoints (docs/plans/add-api-v2-pull) — the push tests never reach them;
		// defaults answer "empty account" so an accidental call is a harmless no-op.
		public Func<Task<MacroListResponse>> MacroListResponse { get; set; } =
			() => Task.FromResult(new MacroListResponse { Success = true, StatusCode = 200, Macros = [] });

		public Func<string, Task<MacroGetResponse>> MacroGetResponse { get; set; } =
			_ => Task.FromResult(new MacroGetResponse { Success = true, StatusCode = 200 });

		public Func<Task<PresetListResponse>> PresetListResponse { get; set; } =
			() => Task.FromResult(new PresetListResponse { Success = true, StatusCode = 200, Presets = [] });

		public Func<string, Task<PresetGetResponse>> PresetGetResponse { get; set; } =
			_ => Task.FromResult(new PresetGetResponse { Success = true, StatusCode = 200 });

		public Func<Task<PreferencesGetResponse>> PreferencesGetResponse { get; set; } =
			() => Task.FromResult(new PreferencesGetResponse { Success = true, StatusCode = 200 });

		public Func<int, int, Task<DeviceStateGetResponse>> DeviceStateGetResponse { get; set; } =
			(_, _) => Task.FromResult(new DeviceStateGetResponse { Success = true, StatusCode = 200 });

		public Task<MacroListResponse> ListMacrosAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("macros/list");
			return MacroListResponse();
		}

		public Task<MacroGetResponse> GetMacroAsync(string token, string uid, CancellationToken ct = default)
		{
			Endpoints.Add("macros/get");
			return MacroGetResponse(uid);
		}

		public Task<PresetListResponse> ListPresetsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("presets/list");
			return PresetListResponse();
		}

		public Task<PresetGetResponse> GetPresetAsync(string token, string uid, CancellationToken ct = default)
		{
			Endpoints.Add("presets/get");
			return PresetGetResponse(uid);
		}

		public Task<PreferencesGetResponse> GetPreferencesAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("preferences/get");
			return PreferencesGetResponse();
		}

		public Task<DeviceStateGetResponse> GetDeviceStateAsync(string token, int vendorId, int productId, CancellationToken ct = default)
		{
			Endpoints.Add("devices/get");
			return DeviceStateGetResponse(vendorId, productId);
		}

		// Device roster (docs/plans/add-api-v21-device-roster) — the push tests never reach
		// them; the list default's null Devices makes the pull's roster step a no-op.
		public Task<ApiResponse> AddDeviceAsync(string token, int vendorId, int productId, string name, CancellationToken ct = default)
		{
			Endpoints.Add("devices/add");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<DeviceListResponse> ListDevicesAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("devices/list");
			return Task.FromResult(new DeviceListResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> RemoveDeviceAsync(string token, int vendorId, int productId, CancellationToken ct = default)
		{
			Endpoints.Add("devices/remove");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		// Notifications (docs/plans/add-api-v21-notifications-sync) — the push tests never
		// reach them; the list default's null Notifications makes the pull's merge a no-op.
		public Task<NotificationAddResponse> AddNotificationAsync(string token, NotificationAddRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/add");
			return Task.FromResult(new NotificationAddResponse { Success = true, StatusCode = 200 });
		}

		public Task<NotificationListResponse> ListNotificationsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/list");
			return Task.FromResult(new NotificationListResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> ReadNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/read");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> UnreadNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/unread");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeleteNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/delete");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> ReadAllNotificationsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/read_all");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeleteAllNotificationsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/delete_all");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}
	}

	// The roster hydration seam, answered "unknown model" — the upgrade-push tests never pull.
	private sealed class NullCatalogResolver : IDeviceCatalogResolver
	{
		public Task<ProoveGaming.GrpcDependencies.Device.DeviceCatalogEntry?> ResolveAsync(int vendorId, int productId, CancellationToken ct = default) =>
			Task.FromResult<ProoveGaming.GrpcDependencies.Device.DeviceCatalogEntry?>(null);
	}

	// Captures level + rendered message so the ALREADY_EXISTS case can assert "not logged as
	// an error" and the collapse case can assert its warning fired.
	private sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<(LogLevel Level, string Message)> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			lock (Entries)
				Entries.Add((logLevel, formatter(state, exception)));
		}
	}
}
