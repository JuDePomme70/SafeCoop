# SafeCoop ? Captain of Industry co-op prototype

SafeCoop is an experimental, open-source two-player synchronization mod for
Captain of Industry. It uses the game's official Mods loader and direct,
opt-in Tailscale networking?no router port, cloud game server, account, native
library, process-launching code, dynamic assembly loading, or raw-memory access.

## Current state: 0.4.0

- Verifies both peers use the same game version, SafeCoop version, session code, and SHA-256 fingerprint of the selected save.
- Captures save-affecting commands at scheduling time and sends bounded, in-memory payloads to one verified peer.
- Replays received commands only on the game's input-update thread and prevents echo loops.
- Retries a guest connection while the host is loading and keeps the host ready after a peer disconnects.
- Includes a Windows launcher that installs/configures the mod, produces a host/join code, and shows connection/join/leave status.
- Launcher 0.4.1 and later checks the public GitHub release on startup. It offers one-click updates and verifies the published SHA-256 before replacing itself.

This is still experimental multiplayer software. Use only a copied test save,
keep backups, and do not expect every command family to be reliable yet.

## Security notes

The source intentionally uses only game assemblies already installed with
Captain of Industry. It makes no changes to game binaries and ships none of
them. The only reflection is a documented lookup of the game's public runtime
`OnCommandScheduled` event because it is missing from the current compile-time
modding interface.

SafeCoop is subject to the [Captain of Industry Modding Policy](https://coigame.com/Legal/Modding-Policy).

## Protocol checks

```powershell
dotnet run --project .\ProtocolTests\ProtocolTests.csproj
```

## Mod build

```powershell
dotnet build .\SafeCoop\SafeCoop.csproj -c Release -p:COI_ROOT="C:\Program Files (x86)\Steam\steamapps\common\Captain of Industry"
```

`DeployToModsFolder` is disabled, so compiling never installs the mod into the game.

## Launcher build

```powershell
dotnet publish .\SafeCoopLauncher\SafeCoopLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:COI_ROOT="C:\Program Files (x86)\Steam\steamapps\common\Captain of Industry"
```
