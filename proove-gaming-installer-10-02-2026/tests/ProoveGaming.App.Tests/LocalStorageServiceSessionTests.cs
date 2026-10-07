using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Regression guard for the remember-me bug: signing in used to leave the persisted guest
// app_session row behind, so the next launch resurrected a phantom guest session alongside
// the restored auth and the tray flow signed the user out. ClearSession is the explicit
// cleanup sign-in and the startup guard rely on.
public class LocalStorageServiceSessionTests
{
    // Each test gets a fresh, in-memory DB (the connection lifetime IS the DB lifetime).
    private static LocalStorageService NewInMemory() =>
        new("Data Source=:memory:");

    private static void SaveValidAuth(LocalStorageService storage) =>
        storage.SaveAuth(42, "user@example.com", "Player", "token-abc", DateTime.UtcNow.AddDays(30));

    // Storage-level characterization of the bug's root: nothing implicit removes the
    // session row when auth is saved — sign-in must call ClearSession explicitly.
    [Fact]
    public void SaveAuth_DoesNotRemovePersistedSession()
    {
        using var storage = NewInMemory();
        storage.InitializeSession(Guid.NewGuid());

        SaveValidAuth(storage);

        var (state, _, _) = storage.GetSessionState();
        Assert.Equal(SessionState.Active, state);
    }

    [Fact]
    public void ClearSession_RemovesSessionRow()
    {
        using var storage = NewInMemory();
        storage.InitializeSession(Guid.NewGuid());

        storage.ClearSession();

        var (state, uuid, startedAt) = storage.GetSessionState();
        Assert.Equal(SessionState.None, state);
        Assert.Null(uuid);
        Assert.Null(startedAt);
    }

    [Fact]
    public void ClearSession_RemovesPersistedDevices()
    {
        using var storage = NewInMemory();
        var session = new UserSession(Guid.NewGuid());
        session.Devices.Add(new DeviceRecord
        {
            Name = "PG Mouse",
            DeviceTypeLabel = "Wired Mouse",
            CardType = "mouse",
            VendorId = 0x04D9,
            ProductId = 0xA23F
        });
        storage.Flush(session);

        storage.ClearSession();

        Assert.Empty(storage.GetDevices());
    }

    [Fact]
    public void ClearSession_KeepsSavedAuth()
    {
        using var storage = NewInMemory();
        storage.InitializeSession(Guid.NewGuid());
        SaveValidAuth(storage);

        storage.ClearSession();

        var auth = storage.GetSavedAuth();
        Assert.NotNull(auth);
        Assert.Equal("user@example.com", auth.Email);
        Assert.False(auth.IsExpired);
    }

    [Fact]
    public void ClearSession_KeepsGuestOwnedRows()
    {
        using var storage = NewInMemory();
        var owner = Guid.NewGuid().ToString("D");
        storage.InitializeSession(Guid.NewGuid());
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, 0x04D9, 0xA23F, "", "polling_rate", 500);

        storage.ClearSession();

