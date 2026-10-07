# add-stale-pairing-detection — probe findings

Date: 2026-09-02. Probe: `dotnet run --project ./src/ProoveGaming.Device.Testing -- last-arrival`
(`LastArrivalDateProbe`), run locally against PG Rampage BT1 per the plan's gate.

## Verdict

**Option 1 holds — but on `DEVPKEY_Bluetooth_LastConnectedTime`, not `DEVPKEY_Device_LastArrivalDate`.**
The plan's original key is falsified; a better one was found on the same node and verified
end-to-end with a live power cycle.

## Measurements

### 1. `DEVPKEY_Device_LastArrivalDate` is an adapter clock, not a device clock — FALSIFIED

Baseline over every `BTHLE` node on the machine (5 nodes): all five carried the **identical**
value `2026-08-28 18:43:47` — including PG Rampage BT1, which (per the DeviceHost logs in
`%TEMP%\Logs`) reconnected many times after that date, and excluding nothing: the value did not
match boot time either (last boot 2026-08-23 15:56). It records some adapter-wide re-enumeration
event and never moves on a per-device reconnect.

### 2. `DEVPKEY_Bluetooth_LastConnectedTime` (`{2bd67d8b-8beb-48d5-87e0-6cda3428040a}`, pid 11) — CONFIRMED

Found by dumping every property of the live node (`Get-PnpDeviceProperty`). Static evidence,
same 5-node baseline:

| Node | IsConnected | LastConnectedTime |
| --- | --- | --- |
| PG Rampage BT1 | True | today |
| X3-5.4 Mouse | True | today |
| Raster-1 | False | 2026-08-13 |
| DragonFang-1 | False | 2026-06-16 |
| BT5.0 KB | False | 2026-05-29 |

Per-device, persistent, months-old on abandoned pairings — exactly the ghost signal.

Dynamic confirmation (live power cycle of the Rampage during the probe's watch):

```
[09:51:40] CHANGED   IsConnected True -> False   LastConnected -> 2026-09-02 12:51:40
[09:51:44] CHANGED   IsConnected False -> True   LastConnected -> 2026-09-02 12:51:42
```

The property is stamped on **both** the disconnect and the reconnect — semantically "when was
this device last on the link". A ghost that never connects never gets a fresh stamp.

### 3. Timezone quirk — harmless but real

The stamped values read ~3 h ahead of wall clock through `DateTime.FromFileTimeUtc` +
`ToLocalTime` (the events above happened at wall-clock 09:51): Windows appears to store this
property as **local time serialised as FILETIME**, unlike e.g. the battery timestamp at
`{104ea319-…}` pid 7, which reads as true UTC. Consequence for the implementation: elapsed time
computed as `utcNow - value` can be off by up to a day and can even be negative ("future" stamp).
At a 14-day threshold that is noise; a negative elapsed simply reads as fresh — which is the
fail-open direction. No correction is attempted.

### 4. Secondary candidate, not used

`{104ea319-6ee2-4701-bd47-8ddbf425bbe5}` pid 7 (FILETIME on the battery GUID) also moved on
reconnect — likely "battery last reported". Left unused: `LastConnectedTime` is the direct
semantic match and also covers devices that never report a battery.
