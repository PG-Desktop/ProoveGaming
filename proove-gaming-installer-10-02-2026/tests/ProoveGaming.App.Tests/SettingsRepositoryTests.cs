using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models.Settings;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

public class SettingsRepositoryTests
{
    private sealed class FakeOwnerProvider : ICurrentOwnerProvider
    {
        public SettingsOwner? Current;
        public SettingsOwner? GetCurrent() => Current;
    }

    private static (SettingsRepository repo, LocalStorageService storage, FakeOwnerProvider owner)
        NewRepo(string ownerKind, string ownerId)
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var owner = new FakeOwnerProvider { Current = new SettingsOwner(ownerKind, ownerId) };
        return (new SettingsRepository(storage, owner), storage, owner);
    }

    private const int Vid = 0x04D9;
    private const int Pid = 0xA23F;
    private const string DeviceKey = "";

    [Fact]
    public async Task Rgb_RoundTripsMultiEffectPayload()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        var payload = new RgbDeviceSettings(
            IsEnabled: true,
            SelectedEffectKey: "Breathing",
            Effects: new Dictionary<string, PerEffectSettings>
            {
                ["Breathing"] = new("#FF0000", 30, 50),
                ["Neon"]      = new("#00FF00", 80, 20),
            });

        await repo.SaveRgbAsync(Vid, Pid, DeviceKey, payload);
        var loaded = await repo.LoadRgbAsync(Vid, Pid, DeviceKey);

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsEnabled);
        Assert.Equal("Breathing", loaded.SelectedEffectKey);
        Assert.Equal("#FF0000", loaded.Effects["Breathing"].Hex);
        Assert.Equal(80, loaded.Effects["Neon"].Brightness);
    }

    [Fact]
    public async Task DpiPresets_RoundTripsSevenStages()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        var stages = new List<DpiPresetSetting>
        {
            new(0, 200,   0x00, 0xFF, 0x00),
            new(1, 800,   0xFF, 0xFF, 0x00),
            new(2, 2200,  0x00, 0xFF, 0xFF),
            new(3, 3200,  0xFF, 0x88, 0x00),
            new(4, 6400,  0xFF, 0x00, 0xFF),
            new(5, 9600,  0x00, 0x88, 0xFF),
            new(6, 12000, 0xFF, 0x00, 0x00),
        };
        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(stages));

        var loaded = await repo.LoadDpiPresetsAsync(Vid, Pid, DeviceKey);
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded!.Stages.Count);
        Assert.Equal(2200, loaded.Stages[2].DpiValue);
        Assert.Equal((byte)0x88, loaded.Stages[3].G);
    }

    // The ladder SHRINKS: the user can delete DPI points, and the keys are positional
    // (dpi.stage.{i}.*). SaveDpiPresetsAsync used to upsert, which only ever wrote the indices it
    // was given — so a 7-point ladder saved over by a 5-point one left rows 5 and 6 behind, and
    // LoadDpiPresetsAsync (which enumerates whatever indices it finds) resurrected the two deleted
    // points on the next open. This fails against a plain upsert and passes only because the save
    // now replaces the whole dpi.stage. family. See docs/plans/add-custom-dpi-values/.
    [Fact]
    public async Task DpiPresets_SavingAShorterLadder_DropsTheRemovedStages()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        var seven = new List<DpiPresetSetting>();
        for (var i = 0; i < 7; i++)
            seven.Add(new DpiPresetSetting(i, 800 + i * 400, 0, 0, 0));
        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(seven));

        var five = new List<DpiPresetSetting>();
        for (var i = 0; i < 5; i++)
            five.Add(new DpiPresetSetting(i, 1000 + i * 50, 0, 0, 0));
        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(five));

        var loaded = await repo.LoadDpiPresetsAsync(Vid, Pid, DeviceKey);
        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.Stages.Count);
        Assert.Equal(new[] { 1000, 1050, 1100, 1150, 1200 }, loaded.Stages.Select(s => s.DpiValue));
        // Contiguous from zero — no orphaned index 5/6 rows behind the survivors.
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, loaded.Stages.Select(s => s.StageIndex));
    }

    // Growing again after a shrink must not reuse a stale value from the first, longer ladder.
    [Fact]
    public async Task DpiPresets_GrowingAfterAShrink_UsesOnlyTheNewValues()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(
            new List<DpiPresetSetting> { new(0, 800, 0, 0, 0), new(1, 1200, 0, 0, 0), new(2, 9999, 0, 0, 0) }));
        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(
            new List<DpiPresetSetting> { new(0, 800, 0, 0, 0) }));
        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(
            new List<DpiPresetSetting> { new(0, 800, 0, 0, 0), new(1, 1600, 0, 0, 0), new(2, 2400, 0, 0, 0) }));

        var loaded = await repo.LoadDpiPresetsAsync(Vid, Pid, DeviceKey);
        Assert.NotNull(loaded);
        Assert.Equal(new[] { 800, 1600, 2400 }, loaded!.Stages.Select(s => s.DpiValue));
    }

    // The replace is scoped to the dpi.stage. prefix and to one device: neither a sibling setting
    // nor another device's ladder may be collateral damage.
    [Fact]
    public async Task DpiPresets_ShrinkingTheLadder_LeavesOtherSettingsAndDevicesAlone()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        await repo.SaveActiveDpiStageAsync(Vid, Pid, DeviceKey, 3);
        await repo.SavePollingRateAsync(Vid, Pid, DeviceKey, 500);
        await repo.SaveDpiPresetsAsync(Vid, Pid + 1, DeviceKey, new DpiPresetsSettings(
            new List<DpiPresetSetting> { new(0, 400, 0, 0, 0), new(1, 900, 0, 0, 0) }));

        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(
            new List<DpiPresetSetting> { new(0, 800, 0, 0, 0), new(1, 1200, 0, 0, 0), new(2, 2400, 0, 0, 0) }));
        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(
            new List<DpiPresetSetting> { new(0, 800, 0, 0, 0) }));

        Assert.Equal(3, await repo.LoadActiveDpiStageAsync(Vid, Pid, DeviceKey));
        Assert.Equal(500, await repo.LoadPollingRateAsync(Vid, Pid, DeviceKey));

        var other = await repo.LoadDpiPresetsAsync(Vid, Pid + 1, DeviceKey);
        Assert.NotNull(other);
        Assert.Equal(2, other!.Stages.Count);
    }

    [Fact]
    public async Task ActiveDpiStage_RoundTrips()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        await repo.SaveActiveDpiStageAsync(Vid, Pid, DeviceKey, 4);
        Assert.Equal(4, await repo.LoadActiveDpiStageAsync(Vid, Pid, DeviceKey));
    }

    [Fact]
    public async Task PollingRate_RoundTrips()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        await repo.SavePollingRateAsync(Vid, Pid, DeviceKey, 500);
        Assert.Equal(500, await repo.LoadPollingRateAsync(Vid, Pid, DeviceKey));
    }

    [Fact]
    public async Task MouseSettings_RoundTrips()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        await repo.SaveMouseSettingsAsync(Vid, Pid, DeviceKey, new MouseSettingsPayload(20, 3));
        var loaded = await repo.LoadMouseSettingsAsync(Vid, Pid, DeviceKey);
        Assert.NotNull(loaded);
        Assert.Equal((byte)20, loaded!.ButtonDebounce);
        Assert.Equal((byte)3, loaded.LiftOffDistance);
    }

    [Fact]
    public async Task AppPreferences_RoundTrips()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        var payload = new AppPreferencesPayload(
            ScrollOneScreenAtATime: true,
            ScrollLines: 10,
            DoubleClickSpeed: 750,
            SleepTimer: 60,
            GenericLiftOffDistance: 2);

        await repo.SaveAppPreferencesAsync(payload);
        var loaded = await repo.LoadAppPreferencesAsync();

        Assert.NotNull(loaded);
        Assert.True(loaded!.ScrollOneScreenAtATime);
        Assert.Equal(10, loaded.ScrollLines);
        Assert.Equal(750, loaded.DoubleClickSpeed);
        Assert.Equal(60, loaded.SleepTimer);
        Assert.Equal(2, loaded.GenericLiftOffDistance);
    }

    [Fact]
    public async Task LoadRgbAsync_WithNoRow_ReturnsNull()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        Assert.Null(await repo.LoadRgbAsync(Vid, Pid, DeviceKey));
    }

    [Fact]
    public async Task SaveRgbAsync_WhenOwnerIsNull_DoesNotThrowAndSkipsWrite()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        using var _s = storage;
        var owner = new FakeOwnerProvider { Current = null };
        var repo = new SettingsRepository(storage, owner);

        var exception = await Record.ExceptionAsync(() => repo.SaveRgbAsync(Vid, Pid, DeviceKey,
            new RgbDeviceSettings(true, "Breathing", new Dictionary<string, PerEffectSettings>())));

        Assert.Null(exception);

        // Nothing to load back — read must return null (owner-less reads no-op).
        Assert.Null(await repo.LoadRgbAsync(Vid, Pid, DeviceKey));
    }

    [Fact]
    public async Task Owner_ChangesFromUserToNewGuest_ReadsReturnNullForNewGuest()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        using var _s = storage;

        var owner = new FakeOwnerProvider
        {
            Current = new SettingsOwner(LocalStorageService.OwnerKindUser, "42")
        };
        var repo = new SettingsRepository(storage, owner);

        await repo.SaveRgbAsync(Vid, Pid, DeviceKey, new RgbDeviceSettings(
            true, "Breathing",
            new Dictionary<string, PerEffectSettings> { ["Breathing"] = new("#FF0000", 50, 50) }));
        await repo.SaveDpiPresetsAsync(Vid, Pid, DeviceKey, new DpiPresetsSettings(
            new List<DpiPresetSetting> { new(0, 400, 0x00, 0xFF, 0x00) }));
        await repo.SaveActiveDpiStageAsync(Vid, Pid, DeviceKey, 3);
        await repo.SavePollingRateAsync(Vid, Pid, DeviceKey, 1000);
        await repo.SaveMouseSettingsAsync(Vid, Pid, DeviceKey, new MouseSettingsPayload(15, 2));
        await repo.SaveAppPreferencesAsync(new AppPreferencesPayload(true, 5, 400, 90, 1));

        // Logout → "Continue as guest" mints a fresh Guid; provider flips to that new owner.
        owner.Current = new SettingsOwner(
            LocalStorageService.OwnerKindGuest,
            System.Guid.NewGuid().ToString("D"));

        Assert.Null(await repo.LoadRgbAsync(Vid, Pid, DeviceKey));
        Assert.Null(await repo.LoadDpiPresetsAsync(Vid, Pid, DeviceKey));
        Assert.Null(await repo.LoadActiveDpiStageAsync(Vid, Pid, DeviceKey));
        Assert.Null(await repo.LoadPollingRateAsync(Vid, Pid, DeviceKey));
        Assert.Null(await repo.LoadMouseSettingsAsync(Vid, Pid, DeviceKey));
        Assert.Null(await repo.LoadAppPreferencesAsync());
    }

    [Fact]
    public async Task Owner_ChangesFromGuestToUser_ReadsPickUpTheNewOwner()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        using var _s = storage;

        var guestGuid = System.Guid.NewGuid();
        var owner = new FakeOwnerProvider
        {
            Current = new SettingsOwner(LocalStorageService.OwnerKindGuest, guestGuid.ToString("D"))
        };
        var repo = new SettingsRepository(storage, owner);

        // Guest writes a setting.
        await repo.SaveRgbAsync(Vid, Pid, DeviceKey, new RgbDeviceSettings(
            true, "Neon",
            new Dictionary<string, PerEffectSettings> { ["Neon"] = new("#FF00FF", 60, 40) }));

        // Simulate a successful sign-in: migration + flip the provider to the user.
        await storage.MigrateGuestToUserAsync(guestGuid, 42);
        owner.Current = new SettingsOwner(LocalStorageService.OwnerKindUser, "42");

        var loaded = await repo.LoadRgbAsync(Vid, Pid, DeviceKey);
        Assert.NotNull(loaded);
        Assert.Equal("Neon", loaded!.SelectedEffectKey);
    }

    [Fact]
    public async Task Rgb_DifferentDeviceKeys_DoNotCollide()
    {
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, "guest-a");
        using var _s = storage;

        await repo.SaveRgbAsync(Vid, Pid, "unit-a", new RgbDeviceSettings(
            true, "Neon", new Dictionary<string, PerEffectSettings> { ["Neon"] = new("#FF0000", 50, 50) }));
        await repo.SaveRgbAsync(Vid, Pid, "unit-b", new RgbDeviceSettings(
            true, "ColorCycle", new Dictionary<string, PerEffectSettings> { ["ColorCycle"] = new("#00FF00", 60, 40) }));

        Assert.Equal("Neon",       (await repo.LoadRgbAsync(Vid, Pid, "unit-a"))!.SelectedEffectKey);
        Assert.Equal("ColorCycle", (await repo.LoadRgbAsync(Vid, Pid, "unit-b"))!.SelectedEffectKey);
    }

    // ── Audio settings: a missing key is "unknown", not 0 ─────────────────────

    // Filling an absent key with default(int) would report the microphone at zero, and the caller
    // applies what it reads — so a device that only ever stored an output level would have its
    // microphone driven to silence.
    [Fact]
    public async Task LoadAudioSettings_LeavesAMissingKeyNull()
    {
        var owner = System.Guid.NewGuid().ToString("D");
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, owner);
        using (storage)
        {
            // Only the output level exists for this device.
            storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "audio.outputVolume", 40);

            var loaded = await repo.LoadAudioSettingsAsync(Vid, Pid, DeviceKey);

            Assert.NotNull(loaded);
            Assert.Equal(40, loaded!.OutputVolume);
            Assert.Null(loaded.MicrophoneVolume);
        }
    }

    [Fact]
    public async Task SaveAudioSettings_DoesNotPersistANullLevel()
    {
        var owner = System.Guid.NewGuid().ToString("D");
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, owner);
        using (storage)
        {
            await repo.SaveAudioSettingsAsync(Vid, Pid, DeviceKey, new AudioSettingsPayload(55, null));

            var all = storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            Assert.True(all.ContainsKey("audio.outputVolume"));
            Assert.False(all.ContainsKey("audio.micVolume"));
        }
    }

    [Fact]
    public async Task LoadAudioSettings_ReturnsNullWhenNeitherKeyExists()
    {
        var owner = System.Guid.NewGuid().ToString("D");
        var (repo, storage, _) = NewRepo(LocalStorageService.OwnerKindGuest, owner);
        using (storage)
        {
            Assert.Null(await repo.LoadAudioSettingsAsync(Vid, Pid, DeviceKey));
        }
    }
}