        var settings = storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, owner, 0x04D9, 0xA23F, "");
        Assert.Equal(500, settings["polling_rate"]);
    }

    // ── Saved sign-in token at rest (docs/plans/fix-repository-audit-findings, S7) ──────────
    //
    // A file database, so a second, raw connection can read exactly what is on disk.

    private const string Token = "bearer-token-123456";

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"pg-auth-{Guid.NewGuid():N}.db");

    private static string ConnectionString(string path) => $"Data Source={path};Pooling=False";

    private static string ReadRawToken(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT token FROM saved_auth WHERE id = 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private static void WriteRawToken(string path, string value)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE saved_auth SET token = $t WHERE id = 1";
        cmd.Parameters.AddWithValue("$t", value);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteDb(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [Fact]
    public void SaveAuth_NeverWritesTheTokenInPlainText()
    {
        var path = NewDbPath();
        try
        {
            using (var storage = new LocalStorageService(ConnectionString(path)))
                storage.SaveAuth(42, "user@example.com", "Player", Token, DateTime.UtcNow.AddDays(30));

            var raw = ReadRawToken(path);
            Assert.DoesNotContain(Token, raw);
            Assert.StartsWith("dpapi1:", raw);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public void SavedToken_RoundTrips()
    {
        var path = NewDbPath();
        try
        {
            using (var storage = new LocalStorageService(ConnectionString(path)))
                storage.SaveAuth(42, "user@example.com", "Player", Token, DateTime.UtcNow.AddDays(30));

            using var reopened = new LocalStorageService(ConnectionString(path));
            var auth = reopened.GetSavedAuth();

            Assert.NotNull(auth);
            Assert.Equal(Token, auth!.Token);
            Assert.Equal("user@example.com", auth.Email);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public void LegacyPlainTextToken_StillSignsIn_AndIsRewrittenProtected()
    {
        // A database written by a build before 2026-09-24.
        var path = NewDbPath();
        try
        {
            using (var storage = new LocalStorageService(ConnectionString(path)))
                storage.SaveAuth(42, "user@example.com", "Player", Token, DateTime.UtcNow.AddDays(30));
            WriteRawToken(path, Token);

            using (var reopened = new LocalStorageService(ConnectionString(path)))
                Assert.Equal(Token, reopened.GetSavedAuth()!.Token);

            var raw = ReadRawToken(path);
            Assert.DoesNotContain(Token, raw);
            Assert.StartsWith("dpapi1:", raw);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public void TokenThatNoLongerDecrypts_IsTreatedAsSignedOut_AndCleared()
    {
        // e.g. the profile was restored onto another PC: DPAPI cannot decrypt it there.
        var path = NewDbPath();
        try
        {
            using (var storage = new LocalStorageService(ConnectionString(path)))
                storage.SaveAuth(42, "user@example.com", "Player", Token, DateTime.UtcNow.AddDays(30));
            WriteRawToken(path, "dpapi1:" + Convert.ToBase64String(new byte[64]));

            using var reopened = new LocalStorageService(ConnectionString(path));

            Assert.Null(reopened.GetSavedAuth());
            Assert.Null(reopened.GetSavedAuth());
        }
        finally { DeleteDb(path); }
    }

    // ── DPAPI failure (review round 2, B1) ──────────────────────────────────────────────────
    //
    // "Cannot protect" must mean "cannot remember", never a failed sign-in or an App that will not
    // start. The ProtectToken seam stands in for a profile whose DPAPI key is unavailable.

    private static string Throws(string _) => throw new CryptographicException("DPAPI key unavailable");

    [Fact]
    public void SaveAuth_WhenProtectionFails_DoesNotThrow_AndClearsAnEarlierSavedSignIn()
    {
        var path = NewDbPath();
        try
        {
            using (var storage = new LocalStorageService(ConnectionString(path)))
            {
                // An earlier "Remember me" for ANOTHER account is on disk.
                storage.SaveAuth(7, "previous@example.com", "Previous", "previous-token", DateTime.UtcNow.AddDays(30));

                storage.ProtectToken = Throws;
                storage.SaveAuth(42, "user@example.com", "Player", Token, DateTime.UtcNow.AddDays(30));

                // Left in place, the next launch would restore the PREVIOUS account's session.
                Assert.Null(storage.GetSavedAuth());
            }
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public void LegacyUpgrade_WhenProtectionFails_StillReturnsTheToken_AndRetriesNextTime()
    {
        var path = NewDbPath();
        try
        {
            using (var storage = new LocalStorageService(ConnectionString(path)))
                storage.SaveAuth(42, "user@example.com", "Player", Token, DateTime.UtcNow.AddDays(30));
            WriteRawToken(path, Token);

            using (var reopened = new LocalStorageService(ConnectionString(path)))
            {
                reopened.ProtectToken = Throws;

                // This runs at startup: it must not throw, and the user stays signed in.
                Assert.Equal(Token, reopened.GetSavedAuth()!.Token);
            }

            // Left as it was, so a later start can upgrade it once DPAPI works again.
            Assert.Equal(Token, ReadRawToken(path));
        }
        finally { DeleteDb(path); }
    }
}
