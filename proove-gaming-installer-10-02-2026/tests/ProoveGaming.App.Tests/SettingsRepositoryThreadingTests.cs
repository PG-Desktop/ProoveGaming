using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProoveGaming.App.Models.Settings;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// The C3 contract: every SettingsRepository Save*/Load*Async is genuinely asynchronous — no
// storage access on the calling thread before the first await (the reproducing assertion that
// failed while the async facade completed synchronously), and fire-and-forget saves keep FIFO
// order through the storage executor. Same seam and pre-await-window reasoning as
// DeviceDetailsSetDeviceThreadingTests.
public class SettingsRepositoryThreadingTests
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string DeviceKey = "";

	private sealed class FakeOwnerProvider : ICurrentOwnerProvider
	{
		public SettingsOwner? Current;
		public SettingsOwner? GetCurrent() => Current;
	}

	private static (SettingsRepository Repo, LocalStorageService Storage) NewRepo()
	{
		var storage = new LocalStorageService("Data Source=:memory:");
		var owner = new FakeOwnerProvider
		{
			Current = new SettingsOwner(LocalStorageService.OwnerKindGuest, Guid.NewGuid().ToString("D"))
		};
		return (new SettingsRepository(storage, owner), storage);
	}

	// One case per repository method family: the delegate issues the call and hands back the
	// task; the harness asserts the pre-await window saw no storage access from the caller.
	public static TheoryData<string> Methods() => new()
	{
		nameof(SettingsRepository.SaveRgbAsync),
		nameof(SettingsRepository.LoadRgbAsync),
		nameof(SettingsRepository.SaveDpiPresetsAsync),
		nameof(SettingsRepository.LoadDpiPresetsAsync),
		nameof(SettingsRepository.SaveActiveDpiStageAsync),
		nameof(SettingsRepository.LoadActiveDpiStageAsync),
		nameof(SettingsRepository.SavePollingRateAsync),
		nameof(SettingsRepository.LoadPollingRateAsync),
		nameof(SettingsRepository.SaveMouseSettingsAsync),
		nameof(SettingsRepository.LoadMouseSettingsAsync),
		nameof(SettingsRepository.SaveAudioSettingsAsync),
		nameof(SettingsRepository.LoadAudioSettingsAsync),
		nameof(SettingsRepository.SaveAppPreferencesAsync),
		nameof(SettingsRepository.LoadAppPreferencesAsync),
	};

	private static Task Invoke(SettingsRepository repo, string method) => method switch
	{
		nameof(SettingsRepository.SaveRgbAsync) => repo.SaveRgbAsync(Vid, Pid, DeviceKey,
			new RgbDeviceSettings(true, "Neon", new Dictionary<string, PerEffectSettings> { ["Neon"] = new("#FF0000", 50, 50) })),
		nameof(SettingsRepository.LoadRgbAsync) => repo.LoadRgbAsync(Vid, Pid, DeviceKey),
		nameof(SettingsRepository.SaveDpiPresetsAsync) => repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey,
			new DpiPresetsSettings([new DpiPresetSetting(0, 800, 0xFF, 0x00, 0x00)])),
		nameof(SettingsRepository.LoadDpiPresetsAsync) => repo.LoadDpiPresetsAsync(Vid, Pid, DeviceKey),
		nameof(SettingsRepository.SaveActiveDpiStageAsync) => repo.SaveActiveDpiStageAsync(Vid, Pid, DeviceKey, 2),
		nameof(SettingsRepository.LoadActiveDpiStageAsync) => repo.LoadActiveDpiStageAsync(Vid, Pid, DeviceKey),
		nameof(SettingsRepository.SavePollingRateAsync) => repo.SavePollingRateAsync(Vid, Pid, DeviceKey, 1000),
		nameof(SettingsRepository.LoadPollingRateAsync) => repo.LoadPollingRateAsync(Vid, Pid, DeviceKey),
		nameof(SettingsRepository.SaveMouseSettingsAsync) => repo.SaveMouseSettingsAsync(Vid, Pid, DeviceKey, new MouseSettingsPayload(8, 2)),
		nameof(SettingsRepository.LoadMouseSettingsAsync) => repo.LoadMouseSettingsAsync(Vid, Pid, DeviceKey),
		nameof(SettingsRepository.SaveAudioSettingsAsync) => repo.SaveAudioSettingsAsync(Vid, Pid, DeviceKey, new AudioSettingsPayload(60, 40)),
		nameof(SettingsRepository.LoadAudioSettingsAsync) => repo.LoadAudioSettingsAsync(Vid, Pid, DeviceKey),
		nameof(SettingsRepository.SaveAppPreferencesAsync) => repo.SaveAppPreferencesAsync(new AppPreferencesPayload(false, 3, 550, 120, 0)),
		nameof(SettingsRepository.LoadAppPreferencesAsync) => repo.LoadAppPreferencesAsync(),
		_ => throw new ArgumentOutOfRangeException(nameof(method)),
	};

	// The reproducing test for C3: on the old facade every one of these completed synchronously,
	// so the storage access landed on the calling thread inside the pre-await window. The calling
	// thread cannot be lent to the pool while it is still executing this body, so any access
	// recorded with its id before the await can only be the synchronous prologue.
	[Theory]
	[MemberData(nameof(Methods))]
	public async Task Method_TouchesNoStorageOnTheCallingThread_BeforeTheFirstAwait(string method)
	{
		var (repo, storage) = NewRepo();
		using (storage)
		{
			var accesses = new ConcurrentBag<int>();
			storage.OnBeforeStorageAccess = () => accesses.Add(Environment.CurrentManagedThreadId);
			var callingThreadId = Environment.CurrentManagedThreadId;

			var pending = Invoke(repo, method);
			var duringPrologue = accesses.ToArray();

			await pending;

			Assert.DoesNotContain(callingThreadId, duringPrologue);
			Assert.NotEmpty(accesses);
		}
	}

	// Two fire-and-forget saves of the same key must apply in enqueue order (last writer wins by
	// FIFO, not by pool-thread luck) — the exact guarantee a bare Task.Run per call site loses.
	[Fact]
	public async Task FireAndForgetSavesOfTheSameKey_ApplyInEnqueueOrder()
	{
		var (repo, storage) = NewRepo();
		using (storage)
		{
			for (var stage = 0; stage < 6; stage++)
				_ = repo.SaveActiveDpiStageAsync(Vid, Pid, DeviceKey, stage);

			await storage.DrainStorageQueueAsync();

			Assert.Equal(5, await repo.LoadActiveDpiStageAsync(Vid, Pid, DeviceKey));
		}
	}

	// A load enqueued after a save of the same key must observe it — loads go through the same
	// queue, not a parallel Task.Run.
	[Fact]
	public async Task LoadEnqueuedAfterAFireAndForgetSave_ObservesTheSave()
	{
		var (repo, storage) = NewRepo();
		using (storage)
		{
			_ = repo.SavePollingRateAsync(Vid, Pid, DeviceKey, 4000);
			var loaded = await repo.LoadPollingRateAsync(Vid, Pid, DeviceKey);

			Assert.Equal(4000, loaded);
		}
	}

	// SaveDpiPresetsAsync now writes through the batched SetDeviceSettings (one transaction);
	// the round-trip pins that the batch upserts exactly the same rows the per-key writes did.
	[Fact]
	public async Task SaveDpiPresets_RoundTripsThroughTheBatchedWrite()
	{
		var (repo, storage) = NewRepo();
		using (storage)
		{
			var stages = new List<DpiPresetSetting>
			{
				new(0, 800, 0xFF, 0x00, 0x00),
				new(1, 1600, 0x00, 0xFF, 0x00),
				new(2, 3200, 0x00, 0x00, 0xFF),
			};
			await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(stages));
			// Upsert, not append: a second batched save of the same stages must not duplicate.
			await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(stages));

			var loaded = await repo.LoadDpiPresetsAsync(Vid, Pid, DeviceKey);

			Assert.NotNull(loaded);
			Assert.Equal(3, loaded!.Stages.Count);
			Assert.Equal(1600, loaded.Stages[1].DpiValue);
			Assert.Equal((byte)0xFF, loaded.Stages[0].R);
		}
	}
}
