# tests

xUnit test projects. They live here rather than under `src/`, but all of them are in `src/ProveGaming.slnx`, and CI runs every one in Release.

| Project | Covers |
|---------|--------|
| `ProoveGaming.App.Tests` | The App: view models, services, local storage, localization parity. |
| `ProoveGaming.Infrastructure.Tests` | Device protocols and discovery, plus the DeviceHost (it has `InternalsVisibleTo` from the DeviceHost). |
| `ProoveGaming.Installer.Tests` | The installer and the uninstaller: install-location rules, permissions, the elevated delete paths, service control. |

Run all of them with `dotnet test ./src/ProveGaming.slnx`, or one project with `dotnet test ./tests/<Project>/<Project>.csproj`. Add `--filter "FullyQualifiedName~<ClassName>"` for a single class.
