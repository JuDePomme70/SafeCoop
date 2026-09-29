using System;
using System.IO;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Core.GameLoop;
using Mafi.Core.Input;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.SaveGame;
using SafeCoop.Network;

namespace SafeCoop;

/// <summary>
/// Official-loader entry point. A session starts only when a player explicitly
/// enables it in the mod's settings for a copied test save.
/// </summary>
public sealed class SafeCoopMod : IMod, IDisposable {
    private readonly ModManifest manifest;
    private readonly ModJsonConfig jsonConfig;
    private LanSessionCoordinator? lanSession;
    private DependencyResolver? resolver;
    private InputScheduler? inputScheduler;
    private Event<IInputCommand>? scheduledCommandsEvent;
    private IGameLoopEvents? gameLoopEvents;
    private bool applyingRemoteCommand;

    public SafeCoopMod(ModManifest manifest) {
        this.manifest = manifest;
        jsonConfig = new ModJsonConfig(this);
        Log.Info("SafeCoop 0.3.0 loaded. Tailscale sessions are disabled until configured in mod settings.");
    }

    public ModManifest Manifest => manifest;
    public bool IsUiOnly => false;
    public Option<IConfig> ModConfig => Option.None;
    public ModJsonConfig JsonConfig => jsonConfig;

    public void RegisterPrototypes(ProtoRegistrator registrator) {
        // No game data is changed in the foundation build.
    }

    public void RegisterDependencies(DependencyResolverBuilder builder, ProtosDb protosDb, bool gameWasLoaded) {
        // SafeCoop uses only existing, official game services in the observation build.
    }

    public void EarlyInit(DependencyResolver resolver) {
        // No initialization is required before the map is ready.
    }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded) {
        if (!LanSessionSettings.TryCreate(jsonConfig, out var settings, out var message)) {
            if (!string.IsNullOrEmpty(message)) {
                Log.Warning(message);
            }
            return;
        }

        string saveFingerprint;
        try {
            var savePath = resolver.Resolve<SaveManager>().LastSaveFilePath.ValueOrNull;
            if (string.IsNullOrWhiteSpace(savePath) || !File.Exists(savePath)) {
                Log.Warning("SafeCoop needs the currently loaded save file before it can start a session.");
                return;
            }

            saveFingerprint = Protocol.SessionFingerprint.FromFile(savePath);
        }
        catch (Exception exception) {
            Log.Warning($"SafeCoop could not verify the selected save: {exception.Message}");
            return;
        }

        this.resolver = resolver;
        inputScheduler = resolver.Resolve<InputScheduler>();
        // The scheduler exposes this hook at runtime, but the current modding
        // reference omits it from the compile-time surface. We use only this
        // named, public game event and never inspect or modify game memory.
        var scheduledProperty = inputScheduler.GetType().GetProperty("OnCommandScheduled", BindingFlags.Instance | BindingFlags.Public);
        scheduledCommandsEvent = scheduledProperty?.GetValue(inputScheduler) as Event<IInputCommand>;
        gameLoopEvents = resolver.Resolve<IGameLoopEvents>();
        if (scheduledCommandsEvent == null) {
            Log.Warning("SafeCoop could not attach to the game command scheduler.");
            ClearGameHooks();
            return;
        }

        scheduledCommandsEvent.AddNonSaveable(this, OnCommandScheduled);
        gameLoopEvents.InputUpdate.AddNonSaveable(this, OnInputUpdate);
        lanSession = new LanSessionCoordinator(settings!, saveFingerprint, Log.Info, Log.Warning);
        lanSession.Start();
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) {
        // This mod currently stores no data in a save file.
    }

    public void Dispose() {
        ClearGameHooks();
        lanSession?.Dispose();
        lanSession = null;
        Log.Info("SafeCoop session closed.");
    }

    private void OnCommandScheduled(IInputCommand command) {
        if (applyingRemoteCommand || !command.AffectsSaveState || lanSession == null || !lanSession.IsConnected) {
            return;
        }

        try {
            var payload = GameCommandCodec.Serialize(command);
            if (!lanSession.QueueLocalCommand(payload)) {
                Log.Warning("SafeCoop did not send a game action because the peer connection is unavailable.");
            }
        }
        catch (Exception exception) {
            Log.Warning($"SafeCoop did not send {command.GetType().Name}: {exception.Message}");
        }
    }

    private void OnInputUpdate(GameTime gameTime) {
        if (lanSession == null || inputScheduler == null || resolver == null) {
            return;
        }

        // A bounded drain keeps a malformed/very busy peer from monopolizing a game frame.
        for (var index = 0; index < 16 && lanSession.TryDequeueRemoteCommand(out var remoteCommand); index++) {
            try {
                var command = GameCommandCodec.Deserialize(remoteCommand.Payload, resolver);
                applyingRemoteCommand = true;
                inputScheduler.ScheduleInputCmd(command);
            }
            catch (Exception exception) {
                Log.Warning($"SafeCoop rejected a remote game action: {exception.Message}");
            }
            finally {
                applyingRemoteCommand = false;
            }
        }
    }

    private void ClearGameHooks() {
        if (scheduledCommandsEvent != null) {
            scheduledCommandsEvent.RemoveNonSaveable(this, OnCommandScheduled);
            scheduledCommandsEvent = null;
        }

        if (gameLoopEvents != null) {
            gameLoopEvents.InputUpdate.RemoveNonSaveable(this, OnInputUpdate);
            gameLoopEvents = null;
        }

        inputScheduler = null;
        resolver = null;
    }
}
