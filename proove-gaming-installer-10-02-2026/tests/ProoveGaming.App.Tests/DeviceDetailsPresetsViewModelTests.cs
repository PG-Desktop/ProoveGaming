using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers the VM-level preset flow: dirty tracking, SelectPreset side-effects, and the
// SavePreset command's CanExecute wiring. Exercises the same code path that the top-panel
// dropdown and the row-list RadioButton both invoke (SelectPresetCommand).
public class DeviceDetailsPresetsViewModelTests
{
    private const int Vid = 0x04D9;
    private const int Pid = 0xA23F;
    private const string DeviceKey = "";

    private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
    {
        private readonly SettingsOwner _owner = new(kind, id);
        public SettingsOwner? GetCurrent() => _owner;
    }

    private static (DeviceDetailsViewModel Vm, LocalStorageService Store, string Owner) NewVmWithStorage()
    {
        var owner = System.Guid.NewGuid().ToString("D");
        var store = new LocalStorageService("Data Source=:memory:");
        var vm = new DeviceDetailsViewModel(
            usbHidDeviceService: null,
            localStorage: store,
            settings: null,
            ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner));
        return (vm, store, owner);
    }

    // Owner-kind-aware variant for the seed-vs-pull gate tests: the gate only engages for
    // OwnerKindUser, and only when an ISettingsSyncService is injected.
    private static (DeviceDetailsViewModel Vm, LocalStorageService Store, string Owner) NewVmWithStorage(
        string ownerKind, ISettingsSyncService settingsSync)
    {
        var owner = System.Guid.NewGuid().ToString("D");
        var store = new LocalStorageService("Data Source=:memory:");
        var vm = new DeviceDetailsViewModel(
            usbHidDeviceService: null,
            localStorage: store,
            settings: null,
            ownerProvider: new FakeOwnerProvider(ownerKind, owner),
            settingsSync: settingsSync);
        return (vm, store, owner);
    }

    // The only fake surface the gate touches is PullTask; the rest is inert. Mirrors the
    // real contract: Task.CompletedTask when no pull ever started, never faults.
    private sealed class FakeSettingsSyncService : ISettingsSyncService
    {
        public Task PullTask { get; set; } = Task.CompletedTask;
        // Never raised here — explicit no-op accessors instead of field-like events, which
        // would warn CS0067 (declared but never used).
        public event Action? PullCompletedWithChanges { add { } remove { } }
        public event Action? SyncCompleted { add { } remove { } }
        public event Action? NotificationsPulled { add { } remove { } }
        public Task OnGuestUpgradedAsync(int customerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnSignedInAsync(int customerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static SingleCardViewModel FakeDevice() => new()
    {
        Name = "Test Device",
        DeviceTypeLabel = "Wired Mouse",
        VendorId = Vid,
        ProductId = Pid,
        SettingsVendorId = Vid,
        SettingsProductId = Pid,
        DeviceKey = DeviceKey,
        SettingsDeviceKey = DeviceKey,
    };

    private static PresetViewModel MakeRow(int id, string name) =>
        new() { Id = id, Name = name };

    // A second physical unit of the SAME model (same Vid/Pid/SettingsProductId) — a different
    // Container ID (DeviceKey/SettingsDeviceKey), like a second connected BLAZE mouse.
    private static SingleCardViewModel FakeDevice(string deviceKey) => new()
    {
        Name = "Test Device",
        DeviceTypeLabel = "Wired Mouse",
        VendorId = Vid,
        ProductId = Pid,
        SettingsVendorId = Vid,
        SettingsProductId = Pid,
        DeviceKey = deviceKey,
        SettingsDeviceKey = deviceKey,
    };

    // ── Initial state ────────────────────────────────────────────────────────

    [Fact]
    public void HasUnsavedPresetChanges_StartsFalse()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            Assert.False(vm.HasUnsavedPresetChanges);
            Assert.Null(vm.SelectedPreset);
        }
    }

    [Fact]
    public void SavePreset_CanExecute_FalseWhenNoSelectedPreset()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            Assert.False(vm.SavePresetCommand.CanExecute(null));
        }
    }

    // ── SelectPreset behaviour ───────────────────────────────────────────────

    [Fact]
    public async Task SelectPreset_SetsSelectedPreset_AndMarksRowIsSelected()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            var b = MakeRow(2, "B");
            vm.Presets.Add(a);
            vm.Presets.Add(b);

            await vm.SelectPresetCommand.ExecuteAsync(a);

            Assert.Same(a, vm.SelectedPreset);
            Assert.True(a.IsSelected);
            Assert.False(b.IsSelected);
        }
    }

    [Fact]
    public async Task SelectPreset_ClearsPriorRowSelection()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            var b = MakeRow(2, "B");
            vm.Presets.Add(a);
            vm.Presets.Add(b);

            await vm.SelectPresetCommand.ExecuteAsync(a);
            await vm.SelectPresetCommand.ExecuteAsync(b);

            Assert.Same(b, vm.SelectedPreset);
            Assert.True(b.IsSelected);
            Assert.False(a.IsSelected);
        }
    }

    [Fact]
    public async Task SelectPreset_CleanSwitch_ClearsHasUnsavedPresetChanges()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            var b = MakeRow(2, "B");
            vm.Presets.Add(a);
            vm.Presets.Add(b);
            await vm.SelectPresetCommand.ExecuteAsync(a);

            // Not dirty — switching to another preset succeeds immediately.
            await vm.SelectPresetCommand.ExecuteAsync(b);

            Assert.Same(b, vm.SelectedPreset);
            Assert.False(vm.HasUnsavedPresetChanges);
        }
    }

    [Fact]
    public async Task SelectPreset_DirtySwitch_OpensUnsavedChangesDialog_AndDoesNotSwitch()
    {
        // New behaviour: switching to a different preset while the current one is dirty
        // parks the target and opens the Unsaved-Changes dialog instead of switching.
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            var b = MakeRow(2, "B");
            vm.Presets.Add(b);
            await vm.SelectPresetCommand.ExecuteAsync(b);

            Assert.True(vm.IsUnsavedChangesOpen);
            Assert.Same(a, vm.SelectedPreset);
            Assert.True(vm.HasUnsavedPresetChanges);
        }
    }

    [Fact]
    public async Task SelectPreset_DirtySwitch_ThenDiscard_SwitchesToParkedTarget_AndClearsDirty()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            var b = MakeRow(2, "B");
            vm.Presets.Add(b);
            await vm.SelectPresetCommand.ExecuteAsync(b);
            Assert.True(vm.IsUnsavedChangesOpen);

            await vm.DiscardUnsavedChangesCommand.ExecuteAsync(null);

            Assert.False(vm.IsUnsavedChangesOpen);
            Assert.Same(b, vm.SelectedPreset);
            Assert.False(vm.HasUnsavedPresetChanges);
        }
    }

    [Fact]
    public async Task SelectPreset_DirtySwitch_ThenCancel_DropsParkedTarget_AndKeepsDirty()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            var b = MakeRow(2, "B");
            vm.Presets.Add(b);
            await vm.SelectPresetCommand.ExecuteAsync(b);

            vm.CancelUnsavedChangesCommand.Execute(null);

            Assert.False(vm.IsUnsavedChangesOpen);
            Assert.Same(a, vm.SelectedPreset);
            Assert.True(vm.HasUnsavedPresetChanges);
        }
    }

    [Fact]
    public async Task SelectPreset_ClosesDropdown()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            vm.IsPresetsDropdownOpen = true;

            await vm.SelectPresetCommand.ExecuteAsync(a);

            Assert.False(vm.IsPresetsDropdownOpen);
        }
    }

    [Fact]
    public async Task SelectPreset_DirtySwitch_ClosesDropdownWhenConfirmOpens()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            var b = MakeRow(2, "B");
            vm.Presets.Add(b);
            vm.IsPresetsDropdownOpen = true;

            // The pick lands INSIDE the dropdown popup, so light-dismiss never fires — the
            // dialog open itself must close the list.
            await vm.SelectPresetCommand.ExecuteAsync(b);

            Assert.True(vm.IsUnsavedChangesOpen);
            Assert.False(vm.IsPresetsDropdownOpen);
        }
    }

    // ── NotifyPresetChanged guards ───────────────────────────────────────────────

    [Fact]
    public void NotifyPresetChanged_WithoutSelectedPreset_IsNoOp()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            vm.NotifyPresetChanged();
            Assert.False(vm.HasUnsavedPresetChanges);
        }
    }

    [Fact]
    public async Task NotifyPresetChanged_WithSelectedPreset_SetsDirty()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);

            vm.NotifyPresetChanged();

            Assert.True(vm.HasUnsavedPresetChanges);
        }
    }

    // ── SavePreset command CanExecute matrix ─────────────────────────────────

    [Fact]
    public async Task SavePreset_CanExecute_FalseWhenSelectedButNotDirty()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);

            Assert.False(vm.HasUnsavedPresetChanges);
            Assert.False(vm.SavePresetCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task SavePreset_CanExecute_FalseWhenDirtyButNoDevice()
    {
        // SavePreset also requires a Device (the snapshot is scoped to the current one).
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            Assert.True(vm.HasUnsavedPresetChanges);
            Assert.Null(vm.Device);
            Assert.False(vm.SavePresetCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task SavePreset_CanExecute_TrueWhenSelectedDirtyAndDeviceSet()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.Device = FakeDevice();
            vm.NotifyPresetChanged();

            Assert.True(vm.SavePresetCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task SavePreset_Execute_OpensDialog_DoesNotClearDirtyYet()
    {
        // SavePresetCommand now merely opens the Save-Preset picker. The actual write
        // happens on ConfirmSavePresetCommand.
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });

            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            Assert.True(vm.SavePresetCommand.CanExecute(null));

            vm.SavePresetCommand.Execute(null);

            Assert.True(vm.IsSavePresetOpen);
            Assert.Same(a, vm.SavePresetTarget);
            Assert.True(vm.HasUnsavedPresetChanges);
        }
    }

    [Fact]
    public async Task SavePresetThenConfirm_ClearsHasUnsavedPresetChanges()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });

            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            vm.SavePresetCommand.Execute(null);
            await vm.ConfirmSavePresetCommand.ExecuteAsync(null);

            Assert.False(vm.IsSavePresetOpen);
            Assert.False(vm.HasUnsavedPresetChanges);
            Assert.False(vm.SavePresetCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task SavePresetThenConfirm_SnapshotsCurrentBindingsIntoDb()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);

            // Seed a live binding then run the two-step save flow.
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 7, null);
            vm.NotifyPresetChanged();
            vm.SavePresetCommand.Execute(null);
            await vm.ConfirmSavePresetCommand.ExecuteAsync(null);

            // Overwrite the live state and load the snapshot back — the saved (3, 7) row
            // must return, proving Confirm persisted it via SavePresetSnapshot.
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 99, null);
            store.LoadPresetSnapshotToDevice(pid, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

            var live = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            Assert.Single(live);
            Assert.Equal((3, 7, (int?)null), live[0]);
        }
    }

    [Fact]
    public async Task SavePresetIntoDifferentTarget_SnapshotsIntoTarget_AndSwitchesSelectionToIt()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pidA = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var pidB = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "B" });
            var a = MakeRow(pidA, "A");
            var b = MakeRow(pidB, "B");
            vm.Presets.Add(a);
            vm.Presets.Add(b);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);

            // Edit while A is active, then save the edit into B via the dialog's target picker.
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 7, null);
            vm.NotifyPresetChanged();
            vm.SavePresetCommand.Execute(null);
            vm.SelectSavePresetTargetCommand.Execute(b);
            await vm.ConfirmSavePresetCommand.ExecuteAsync(null);

            // The edit landed in B's snapshot, not A's.
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 99, null);
            store.LoadPresetSnapshotToDevice(pidB, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            var live = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            Assert.Single(live);
            Assert.Equal((3, 7, (int?)null), live[0]);

            // And the app switched to B automatically, with nothing left dirty.
            Assert.Same(b, vm.SelectedPreset);
            Assert.True(b.IsSelected);
            Assert.False(a.IsSelected);
            Assert.False(vm.HasUnsavedPresetChanges);
            Assert.False(vm.IsSavePresetOpen);
        }
    }

    [Fact]
    public async Task SaveUnsavedChanges_ReseedsSaveDialogTargetToSelectedPreset()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pidA = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var pidB = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "B" });
            var a = MakeRow(pidA, "A");
            var b = MakeRow(pidB, "B");
            vm.Presets.Add(a);
            vm.Presets.Add(b);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            // A previously cancelled Save-Preset session parks B as the target and leaves
            // a search query behind.
            vm.SavePresetCommand.Execute(null);
            vm.SelectSavePresetTargetCommand.Execute(b);
            vm.SavePresetSearchQuery = "B";
            vm.CancelSavePresetCommand.Execute(null);

            // Re-opening via the unsaved-changes "Save" route must reseed to the ACTIVE
            // preset — Confirm now saves into AND switches to the target, so a stale B
            // here would send the user's changes to a preset they never picked this time.
            vm.SaveUnsavedChangesCommand.Execute(null);

            Assert.True(vm.IsSavePresetOpen);
            Assert.Same(a, vm.SavePresetTarget);
            Assert.True(a.IsSaveTarget);
            Assert.False(b.IsSaveTarget);
            Assert.Equal(string.Empty, vm.SavePresetSearchQuery);
        }
    }

    // ── ConfirmResetPreset wiring ────────────────────────────────────────────

    // Row-menu Reset restores the device to its defaults and SAVES that into the preset. It used to
    // wipe the snapshot and leave the live device alone, so the menu item looked like it did nothing
    // until the row was re-selected.
    //
    // The discriminator is that the snapshot ends up holding the DEFAULT bindings. The old wipe left
    // it EMPTY, which is why asserting only "the custom binding is gone" passes either way.
    [Fact]
    public async Task ConfirmResetPreset_RestoresDefaultsAndSavesThemIntoThePreset()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);
            // Reset only touches buttons on a device that declares remapping — the plain FakeDevice
            // does not, and would make this pass for the wrong reason.
            var device = FakeDevice();
            device.SupportsButtonRemap = true;
            device.SupportsConfiguration = true;
            vm.Device = device;
            await vm.SelectPresetCommand.ExecuteAsync(a);

            // Put a non-default binding on the device and save it into the preset.
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 7, null);
            vm.NotifyPresetChanged();
            vm.SavePresetCommand.Execute(null);
            await vm.ConfirmSavePresetCommand.ExecuteAsync(null);
            store.LoadPresetSnapshotToDevice(pid, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            Assert.Contains(
                store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey),
                b => b.Button == 3 && b.Action == 7);

            vm.RestorePresetCommand.Execute(a);
            Assert.True(vm.IsResetPresetOpen); // the confirmation dialog gates the reset
            await vm.ConfirmResetPresetCommand.ExecuteAsync(null);

            Assert.False(vm.IsResetPresetOpen);

            // The presets row survives.
            Assert.Single(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));

            store.LoadPresetSnapshotToDevice(pid, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            var restored = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

            // The DEFAULTS were saved into the preset. The old implementation wiped the snapshot and
            // left this empty, so this assertion is what separates the two.
            Assert.NotEmpty(restored);
            Assert.DoesNotContain(restored, b => b.Button == 3 && b.Action == 7);

            // The reset was SAVED to the preset, not just applied to the hardware.
            Assert.Equal(pid, store.GetAppliedPreset(
                LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

            // Resetting the active preset leaves nothing unsaved.
            Assert.False(vm.HasUnsavedPresetChanges);
        }
    }

    // Cancel must leave both the preset and the live device untouched.
    [Fact]
    public async Task CancelResetPreset_LeavesTheSnapshotIntact()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);

            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 7, null);
            vm.NotifyPresetChanged();
            vm.SavePresetCommand.Execute(null);
            await vm.ConfirmSavePresetCommand.ExecuteAsync(null);

            vm.RestorePresetCommand.Execute(a);
            vm.CancelResetPresetCommand.Execute(null);

            Assert.False(vm.IsResetPresetOpen);
            store.LoadPresetSnapshotToDevice(pid, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            Assert.Contains(
                store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey),
                b => b.Button == 3 && b.Action == 7);
        }
    }

    // Presets exist on keyboards and the headset too, and the reset used to run the mouse Settings
    // section's command — which resets sliders, buttons and the DPI ladder and nothing else. These
    // pin that the reset is driven by CAPABILITY, so a device without mouse hardware still gets its
    // own aspects restored rather than a no-op.

    [Fact]
    public async Task ConfirmResetPreset_RestoresRgbDefaults_OnANonMouseDevice()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);

            // Keyboard-shaped: lighting, but no remappable buttons and no DPI ladder.
            var device = FakeDevice();
            device.DeviceTypeLabel = "Wired Keyboard";
            device.SupportsRgb = true;
            device.SupportsButtonRemap = false;
            vm.Device = device;
            await vm.SelectPresetCommand.ExecuteAsync(a);

            // Move the lighting off its default.
            var effect = vm.ActiveRgbEffect;
            effect.SelectedHexColor = "#00FF00";
            effect.Brightness = 12;

            vm.RestorePresetCommand.Execute(a);
            await vm.ConfirmResetPresetCommand.ExecuteAsync(null);

            // Back on the catalog default (no catalog RGB configured here, so the built-in fallback).
            Assert.NotEqual("#00FF00", vm.ActiveRgbEffect.SelectedHexColor);
            Assert.NotEqual(12, vm.ActiveRgbEffect.Brightness);
        }
    }

    // A headset has neither buttons nor a DPI ladder. The reset must still complete and save,
    // rather than throwing on a mouse-only step or silently doing nothing.
    [Fact]
    public async Task ConfirmResetPreset_CompletesOnADeviceWithNoMouseCapabilities()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);

            var device = FakeDevice();
            device.DeviceTypeLabel = "Wireless Headset";
            device.SupportsRgb = false;
            device.SupportsButtonRemap = false;
            device.SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"];
            vm.Device = device;
            await vm.SelectPresetCommand.ExecuteAsync(a);

            vm.RestorePresetCommand.Execute(a);
            await vm.ConfirmResetPresetCommand.ExecuteAsync(null);

            Assert.False(vm.IsResetPresetOpen);
            Assert.Single(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
            Assert.Equal(pid, store.GetAppliedPreset(
                LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
        }
    }

    // ── Delete cascade through the VM ────────────────────────────────────────

    [Fact]
    public async Task DeletePreset_RemovesRowFromCollectionAndDb()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);

            vm.DeletePresetCommand.Execute(a);
            await vm.ConfirmDeletePresetCommand.ExecuteAsync(null);

            Assert.DoesNotContain(a, vm.Presets);
            Assert.Empty(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
        }
    }

    [Fact]
    public void DeletePreset_OnDefault_IsNoOp()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "Default", IsDefault = true });
            var a = new PresetViewModel { Id = pid, Name = "Default", IsDefault = true };
            vm.Presets.Add(a);

            vm.DeletePresetCommand.Execute(a);

            // The user is never even asked to confirm a delete that cannot happen.
            Assert.False(vm.IsDeletePresetOpen);

            // Row survives both in memory and in storage.
            Assert.Contains(a, vm.Presets);
            Assert.Single(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
        }
    }

    [Fact]
    public async Task DeletePreset_OfActivePreset_SwitchesToNextRow_AndAppliesItsSnapshot()
    {
        // After deleting the currently-selected row, the next row must become active AND
        // its snapshot must be loaded so device_* rows reflect the new selection.
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var aid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var bid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "B" });
            var a = MakeRow(aid, "A");
            var b = MakeRow(bid, "B");
            vm.Presets.Add(a);
            vm.Presets.Add(b);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);

            // Preset B carries a distinct snapshot; the live state currently has A's stuff
            // (an empty snapshot at this point — good enough for the assertion).
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 4, 9, null);
            store.SavePresetSnapshot(bid, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

            // Overwrite the live rows with A-flavoured state, then delete A.
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 4, 99, null);
            vm.DeletePresetCommand.Execute(a);
            await vm.ConfirmDeletePresetCommand.ExecuteAsync(null);

            Assert.Same(b, vm.SelectedPreset);

            // Device rows now reflect B's snapshot.
            var live = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            Assert.Single(live);
            Assert.Equal((4, 9, (int?)null), live[0]);
        }
    }

    // Deleting destroys a preset's saved settings with no undo, so the row menu must only OPEN a
    // confirmation. Without this the menu item deleted on the first click.
    [Fact]
    public void DeletePreset_OpensTheConfirmationDialog_WithoutDeleting()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);

            vm.DeletePresetCommand.Execute(a);

            Assert.True(vm.IsDeletePresetOpen);
            Assert.Contains(a, vm.Presets);
            Assert.Single(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
        }
    }

    [Fact]
    public async Task CancelDeletePreset_KeepsThePreset()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);

            vm.DeletePresetCommand.Execute(a);
            vm.CancelDeletePresetCommand.Execute(null);

            Assert.False(vm.IsDeletePresetOpen);
            Assert.Contains(a, vm.Presets);
            Assert.Single(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
        }
    }

    // Confirm with no pending target (dialog dismissed, command re-invoked) must not delete anything.
    [Fact]
    public async Task ConfirmDeletePreset_WithoutAPendingTarget_IsNoOp()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            vm.Presets.Add(MakeRow(pid, "A"));

            await vm.ConfirmDeletePresetCommand.ExecuteAsync(null);

            Assert.Single(vm.Presets);
            Assert.Single(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
        }
    }

    // ── Unsaved-Changes: leaving-view flow ───────────────────────────────────

    [Fact]
    public async Task OpenUnsavedChangesForLeaving_DiscardFires_ExitConfirmedEvent()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            var fired = 0;
            vm.ExitConfirmedAfterUnsavedChanges += () => fired++;

            vm.OpenUnsavedChangesForLeaving();
            Assert.True(vm.IsUnsavedChangesOpen);

            await vm.DiscardUnsavedChangesCommand.ExecuteAsync(null);

            Assert.Equal(1, fired);
            Assert.False(vm.IsUnsavedChangesOpen);
            Assert.False(vm.HasUnsavedPresetChanges);
        }
    }

    [Fact]
    public async Task DiscardUnsavedChanges_WhenLeavingView_RevertsLiveStateToPresetSnapshot()
    {
        // Regression test: discarding while leaving the view must not just clear the dirty flag —
        // it must also undo whatever the user tinkered with (RGB/buttons/etc. already land in the
        // live device_* rows the moment the user touches a control, before Save Preset is ever
        // clicked), by re-loading the active preset's own last-saved snapshot back over the live
        // state. Otherwise "Discard" silently keeps the unsaved change as the new de-facto state.
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);

            // Save a baseline snapshot with one binding.
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 7, null);
            vm.NotifyPresetChanged();
            vm.SavePresetCommand.Execute(null);
            await vm.ConfirmSavePresetCommand.ExecuteAsync(null);
            Assert.False(vm.HasUnsavedPresetChanges);

            // Tinker with the live binding without saving — this is the state a UI control change
            // would leave behind (e.g. picking a different button action).
            store.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 3, 99, null);
            vm.NotifyPresetChanged();
            Assert.True(vm.HasUnsavedPresetChanges);

            var fired = 0;
            vm.ExitConfirmedAfterUnsavedChanges += () => fired++;

            vm.OpenUnsavedChangesForLeaving();
            await vm.DiscardUnsavedChangesCommand.ExecuteAsync(null);

            Assert.Equal(1, fired);
            Assert.False(vm.HasUnsavedPresetChanges);

            // The tinkered (3, 99) binding must be gone — reverted back to the saved (3, 7).
            var live = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
            Assert.Single(live);
            Assert.Equal((3, 7, (int?)null), live[0]);
        }
    }

    [Fact]
    public async Task OpenUnsavedChangesForLeaving_Cancel_DoesNotFireExit_AndKeepsDirty()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = MakeRow(1, "A");
            vm.Presets.Add(a);
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            var fired = 0;
            vm.ExitConfirmedAfterUnsavedChanges += () => fired++;

            vm.OpenUnsavedChangesForLeaving();
            vm.CancelUnsavedChangesCommand.Execute(null);

            Assert.Equal(0, fired);
            Assert.False(vm.IsUnsavedChangesOpen);
            Assert.True(vm.HasUnsavedPresetChanges);
        }
    }

    // ── LoadPresetsForDevice self-heal branches ──────────────────────────────

    [Fact]
    public async Task LoadPresetsForDevice_EmptyList_AutoCreatesDefaultRow()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync();

            // In memory.
            Assert.Single(vm.Presets);
            Assert.True(vm.Presets[0].IsDefault);
            Assert.Equal(vm.Presets[0], vm.SelectedPreset);

            // Persisted.
            var rows = store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
            Assert.Single(rows);
            Assert.True(rows[0].IsDefault);
        }
    }

    [Fact]
    public async Task LoadPresetsForDevice_LegacyRowsWithoutDefault_PromoteFirstRow()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            // Simulate a legacy DB by inserting rows explicitly WITHOUT IsDefault.
            store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "A" });
            store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "B" });

            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync();

            Assert.Equal(2, vm.Presets.Count);
            Assert.True(vm.Presets[0].IsDefault);
            Assert.False(vm.Presets[1].IsDefault);

            var rows = store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
            Assert.True(rows.First(r => r.Name == "A").IsDefault);
            Assert.False(rows.First(r => r.Name == "B").IsDefault);
        }
    }

    // Reopening the app must restore whatever preset the user was actually using last, not just
    // whichever preset happens to be first by id. B is inserted second (higher id) but was the
    // last one applied to the device — LoadPresetsForDevice must select B, matching
    // device_applied_preset, not A.
    [Fact]
    public async Task LoadPresetsForDevice_SelectsLastAppliedPreset_NotFirstById()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var aid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "A", IsDefault = true });
            var bid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "B" });
            store.SetAppliedPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, bid);

            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync();

            Assert.Equal(bid, vm.SelectedPreset?.Id);
            Assert.True(vm.Presets.Single(p => p.Id == bid).IsSelected);
            Assert.False(vm.Presets.Single(p => p.Id == aid).IsSelected);
        }
    }

    // No preset has ever been applied (a genuinely fresh device, or one from before the tracking
    // table existed) — falls back to the first row, same as before this fix.
    [Fact]
    public async Task LoadPresetsForDevice_NoAppliedPreset_FallsBackToFirstById()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var aid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "A", IsDefault = true });
            store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "B" });

            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync();

            Assert.Equal(aid, vm.SelectedPreset?.Id);
        }
    }

    // ── Seed-vs-pull gate (docs/plans/fix-duplicate-default-preset) ──────────
    // For a signed-in user an empty preset scope may just be the sign-in pull still in
    // flight: seeding a default then mints a fresh-uid row the pull cannot dedup, and the
    // coordinator pushes it — one server-side duplicate default per DB wipe. The load must
    // wait for ISettingsSyncService.PullTask before judging emptiness; guests never wait.

    [Fact]
    public async Task LoadPresetsForDevice_UserOwnerWithPullInFlight_DefersTheSeedUntilThePullSettles()
    {
        var pullParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeSettingsSyncService { PullTask = pullParked.Task };
        var (vm, store, owner) = NewVmWithStorage(LocalStorageService.OwnerKindUser, sync);
        using (store)
        {
            vm.Device = FakeDevice();
            // Everything before the gate is synchronous, so with the pull parked the load is
            // deterministically parked at the await — and the seed must NOT have run yet.
            var load = vm.LoadPresetsForDeviceAsync();

            Assert.False(load.IsCompleted);
            Assert.Empty(store.GetPresets(LocalStorageService.OwnerKindUser, owner, Vid, Pid));

            // The pull settles with the scope still genuinely empty (empty account) — the
            // seed now proceeds exactly as before the gate existed.
            pullParked.SetResult();
            await load.WaitAsync(TimeSpan.FromSeconds(10));

            var row = Assert.Single(store.GetPresets(LocalStorageService.OwnerKindUser, owner, Vid, Pid));
            Assert.True(row.IsDefault);
            Assert.True(Assert.Single(vm.Presets).IsDefault);
        }
    }

    [Fact]
    public async Task LoadPresetsForDevice_PullHydratesTheScopeWhileParked_SkipsTheSeed()
    {
        var pullParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeSettingsSyncService { PullTask = pullParked.Task };
        var (vm, store, owner) = NewVmWithStorage(LocalStorageService.OwnerKindUser, sync);
        using (store)
        {
            vm.Device = FakeDevice();
            var load = vm.LoadPresetsForDeviceAsync();
            Assert.False(load.IsCompleted);

            // While the VM waits, the pull lands the account's default preset into the scope
            // — the very hydration the gate exists to wait for.
            store.InsertPreset(LocalStorageService.OwnerKindUser, owner, Vid, Pid,
                new PresetRecord { Uid = "dddddddd-4444-4444-4444-444444444444", Name = "SERVER-P", IsDefault = true });
            pullParked.SetResult();
            await load.WaitAsync(TimeSpan.FromSeconds(10));

            // No seed: the scope holds exactly the pulled preset, still the only default.
            var row = Assert.Single(store.GetPresets(LocalStorageService.OwnerKindUser, owner, Vid, Pid));
            Assert.Equal("SERVER-P", row.Name);
            Assert.True(row.IsDefault);
            Assert.Equal("SERVER-P", Assert.Single(vm.Presets).Name);
        }
    }

    [Fact]
    public async Task LoadPresetsForDevice_GuestOwner_SeedsImmediatelyWithoutAwaitingThePull()
    {
        // The pull is parked FOREVER: if the guest path wrongly awaited it, WaitAsync turns
        // the hang into a test failure instead of a timeout.
        var sync = new FakeSettingsSyncService { PullTask = new TaskCompletionSource().Task };
        var (vm, store, owner) = NewVmWithStorage(LocalStorageService.OwnerKindGuest, sync);
        using (store)
        {
            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var row = Assert.Single(store.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
            Assert.True(row.IsDefault);
        }
    }

    [Fact]
    public async Task LoadPresetsForDevice_UserOwnerWithNoPullEverStarted_SeedsStraightThrough()
    {
        // The no-pull default is Task.CompletedTask (guest→user upgrade, or storage restored
        // without a sign-in pull): the gate is a zero-cost pass-through.
        var sync = new FakeSettingsSyncService();
        var (vm, store, owner) = NewVmWithStorage(LocalStorageService.OwnerKindUser, sync);
        using (store)
        {
            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var row = Assert.Single(store.GetPresets(LocalStorageService.OwnerKindUser, owner, Vid, Pid));
            Assert.True(row.IsDefault);
        }
    }

    // ── Two physical units of the same model share one preset list ───────────
    // Mirrors two connected BLAZE mice: same Vid/Pid/SettingsProductId, different
    // DeviceKey/SettingsDeviceKey. DeviceDetailsViewModel is a DI singleton reused across
    // device switches in the real app, so switching `vm.Device` between the two units and
    // calling LoadPresetsForDevice again is the realistic simulation, not two separate VMs.

    [Fact]
    public async Task LoadPresetsForDevice_TwoPhysicalUnitsOfSameModel_SeeTheIdenticalPresetList()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var unitA = FakeDevice("unit-a");
            var unitB = FakeDevice("unit-b");

            vm.Device = unitA;
            await vm.LoadPresetsForDeviceAsync();
            var namesFromA = vm.Presets.Select(p => (p.Id, p.Name)).ToList();

            vm.Device = unitB;
            await vm.LoadPresetsForDeviceAsync();
            var namesFromB = vm.Presets.Select(p => (p.Id, p.Name)).ToList();

            // Both units see the same auto-created default preset — same id, same name.
            Assert.Equal(namesFromA, namesFromB);
        }
    }

    [Fact]
    public async Task LoadPresetsForDevice_PresetCreatedOnOneUnit_VisibleOnTheOtherUnit()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var unitA = FakeDevice("unit-a");
            var unitB = FakeDevice("unit-b");

            vm.Device = unitA;
            await vm.LoadPresetsForDeviceAsync();
            store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "Created on A" });

            vm.Device = unitB;
            await vm.LoadPresetsForDeviceAsync();

            Assert.Contains(vm.Presets, p => p.Name == "Created on A");
        }
    }

    [Fact]
    public async Task LoadPresetsForDevice_EachUnitKeepsItsOwnAppliedPresetSelection()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var unitA = FakeDevice("unit-a");
            var unitB = FakeDevice("unit-b");

            var aid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "A", IsDefault = true });
            var bid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, new PresetRecord { Name = "B" });

            // Unit A has "A" applied, unit B has "B" applied — same shared list, different
            // per-physical-unit selection (device_applied_preset stays scoped by device_key).
            store.SetAppliedPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-a", aid);
            store.SetAppliedPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-b", bid);

            vm.Device = unitA;
            await vm.LoadPresetsForDeviceAsync();
            Assert.Equal(aid, vm.SelectedPreset?.Id);

            vm.Device = unitB;
            await vm.LoadPresetsForDeviceAsync();
            Assert.Equal(bid, vm.SelectedPreset?.Id);

            // Switching back to unit A must still show A's own selection, unaffected by B.
            vm.Device = unitA;
            await vm.LoadPresetsForDeviceAsync();
            Assert.Equal(aid, vm.SelectedPreset?.Id);
        }
    }

    // ── IsSetAsDefaultLocked wiring ──────────────────────────────────────────

    // ── Save-Preset dialog search ────────────────────────────────────────────

    [Fact]
    public async Task SavePresetSearchQuery_EmptyByDefault_ShowsAllRows()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync();
            var a = MakeRow(vm.Presets[0].Id, "Alpha");
            vm.Presets.Clear();
            vm.Presets.Add(a);
            vm.Presets.Add(MakeRow(2, "Beta"));
            vm.Presets.Add(MakeRow(3, "Gamma"));

            Assert.Equal(3, vm.FilteredSavePresets.Count);
        }
    }

    [Fact]
    public async Task SavePresetSearchQuery_CaseInsensitiveSubstringMatch_FiltersRows()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            vm.Presets.Add(MakeRow(1, "Alpha"));
            vm.Presets.Add(MakeRow(2, "Beta"));
            vm.Presets.Add(MakeRow(3, "Alphabet"));

            // Hook the filter (LoadPresetsForDevice normally wires it, but the test
            // seeds Presets directly).
            vm.Device = FakeDevice();
            await vm.LoadPresetsForDeviceAsync();
            vm.Presets.Clear();
            vm.Presets.Add(MakeRow(1, "Alpha"));
            vm.Presets.Add(MakeRow(2, "Beta"));
            vm.Presets.Add(MakeRow(3, "Alphabet"));

            vm.SavePresetSearchQuery = "alpha";
            var names = vm.FilteredSavePresets.Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Alpha", "Alphabet" }, names);

            vm.SavePresetSearchQuery = "  BET  ";
            var trimmed = vm.FilteredSavePresets.Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Alphabet", "Beta" }, trimmed);

            vm.SavePresetSearchQuery = "no-hit";
            Assert.Empty(vm.FilteredSavePresets);

            vm.SavePresetSearchQuery = string.Empty;
            Assert.Equal(3, vm.FilteredSavePresets.Count);
        }
    }

    [Fact]
    public async Task SavePreset_Opens_ResetsSearchQuery()
    {
        var (vm, store, owner) = NewVmWithStorage();
        using (store)
        {
            var pid = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
                new PresetRecord { Name = "A" });
            var a = MakeRow(pid, "A");
            vm.Presets.Add(a);
            vm.Device = FakeDevice();
            await vm.SelectPresetCommand.ExecuteAsync(a);
            vm.NotifyPresetChanged();

            vm.SavePresetSearchQuery = "leftover";
            Assert.Equal("leftover", vm.SavePresetSearchQuery);

            vm.SavePresetCommand.Execute(null);

            Assert.True(vm.IsSavePresetOpen);
            Assert.Equal(string.Empty, vm.SavePresetSearchQuery);
        }
    }

    [Fact]
    public void EditPreset_OnDefaultRow_LocksSetAsDefaultToggle()
    {
        var (vm, store, _) = NewVmWithStorage();
        using (store)
        {
            var a = new PresetViewModel { Id = 1, Name = "Default", IsDefault = true };
            var b = new PresetViewModel { Id = 2, Name = "Other" };
            vm.Presets.Add(a);
            vm.Presets.Add(b);
            vm.Device = FakeDevice();

            vm.EditPresetCommand.Execute(a);
            Assert.True(vm.IsSetAsDefaultLocked);
            Assert.True(vm.NewPresetSetAsDefault);
            vm.CancelNewPresetCommand.Execute(null);

            vm.EditPresetCommand.Execute(b);
            Assert.False(vm.IsSetAsDefaultLocked);
            Assert.False(vm.NewPresetSetAsDefault);
            vm.CancelNewPresetCommand.Execute(null);

            vm.OpenNewPresetCommand.Execute(null);
            Assert.False(vm.IsSetAsDefaultLocked);
        }
    }
}
