# Migrate App ↔ DeviceHost gRPC to a named pipe

**Date:** 2026-09-24
**Kind:** `migrate-*`. Existing tests must stay green; new behaviour gets its own tests.
**Origin:** [`fix-repository-audit-findings`](../fix-repository-audit-findings/fix-repository-audit-findings-plan.md), findings **S8** (unauthenticated local gRPC) and **A10** (the endpoint is hard-coded in three places). The requester chose this approach on 2026-09-24 (that plan's Open question 1).

## Goal

The DeviceHost is a `LocalSystem` service that serves unauthenticated, unencrypted HTTP/2 (h2c) gRPC on TCP `localhost:5000`. Two problems follow:
1. Any local process, in any session, can call it, for example to program a mouse button with a macro that types arbitrary keystrokes, persisted in the device.
2. Any process that binds port 5000 before the service starts can both deny service to the host and impersonate it to the App.

This change moves the channel to a Windows **named pipe** with an ACL that admits only interactive users (plus SYSTEM and administrators), refuses network logons, and is closed to low-integrity and AppContainer processes by default. The App also **verifies the pipe's server is the real DeviceHost service** before using it, so a process that grabs the pipe name first can at worst deny service and can never impersonate the host. The pipe name becomes one shared constant, which also retires A10.

## Scope

**In:**
- The DeviceHost's Kestrel listener: named pipe instead of TCP, with the ACL.
- A shared client channel factory, used by both App gRPC clients (`UsbHidDeviceService`, `DeviceCatalogResolver`) and by `DeviceHost.Checker`.
- Server-identity verification.
- Removing the TCP endpoint from both `appsettings.json` files and from `ApiSettings`.
- Tests, and updates to the docs that name `localhost:5000`.

**Out:**
- Authenticating *which program* in the user's session is calling (for example verifying the caller is `ProoveGaming.App.exe`). A same-user process can still connect, exactly as it can drive the App itself. That is a different threat model; see Open questions.
- Restricting access to the console session only (see Open questions).
- Any change to the gRPC contract (`deviceservice.proto`) or to the Installer/Uninstaller code.

## Options considered

1. **Named pipe + ACL + server verification (recommended).** This closes both halves of S8: who may connect (the ACL) and who the App is talking to (verification). It is one new client file, one server helper and a configuration change; there is no new package, because Kestrel's named-pipe transport ships in the ASP.NET Core shared framework since .NET 8. Downside: the verification uses a small amount of P/Invoke (`GetNamedPipeServerProcessId`, `QueryServiceStatusEx`), and a developer running the DeviceHost as a console app needs the check relaxed (Debug builds only).
2. **Named pipe + ACL only.** Simpler, but the App would talk to whatever process created the pipe first, so impersonation by squatting survives, only moved from a port to a pipe name.
3. **Keep TCP and add a per-boot secret** (a random token in an ACL'd file that the App sends as metadata). This stops other users, not same-user processes, and it adds a secret's lifecycle (creation, rotation, reading it before the first call) while leaving port squatting unsolved.

## Recommended approach

Option 1.

**Shared constant and client factory** (`src/ProoveGaming.GrpcDependencies/DeviceHostPipe.cs`, new; the project is already referenced by App, DeviceHost and Checker):
- `public const string PipeName = "ProoveGaming.DeviceHost";` (full path `\\.\pipe\ProoveGaming.DeviceHost`).
- `public static GrpcChannel CreateChannel(bool requireServiceHost)`, plus an `internal` overload taking the pipe name, for tests. It builds `GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { DisposeHttpClient = true, HttpHandler = new SocketsHttpHandler { ConnectCallback = ConnectAsync } })`. `DisposeHttpClient` is required: `GrpcChannel` does not dispose a handler that was passed in unless told to. The address is a placeholder required by HTTP/2 framing; the connect callback decides the real endpoint.
- `ConnectAsync` creates `new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)` and calls `ConnectAsync(ConnectTimeout, cancellationToken)` with **`ConnectTimeout = 2 s`**. Without a timeout, `NamedPipeClientStream.ConnectAsync` waits *indefinitely* for the pipe to exist, which would turn today's fast "connection refused" into a hang whenever the host is absent. The App's existing reconnect loop and per-call deadlines stay as they are.
- When `requireServiceHost` is true and the OS is Windows: read the pipe's server PID (`GetNamedPipeServerProcessId`) and the PID the Service Control Manager reports for `DeviceHostPipe.ServiceName` (`OpenSCManager` / `OpenService(SERVICE_QUERY_STATUS)` / `QueryServiceStatusEx(SC_STATUS_PROCESS_INFO)`; interactive users hold `SERVICE_QUERY_STATUS` under the default service DACL). The decision itself is a pure `internal static bool IsTrustedServer(uint serverPid, uint servicePid)`: both non-zero and equal. On failure, dispose the pipe and throw `IOException("The pipe server is not the ProoveGamingDeviceHost service")`. Grpc.Net.Client surfaces that as `RpcException(Unavailable)`, which the App already treats as "host absent".
- The service name must match the one the installer registers. `InstallLayout.DeviceHostServiceName` lives in `ProoveGaming.Common`, which GrpcDependencies does not reference, so the factory has its own constant, `DeviceHostPipe.ServiceName` (`"ProoveGamingDeviceHost"`). The DeviceHost's `AddWindowsService` uses that constant rather than its own literal, and `DeviceHostServiceNameTests` in App.Tests (the one test project that sees both Common and GrpcDependencies) pins it to the installer's name.

**Server** (`src/ProoveGaming.DeviceHost/Program.cs`, and `src/ProoveGaming.DeviceHost/Helpers/DeviceHostPipeSecurity.cs`, new):
- `builder.WebHost.UseNamedPipes(o => { o.CurrentUserOnly = false; o.PipeSecurity = DeviceHostPipeSecurity.Build(); })`. `CurrentUserOnly` defaults to true, which under `LocalSystem` would admit *only* SYSTEM and lock the App out.
- `builder.WebHost.ConfigureKestrel(k => k.ListenNamedPipe(DeviceHostPipe.PipeName, l => l.Protocols = HttpProtocols.Http2))`.
- `DeviceHostPipeSecurity.Build()` returns a protected (non-inheriting) `PipeSecurity` with:
  - `SYSTEM` full control;
  - `BUILTIN\Administrators` read/write;
  - `NT AUTHORITY\INTERACTIVE` (S-1-5-4) read/write;
  - an explicit **deny** for `NT AUTHORITY\NETWORK` (S-1-5-2). This is defence in depth: .NET's server pipe also sets `PIPE_REJECT_REMOTE_CLIENTS`.
  - Nothing for `Everyone`, `Users`, `Authenticated Users` or `ALL APPLICATION PACKAGES`, so services, batch jobs, network logons and AppContainers are refused. Low-integrity processes are refused by the object's default medium integrity label.
  - **The host's own account** (`WindowsIdentity.GetCurrent().User`) with full control. *(Added during implementation, 2026-09-24.)* Kestrel keeps several pipe instances listening, and creating every instance after the first needs `CreateNewInstance` on this same DACL. For the installed service the account is SYSTEM, which is already covered. For a host run from a developer's console it is the developer, who would otherwise have read/write only, so the host could not start. The integration test found this. It also made the planned test-only `alsoAllow` parameter unnecessary: the test client runs as the same account as its server.
- Delete the `Kestrel` section from `src/ProoveGaming.DeviceHost/appsettings.json`. Left in place, Kestrel would **also** keep listening on TCP 5000.

**Clients:**
- `UsbHidDeviceService` and `DeviceCatalogResolver` replace `GrpcChannel.ForAddress(_serverUrl)` with `DeviceHostConnection.CreateChannel()`. This is a small App helper (`src/ProoveGaming.App/Services/DeviceHostConnection.cs`) that holds the single `RequireServiceHost` rule (`false` under `#if DEBUG`, `true` otherwise), so the two clients cannot disagree. A developer runs the host as a console process, which the SCM does not know about. `_serverUrl` and its constructor reads go, and both clients lose their `ConfigurationManager` constructor parameter (DI resolves the new signatures unchanged).
- Remove `ApiSettings.GrpcProoveGamingDeviceServerUrl` (it is `required`, so every JSON that sets it must drop it) and its entry in `src/ProoveGaming.App/appsettings.json`.
- `DeviceHost.Checker` uses `DeviceHostPipe.CreateChannel(requireServiceHost: false)`, since it is a diagnostic that must also work against a console host, and prints the pipe name instead of the URL.

## File-level change list

Tests that pin existing behaviour already exist (`UsbHidDeviceServiceHostAbsentTests` covers the absent-host contract through the client seam) and must stay green. The new tests land with the change.

- T: `tests/ProoveGaming.Infrastructure.Tests/DeviceHostPipeTests.cs`: **new**. See Test strategy.
- T: `tests/ProoveGaming.App.Tests/DeviceHostServiceNameTests.cs`: **new**. It checks that the service name agrees with the installer's.
- `src/ProoveGaming.App/Services/DeviceHostConnection.cs`: **new**. It holds the App's single `RequireServiceHost` rule.
- `src/ProoveGaming.GrpcDependencies/DeviceHostPipe.cs`: **new**. Pipe name, channel factory, bounded connect, server verification, P/Invoke.
- `src/ProoveGaming.GrpcDependencies/ProoveGaming.GrpcDependencies.csproj`: **edit** only if `InternalsVisibleTo` for `ProoveGaming.Infrastructure.Tests` is needed for the internal overloads.
- `src/ProoveGaming.DeviceHost/Helpers/DeviceHostPipeSecurity.cs`: **new**. The pipe ACL.
- `src/ProoveGaming.DeviceHost/Program.cs`: **edit**. `UseNamedPipes` + `ListenNamedPipe`.
- `src/ProoveGaming.DeviceHost/appsettings.json`: **edit**. Remove the `Kestrel` section.
- `src/ProoveGaming.App/Services/UsbHidDeviceService.cs`, `src/ProoveGaming.App/Services/DeviceCatalogResolver.cs`: **edit**. Use the factory; drop `_serverUrl`.
- `src/ProoveGaming.App/ApiSettings.cs`, `src/ProoveGaming.App/appsettings.json`: **edit**. Remove `GrpcProoveGamingDeviceServerUrl`.
- `tests/ProoveGaming.App.Tests/ConfigurationManagerTests.cs`: **edit**. The hostile-config fixture drops the removed property.
- `src/ProoveGaming.DeviceHost.Checker/Program.cs`: **edit**. Use the factory.
- `CLAUDE.md` (Architecture: "App → DeviceHost via gRPC at `http://localhost:5000`"), `README.md` (How the pieces fit together): **edit**. Name the pipe and the ACL.
- `docs/CONTEXT.md`: **edit**, done with this plan: **DeviceHost pipe** and **Pipe server verification** are defined.
- `docs/plans/fix-repository-audit-findings/fix-repository-audit-findings-plan.md`: **edit**. Dated note on S8 and A10 pointing here.

## Acceptance criteria

- [ ] Given the installed service is running, when `netstat -ano` is run, then nothing listens on TCP port 5000, and `[System.IO.Directory]::GetFiles('\\.\pipe\') -match 'ProoveGaming.DeviceHost'` finds the pipe.
- [ ] Given the installed App and service, when the user connects a supported mouse, then its card appears and settings apply exactly as before.
- [ ] Given the service is stopped, when the App runs, then within the existing retry cadence it reports the host as unavailable, and no call blocks longer than its deadline or the 2 s connect bound (no hang). Given the service is started again, then the App reconnects without a restart.
- [ ] Given `DeviceHostPipeSecurity.Build()`, then its DACL is protected and contains exactly: SYSTEM full control; Administrators read/write; INTERACTIVE read/write; a deny for NETWORK; and the host's own account with full control (SYSTEM again for the installed service). There is no rule for Everyone, Users, Authenticated Users, SERVICE or ALL APPLICATION PACKAGES.
- [ ] Given the DeviceHost is started from a developer's console, then it starts and serves the pipe (every pipe instance can be created).
- [ ] Given a pipe server that is *not* the SCM's `ProoveGamingDeviceHost` process, when a Release-mode client (`requireServiceHost: true`) connects, then the call fails with `RpcException` (`Unavailable`) and no request reaches that server.
- [ ] Given a channel from `DeviceHostPipe.CreateChannel` that has made a call, when it is disposed, then its pipe connection closes on the server.
- [ ] Given a Debug build of the App and a DeviceHost started from the console, then the App connects (verification relaxed in Debug only).
- [ ] `git grep -n 'localhost:5000\|GrpcProoveGamingDeviceServerUrl' -- src tests CLAUDE.md README.md docs/CONTEXT.md` returns nothing.
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx -c Release` pass.

## Test strategy

Kind is `migrate-*`: existing coverage of the absent-host contract (`UsbHidDeviceServiceHostAbsentTests`, which uses the client seam and never opens a channel) stays green unchanged. New tests go in `tests/ProoveGaming.Infrastructure.Tests/DeviceHostPipeTests.cs`, which already references the DeviceHost and, through it, GrpcDependencies:

1. **ACL shape:** `DeviceHostPipeSecurity.Build()` has exactly the rules in the criterion above, read back with `GetAccessRules(true, true, typeof(SecurityIdentifier))`, and is protected.
2. **End-to-end over a pipe:** start an in-process Kestrel host on a unique pipe name (`$"PgTest.{Guid.NewGuid():N}"`) with a minimal `Device.DeviceBase` implementation of `GetDevices`, using the production `Build()` unchanged. A client from `CreateChannel(pipeName, requireServiceHost: false)` gets the response.
3. **Bounded connect:** with no server on a fresh pipe name, a call with a 10 s deadline fails with `RpcException` in well under 10 s. The 2 s connect bound applies, not the deadline and not forever.
4. **Verification wired:** the same in-process server with `requireServiceHost: true` is refused (`RpcException`, `Unavailable`), because the test process is not the SCM's service.
5. **Decision rule:** `IsTrustedServer`: equal non-zero PIDs → true; different → false; either zero (service not running, or PID unreadable) → false.
6. **Name agreement:** `DeviceHostPipe.ServiceName` equals `InstallLayout.DeviceHostServiceName` (`tests/ProoveGaming.App.Tests/DeviceHostServiceNameTests.cs`). The DeviceHost registers `DeviceHostPipe.ServiceName` itself, so its side cannot drift.
7. `ConfigurationManagerTests` keeps passing with the fixture updated.
8. **Connect bound:** `ConnectTimeout` stays below the App's shortest unary deadline (5 s).
9. **Disposal:** disposing a channel closes its pipe connection on the server (the test server counts open connections with a connection middleware).
10. **No instance creation:** neither INTERACTIVE nor Administrators hold `CreateNewInstance`, `ChangePermissions` or `TakeOwnership`.

**Not automated:** real SYSTEM-owned service behaviour, the SCM PID lookup against the installed service, another user session being refused, and a network client being refused. These need an installed service, a second account or a second machine, so they are covered by QA steps 1–6.

## QA (manual test steps)

Run on a clean Windows 11 VM with the new installer, signed in as a standard user.

1. Install Proove Gaming and open the app. **Expected:** it starts normally.
2. Connect a supported mouse (and keyboard, if available). Change its lighting and one button assignment. **Expected:** the device card appears, and both changes apply on the device.
3. Open a Command Prompt and run `netstat -ano | findstr :5000`. **Expected:** no output (nothing listens on port 5000 any more).
4. Open Services, stop "Proove Gaming Device Host", and wait 15 seconds. **Expected:** the app shows the devices as unavailable and does not freeze; you can still move around the app.
5. Start the service again. **Expected:** within about 15 seconds the devices come back without restarting the app.
6. Restart Windows, sign in, and open the app. **Expected:** devices appear as before.
7. If a second Windows account is available: switch to it (Fast User Switching), open the app there, and change a lighting setting. **Expected:** works as normal for that user. *(Record the result; this confirms the decision in Open question 1.)*

## Review focus

`gRPC contract` (transport), `resource disposal` (pipe streams, channels), `async/await` (bounded connect, cancellation), `null-safety`

## Performance & quality

- **Hot paths:** none new. The channel is created once per client, and HTTP/2 multiplexes every call over the pipe connection, as it did over TCP. Pipe versus loopback TCP is not measurable at this call rate.
- **No UI-thread impact:** connecting happens inside gRPC calls that are already asynchronous and off the UI thread. The only new synchronous work (two P/Invoke lookups) runs once per connection inside the connect callback.
- **Disposal:** on a failed verification or connect, the callback disposes the `NamedPipeClientStream` before throwing. Each service owns and disposes its channel, and the channel disposes its handler (`DisposeHttpClient = true`), so disposing a channel closes its pipe connection. *(Corrected 2026-09-24 after review Round 1, B1: the handler was first left undisposed.)*

## Risks

- **CI logon type.** GitHub runners may not log on as INTERACTIVE. The integration tests do not depend on that: their client runs as the same account as their server, and the production ACL gives that account full control. So the tests use the production ACL unmodified.
- **API names in .NET 10.** `NamedPipeTransportOptions.PipeSecurity` / `CurrentUserOnly` exist since .NET 8. If they have moved, the fallback is `NamedPipeTransportOptions.CreateNamedPipeServerStream` with `NamedPipeServerStreamAcl.Create`. Confirm at implementation time.
- **SCM access.** Reading the service PID relies on interactive users holding `SERVICE_QUERY_STATUS` under the default service DACL that `sc create` applies. Confirm on the VM (QA 2 fails visibly if not: the App would refuse the host).
- **Developer workflow.** A Release App will not talk to a console-run DeviceHost, by design. Debug Apps will. Say so in CLAUDE.md next to the architecture line.
- **Lockstep.** Old App plus new host (or the reverse) would not connect, but the installer always replaces both together, and the existing reconnect loop covers the brief window during an upgrade.
- **Other interactive sessions.** INTERACTIVE includes every signed-in user, not only the console user. See Open questions.

## Open questions

1. **Other signed-in users.** INTERACTIVE admits every interactive session (Fast User Switching), so a second signed-in user can configure the shared devices. That matches today's behaviour for that user, and the devices are physically shared. Accept, or restrict to the console session (it would need a per-connection session check in the host)? **Default if unanswered: accept.**
2. **Caller identity.** Should the host additionally verify the *caller* is `ProoveGaming.App.exe` from the install folder (client PID → image path)? It narrows same-user misuse but does not stop code injected into the App. **Default: no.** Out of scope, recorded for later.

## Review findings

_Filled in by /reviewer._

### Round 1 — Ship: no

Reviewed on 2026-09-24. Scope: the uncommitted Phase 5 working tree on `fix/repository-audit`, on top of `aa33683`.
- **In scope:** every file this plan lists, plus the new files `DeviceHostConnection.cs` and `DeviceHostServiceNameTests.cs`.
- **Also in the working tree, reviewed only for accuracy:**
  - the add-device "needs revisiting" comments in `AddDeviceCardViewModel.cs` and `HomeViewModel.cs` (audit D6/A4b). They are accurate: `AddDevice()` is empty and `DeviceAdded` is never raised.
  - the untracked `docs/plans/refactor-auth-session-flow/` plan (not implemented yet).
- **Not re-reviewed:** Phases 1–4 are committed and were reviewed in the parent plan's Rounds 1–4.

#### Plan conformance
- **Implemented as planned:**
  - the shared constant and factory, with a bounded 2 s connect and the server-PID ↔ SCM-PID check (`DeviceHostPipe.cs`);
  - the ACL (`DeviceHostPipeSecurity.cs`);
  - Kestrel listening on the named pipe only, with the `Kestrel` TCP section removed;
  - both App clients and the Checker moved to the factory;
  - `GrpcProoveGamingDeviceServerUrl` removed from `ApiSettings`, the JSON and the test fixture;
  - `AddWindowsService` using `DeviceHostPipe.ServiceName`;
  - the docs (CLAUDE.md, README, CONTEXT) and the parent plan's S8/A10 note.
- **Acceptance criteria:** the grep criterion and the Release build/test criterion hold. The coder reports 3,112 passed and 0 failed, and this round re-ran none of them. The runtime criteria (netstat, service stop/start, console host) are QA-only and not yet run.
- **Deviates from plan, already reconciled in the live sections:**
  - the host's own account gets full control, and `alsoAllow` is dropped;
  - the new `DeviceHostConnection` helper;
  - the service-name test lives in App.Tests.

  All three are justified and recorded.
- **Plan statement now false:** Performance & quality says "The channel's lifetime is unchanged (owned and disposed by each service)". It is not; see B1.
- **Accepted limitation (per plan: "at worst deny service"):** a standard user can create `\.\pipe\ProoveGaming.DeviceHost` while the service is not running (for example after they stop it, or during an upgrade). Kestrel creates the first instance with first-instance semantics, so the real service then fails to start until that process exits.
  - **Residual risk:** a same-machine denial of service. It cannot impersonate the host, because a Release App refuses a server that isn't the service.
  - **Not verified:** that a failed listen shows up in the host log as a clear message rather than a bare service-start failure.

#### Code
- [x] **B1. The pipe connection handler is never disposed. Each channel re-creation leaks an open pipe connection.** `src/ProoveGaming.GrpcDependencies/DeviceHostPipe.cs:46` (`CreateChannel`). Blocker.
  **Problem:** the channel is now built with a caller-supplied `HttpHandler` (`SocketsHttpHandler`). `GrpcChannelOptions.DisposeHttpClient` defaults to **false**, and its XML doc says so: "This setting is used when a HttpClient or HttpHandler value is specified". So `GrpcChannel.Dispose()` no longer disposes the handler or its connection pool. The old `GrpcChannel.ForAddress(url)` created its own handler, which it always disposed.
  **Why it blocks (concrete scenario):** `UsbHidDeviceService.StartCommunication` (`UsbHidDeviceService.cs:729-733`) disposes and re-creates the channel on every start, and a start follows every deliberate stop (tray hide/restore, navigation).
  - Each cycle orphans the old handler, which still holds a live HTTP/2 connection over the pipe.
  - That connection pins a server pipe instance and a Kestrel connection in the LocalSystem host until the pool's idle timeout (about 1 min) closes it.
  - Repeated tray toggles pile these up, and nothing in our code ever closes them.

  The plan names "resource disposal (pipe streams, channels)" as review focus, and this is the regression that focus exists to catch. It is bounded by the idle timeout, so it degrades the host rather than crashing it, but it is a leak introduced by this change.
  **Solution options:**
  - A (correct, one line): set `DisposeHttpClient = true` in the `GrpcChannelOptions` in `DeviceHostPipe.CreateChannel`. The factory creates the handler and the channel is its only user, so the channel must own it. Also correct the Performance & quality sentence.
  - B (not recommended): have each caller keep and dispose the handler. This spreads ownership across three call sites for no gain.

#### Tests
- [x] **Disposal is untested.** Add a `DeviceHostPipeTests` case: count connections on the test server with a Kestrel connection middleware on the `ListenNamedPipe` endpoint (`listen.Use(next => async ctx => { Interlocked.Increment(ref opened); try { await next(ctx); } finally { Interlocked.Increment(ref closed); } })`). Then make one call, dispose the channel, and assert the connection closes within a few seconds. It fails today (B1) and passes with option A. Project: `tests/ProoveGaming.Infrastructure.Tests`.
- [x] **The ACL test does not pin the security-relevant rights.** `PipeSecurity_AdmitsOnly…` (`DeviceHostPipeTests.cs:49`) only checks that INTERACTIVE and Administrators have *an* Allow rule.
  - The property that actually stops other processes from squatting while the service runs is that neither group holds `CreateNewInstance`, `ChangePermissions` or `TakeOwnership`.
  - If a later edit widened INTERACTIVE to `FullControl`, any user could open extra instances of the live pipe, and every test would stay green.
  - Assert that those three rights are absent from both groups' Allow rules. Project: `tests/ProoveGaming.Infrastructure.Tests`.

#### Nits
- `docs/audits/2026-08-21-api-needs.md:60` still says the DeviceHost listens on `localhost:5000`. It is a dated audit, so leave it. It is noted here only so a later grep over `docs/` is not read as a missed reference.

#### What's done well
- **Bounded connect.** The 2 s `ConnectTimeout` is justified in a comment, and a test pins it below the App's shortest deadline. The absent-host path stays a fast `Unavailable` rather than becoming a hang.
- **Squatting cannot impersonate the host.** Server verification is a pure `IsTrustedServer` rule, tested with a theory. It fails closed on an unknown PID, and the end-to-end test proves no request reaches a non-service server (`server.Calls == 0`).
- **Integration test on the real ACL.** The in-process Kestrel test runs over a real pipe with the production ACL unmodified. It found the CreateNewInstance bug before shipping, so the fix is a real finding, not a guess.
- **One rule, one place.** The service name has one constant, and the DeviceHost registers it itself. The Debug/Release verification rule lives in one App helper, so the two clients cannot disagree.
- **Kestrel TCP section removed.** Leaving it would have kept port 5000 open alongside the pipe.

#### Verdict
Ship: no. One blocker: the handler leak on channel re-creation, a one-line fix. Everything else conforms to the plan, and the security design is sound. First: (1) B1, `DisposeHttpClient = true`, plus the plan's disposal sentence; (2) the disposal test; (3) tighten the ACL rights test. Then run QA steps 1–7 on a clean VM. Before merge, run `/security-review` over the whole branch: earlier phases touched the elevated Installer/Uninstaller.

### Round 2 — Ship: yes

Reviewed on 2026-09-24. Scope: the uncommitted working tree after the Round 1 fixes. It also covers ten `.csproj` files that changed at 18:46:28 outside any plan (see S1).

**Evidence:**
- Full Release suite: 3,115 passed, 0 failed, 1 skipped (Infrastructure 1,145, Installer 127, App 1,843). The run started after the package bumps, so it covers them.
- `dotnet list ./src/ProveGaming.slnx package --vulnerable --include-transitive` reports no vulnerable packages in any project.

#### Plan conformance
- **Round 1 items, verified closed:**
  - **B1:** `DisposeHttpClient = true` at `src/ProoveGaming.GrpcDependencies/DeviceHostPipe.cs:49`, with a comment explaining why.
  - **Disposal test:** `DisposingTheChannel_ClosesItsPipeConnection` (`DeviceHostPipeTests.cs:108`) counts server connections with a Kestrel connection middleware. It failed on the pre-fix code, which reproduces B1, and passes now.
  - **ACL rights test:** `PipeSecurity_LetsNoConnectingGroupCreateInstancesOrRewriteTheAcl` (`:82`) pins `CreateNewInstance`, `ChangePermissions` and `TakeOwnership` as absent for INTERACTIVE and Administrators.
- **Live plan sections:** reconciled. The factory description names `DisposeHttpClient`, the Performance & quality disposal sentence is corrected with a dated note, Test strategy gains items 9–10, and an acceptance criterion covers disposal.
- **Acceptance criteria:** every automated criterion holds. The runtime criteria (netstat, service stop/start, console host, a second user) remain QA-only on a clean VM.
- **Accepted limitation (unchanged from Round 1):** a squatter on the pipe name while the service is stopped can stop the service from starting. That is a denial of service only; it cannot impersonate the host.

#### Code
- [ ] **S1. Package upgrades that are in no plan, mixed into this change.** Should-fix (process, not code).
  - **Files:**
    - `src/ProoveGaming.App/`, `Common`, `DeviceHost`, `GrpcDependencies`, `Infrastructure`, `Installer` and `Uninstaller` `.csproj`;
    - `tests/*/*.csproj`.
  - **Problem / why it matters:** every one of these files was rewritten in the same second. That is a Visual Studio NuGet "update all", not part of this plan. It bumps:
    - Avalonia 12.1.0 → 12.1.3;
    - Microsoft.Extensions.* and Microsoft.Data.Sqlite 10.0.10 → 10.0.12;
    - Grpc.* 2.80/2.82 → 2.84, and Google.Protobuf 3.35.1 → 3.36.2;
    - Svg.Skia 5.1.1 → 5.2.3, and Svg.Controls.Skia.Avalonia → 12.0.0.17;
    - InTheHand.BluetoothLE → 4.0.45;
    - Microsoft.NET.Test.Sdk → 18.10.1;
    - **xunit.runner.visualstudio 3.1.5 → 4.0.0**, a major version.

    It also reflows the multi-line `<None Include=…>` items in the test projects onto single lines. Nothing is broken: the suite is green, the xUnit v2 tests are still discovered under runner 4.0.0 (same counts as before), and the vulnerability audit is clean. The risk is reviewability and rollback:
    - these bumps change the shipped installer payload (App, DeviceHost, Installer, Uninstaller);
    - if one regresses on hardware (Avalonia rendering, HID/Bluetooth through InTheHand, gRPC over the new pipe), it could not be told apart from the transport change;
    - a revert of either would take the other with it.
  - **Fix:**
    - A (recommended): commit the package bumps as their own commit, separate from the named-pipe work (for example "chore: update NuGet packages"). Record them in the parent plan beside A12 (central package management), which is still undecided.
    - B: if the bumps were not intended, restore the `.csproj` files from `HEAD`, keeping only this plan's `InternalsVisibleTo` line in `ProoveGaming.GrpcDependencies.csproj`. Then re-run the suite.

#### Tests
- none. The Round 1 gaps are closed. The disposal test's 10 s poll is generous for Release CI, and the "exactly one open connection" assertion is deterministic: one channel makes one HTTP/2 connection.

#### Nits
- none new.

#### What's done well
- **B1 fixed properly.** It was reproduced with a failing test before the fix, and the fix is one owned option rather than per-caller handler bookkeeping.
- **Meaningful ACL test.** It pins the specific rights that matter against squatting and ACL rewrites, not just "some Allow rule exists".
- **Honest plan history.** The plan was corrected where it had become false, and a dated note explains the change instead of silently rewriting it.

#### Verdict
Ship: yes, for the named-pipe migration. There are no blockers, all Round 1 findings are closed and verified, and the full Release suite plus the vulnerability audit are green. First:
1. S1: separate (or drop) the NuGet bumps before committing.
2. QA steps 1–7 on a clean VM with the installed service.
3. `/security-review` over the whole branch before merge, because earlier phases touched the elevated Installer and Uninstaller.
