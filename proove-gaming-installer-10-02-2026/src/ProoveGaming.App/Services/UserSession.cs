using System;
using System.Collections.Generic;
using System.Threading;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Services;

public sealed class UserSession
{
    // Devices is mutated off the UI thread: HomeViewModel.PersistDeviceCard runs on whichever
    // thread reported the device (the gRPC discovery handler), while the UI thread snapshots the
    // list for a guest flush. List mutations compact the backing array, so an unsynchronised
    // snapshot taken mid-mutation can duplicate or drop an element rather than merely miss the
    // tail. Every mutation and every snapshot goes through this gate; use SnapshotDevices rather
    // than enumerating Devices from a thread that is not the only writer.
    private readonly Lock _gate = new();
    private int _nextId = 1;

    public Guid SessionId { get; }
    public List<DeviceRecord> Devices { get; } = new();

    public UserSession(Guid sessionId)
    {
        SessionId = sessionId;
    }

    public UserSession(Guid sessionId, IEnumerable<DeviceRecord> existing) : this(sessionId)
    {
        foreach (var record in existing)
        {
            Devices.Add(record);
            if (record.Id >= _nextId)
                _nextId = record.Id + 1;
        }
    }

    /// <summary>
    /// Replaces the record carrying <paramref name="record"/>'s id in place, or appends it when no
    /// such record exists (a card stored for the first time, or one whose id belongs to another
    /// owner's store).
    /// </summary>
    /// <remarks>
    /// The only way to add a device: one locked mutation instead of a remove-then-add pair, so a
    /// concurrent snapshot can
    /// never observe the card missing, the card keeps its id and its slot, and _nextId does not
    /// churn on every re-persist — so a guest card's position survives a transport switch.
    /// </remarks>
    public int UpsertDevice(DeviceRecord record)
    {
        lock (_gate)
        {
            var index = record.Id > 0 ? Devices.FindIndex(d => d.Id == record.Id) : -1;
            if (index >= 0)
            {
                Devices[index] = record;
                return record.Id;
            }

            record.Id = _nextId++;
            Devices.Add(record);
            return record.Id;
        }
    }

    public void RemoveDevice(int id)
    {
        lock (_gate)
            Devices.RemoveAll(d => d.Id == id);
    }

    /// <summary>
    /// Updates one record's low-battery announcement guard in place (0 = re-armed), so the next
    /// guest flush persists it. A full <see cref="UpsertDevice"/> here would rebuild the record
    /// from the card on the UI thread for a two-ints change; this touches only the field.
    /// </summary>
    public void SetDeviceLowBatteryBand(int id, int band)
    {
        lock (_gate)
        {
            var index = Devices.FindIndex(d => d.Id == id);
            if (index >= 0)
                Devices[index].LowBatteryAnnouncedBand = band;
        }
    }

    /// <summary>Point-in-time copy for callers that enumerate the list off the mutating thread.</summary>
    public DeviceRecord[] SnapshotDevices()
    {
        lock (_gate)
            return Devices.ToArray();
    }
}
