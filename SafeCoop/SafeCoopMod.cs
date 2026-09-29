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
    private LanSessionSettings? pendingSessionSettings;
    private bool applyingRemoteCommand;

    public SafeCoopMod(ModManifest manifest) {
        this.manifest = manifest;
        jsonConfig = new ModJsonConfig(this);
        Log.Info("SafeCoop 0.3.1 loaded.");
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
        // LastSaveFilePath is populated only after this mod's Initialize call.
        // Starting here used to prevent every real session from ever opening.
        pendingSessionSettings = settings;
        Log.Info("SafeCoop is configured and waiting for the selected save to finish loading.");
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) {
        // This mod currently stores no data in a save file.
    }

    public void Dispose() {
        ClearGameHooks();
        pendingSessionSettings = null;
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
        TryStartSessionAfterSaveLoad();

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

    private void TryStartSessionAfterSaveLoad() {
        if (lanSession != null || pendingSessionSettings == null || resolver == null) {
            return;
        }

        try {
            var savePath = resolver.Resolve<SaveManager>().LastSaveFilePath.ValueOrNull;
            if (string.IsNullOrWhiteSpace(savePath) || !File.Exists(savePath)) {
                return;
            }

            var saveFingerprint = Protocol.SessionFingerprint.FromFile(savePath);
            lanSession = new LanSessionCoordinator(pendingSessionSettings, saveFingerprint, Log.Info, Log.Warning);
            pendingSessionSettings = null;
            lanSession.Start();
        }
        catch (Exception exception) {
            // The game may expose the SaveManager before the file is fully ready.
            // Keep waiting rather than turning a short load delay into a failed session.
            Log.Warning($"SafeCoop is waiting for the selected save: {exception.Message}");
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
