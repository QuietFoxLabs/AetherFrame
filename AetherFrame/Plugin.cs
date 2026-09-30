using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Hosting;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Commands;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Fonts;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.Domain.Profiles;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows;
using AetherFrame.Windows.Theme;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
#if AETHERFRAME_NETWORK_PREVIEW
using AetherFrame.Hosting.Network;
using AetherFrame.Hosting.Network.Publishing;
using AetherFrame.Services.Network.Personas;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Network.Transport;
using AetherFrame.Windows.Network;
#endif

namespace AetherFrame;

public sealed class Plugin : IAsyncDalamudPlugin, IAsyncDisposable
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IReliableFileStorage FileStorage { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
#if AETHERFRAME_NETWORK_PREVIEW
    [PluginService] internal static ITextureReadbackProvider TextureReadback { get; private set; } = null!;
#endif

    public PluginConfiguration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("AetherFrame");

    private readonly PlateLibraryService plateLibrary;
    private readonly TemplateLibraryService templateLibrary;
    private readonly CharacterIdentityService characterIdentityService;
    private readonly KeyboardShortcutService keyboardShortcutService;
    private readonly ImageTextureCache imageTextureCache;
    private readonly AssetStorageService assetStorageService;
    private readonly ProfileFontService fontService;
    private readonly ProceduralTextureCache proceduralTextureCache;
    private readonly BuiltInArtTextureCache builtInArtTextureCache;
    private readonly PlateThumbnailService thumbnailService;
    private readonly PlateThumbnailTextures thumbnailTextures;
    private readonly PlateThumbnailService templateThumbnailService;
    private readonly PlateThumbnailTextures templateThumbnailTextures;
    private readonly EditorSurfaceCoordinator editorSurfaces;

    // The Plate menu both editors' action bar shares (interface task 1).
    private readonly EditorPlateMenu editorPlateMenu;
    private readonly PlateLibraryWindow plateLibraryWindow;
    private readonly BasicProfileEditorWindow basicProfileEditorWindow;
    private readonly ProfileEditorWindow profileEditorWindow;
    private readonly ProfileViewWindow profileViewWindow;
    private readonly PackageImportWindow packageImportWindow;
    private readonly PlatePackageService packageService;
    private readonly BasicGuidance basicGuidance;
    private readonly AetherFrameCommandRegistration commands;
    private readonly IAetherFrameLog log;

    // The interface's typography, the tutorial (its state, and the overlay windows that show it),
    // and what the tutorial needs to know about this install once the Library has loaded.
    private readonly AetherFonts fonts;
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly OnboardingCoordinator onboarding;
    private readonly TutorialOverlay tutorialOverlay;
    private readonly bool configurationFound;
    private readonly bool configurationUnreadable;

    // Every file-writing operation the plugin owns (Library, Templates, package import/export),
    // so unloading can let running ones finish before disposing what they use.
    private readonly OwnedOperations ownedOperations = new();

#if AETHERFRAME_NETWORK_PREVIEW
    // The persona session (NETWORK2's N2-5b; docs/networking/DecisionRegister.md, K3 and P3): the
    // capability probe, the persona files' lock, the registry and the audit, all off the framework
    // thread. Its operations are owned operations too. Closed first when unloading.
    private readonly PersonaSession personaSession;

    // The share check (N2-6c): a Plate's saved state as it would be shared, signed into the outbox
    // on this PC. Its preparation is an owned operation; the window is disposed with the others.
    private readonly ShareCheckWindow shareCheckWindow;

    // Opting characters in and out of sharing (N2-9b), over the connection to the sharing server
    // (N2-9a, decision R2). Its operations are persona-session operations, which unloading waits
    // for, so the connection is disposed only after every owned operation has ended.
    private readonly SharingConnection sharingConnection;

    // Publishing the Active Plate when it is saved (N2-9c), driven once a frame while drawing,
    // and the Sharing window that shows it.
    private readonly LivePublisher livePublisher;
    private readonly SharingWindow sharingWindow;
#endif

    public Plugin()
    {
        DalamudServices.Initialize(PluginInterface, PlayerState, Framework, Log, KeyState, TextureProvider, DataManager, UnlockState, ObjectTable);
        // Every message and exception passes LogPrivacy first: no character binding file (named by
        // the character's Content ID) is ever named in the log.
        log = new RedactingAetherFrameLog(new DalamudAetherFrameLog(Log));

        // A damaged configuration file never stops AetherFrame from loading: it starts from the
        // defaults instead (the file holds only the guidance flag below, and is rewritten readable).
        var savedConfiguration = StartupGuard.TryLoad(
            () => PluginInterface.GetPluginConfig() as PluginConfiguration, "its configuration", log, out var configurationUnreadable);
        Configuration = savedConfiguration ?? new PluginConfiguration();
        configurationFound = savedConfiguration is not null;
        this.configurationUnreadable = configurationUnreadable;

        // No configuration at all: the guidance below saves one right away, before the Library is
        // read, so the tutorial's first-run decision (made after the load) is marked as pending in
        // it. A later launch then judges this install by its Library, never by that file. A file
        // that exists but can't be read is an established install's (only v0.1.6 or later ever
        // wrote one): that is recorded at once, before the rewrite replaces the damaged file, so
        // the tutorial is never offered unasked, whatever the Library holds.
        if (savedConfiguration is null)
        {
            Configuration.Tutorial = new TutorialPreferences
            {
                Install = configurationUnreadable ? TutorialInstallKind.ExistingInstall : TutorialInstallKind.PendingDecision,
            };
        }

        // The one-time Basic suggestion: decided now from the configuration alone (a current one's
        // stored flag always wins), so it's ready before any window can ask for Advanced.
        basicGuidance = new BasicGuidance(new ConfigurationGuidanceStore(Configuration, log));
        basicGuidance.Resolve(BasicGuidance.OriginOf(
            savedConfiguration is not null, Configuration.Version, PluginConfiguration.CurrentVersion, configurationUnreadable));

        // Everything from here on is undone if a later step throws (see StartupGuard): Dalamud never
        // disposes a plugin whose constructor failed.
        var startup = new StartupGuard(log);
        try
        {
            var paths = new PlateStoragePaths(PluginInterface.ConfigDirectory.FullName);

            // Every Library operation starts on the framework thread, as profile IO always has:
            // its synchronous prefix runs there, and once the first file step completes elsewhere
            // (Dalamud writes from the thread pool) its continuations — and the Changed, PlateSaved
            // and PlateDeleted events — run on that thread, so every subscriber is thread-safe.
            // An operation stops between files if unloading ever stops waiting for it (see
            // OwnedOperations), and a save whose temporary file Dalamud has left stuck open is
            // written directly instead of failing until the game restarts (see
            // StuckTempFallbackFileStore).
            var fileStore = new ShutdownGuardedFileStore(
                new StuckTempFallbackFileStore(new ReliablePlateFileStore(FileStorage), log), ownedOperations);
            plateLibrary = new PlateLibraryService(paths, fileStore, log, dispatch: work => Framework.Run(work), operations: ownedOperations);
            templateLibrary = new TemplateLibraryService(paths, fileStore, plateLibrary, log, dispatch: work => Framework.Run(work), operations: ownedOperations);

            var jobCatalog = new JobCatalog();
            characterIdentityService = new CharacterIdentityService(jobCatalog);
            profileService = new ProfileService(plateLibrary);

            assetStorageService = new AssetStorageService(
                paths.AssetsDirectory, paths.AssetStagingDirectory, new AssetMetadataStore(paths.AssetMetadataDirectory, log), ImageFormatSupport.IsSupported, log);
            imageTextureCache = new ImageTextureCache(assetStorageService);
            fontService = new ProfileFontService();
            startup.OnFailure("fonts", fontService.Dispose);
            proceduralTextureCache = new ProceduralTextureCache();
            startup.OnFailure("pattern textures", proceduralTextureCache.Dispose);
            builtInArtTextureCache = new BuiltInArtTextureCache();
            startup.OnFailure("artwork textures", builtInArtTextureCache.Dispose);
            var renderResources = new ProfileRenderResources(imageTextureCache, fontService, proceduralTextureCache, builtInArtTextureCache, jobCatalog);
            var fileDialogManager = new FileDialogManager();
            var basicFileDialogManager = new FileDialogManager();

            // No thumbnail generator yet (no offscreen renderer exists): cards use their fallback.
            thumbnailService = new PlateThumbnailService(paths.ThumbnailsDirectory, generator: null, log);
            thumbnailTextures = new PlateThumbnailTextures(thumbnailService);
            startup.OnFailure("Plate thumbnails", thumbnailService.Dispose);
            plateLibrary.PlateSaved += thumbnailService.Invalidate;
            plateLibrary.PlateDeleted += thumbnailService.Remove;

            // Same (currently inert) thumbnail pipeline as Plates, kept in a separate directory only
            // to avoid a Guid-collision surface between a Template id and a Plate id.
            templateThumbnailService = new PlateThumbnailService(paths.TemplateThumbnailsDirectory, generator: null, log);
            templateThumbnailTextures = new PlateThumbnailTextures(templateThumbnailService);
            startup.OnFailure("Template thumbnails", templateThumbnailService.Dispose);

            editorSession = new EditorSession(profileService, assetStorageService, imageTextureCache, log, () => ImGui.GetFrameCount());
            editorSurfaces = new EditorSurfaceCoordinator(() =>
            {
                editorSession.CommitPendingEdits();
                editorSession.EndInteraction();
            });
            var gameTitleCatalog = new GameTitleCatalog();
            var textMeasurer = new ProfileTextMeasurer(fontService);
            editorSession.IdentityMeasurer = textMeasurer;
            var basicIdentitySession = new BasicIdentitySession(
                profileService, editorSession, characterIdentityService, textMeasurer, gameTitleCatalog);
            var basicEditorSession = new BasicEditorSession(
                profileService, editorSession, assetStorageService, basicIdentitySession, characterIdentityService, jobCatalog);
            keyboardShortcutService = new KeyboardShortcutService();
            startup.OnFailure("keyboard shortcuts", keyboardShortcutService.Dispose);

            // .aetherframe export/import: local files only, chosen by the player; nothing networked.
            packageService = new PlatePackageService(
                plateLibrary, assetStorageService, paths, $"AetherFrame {PluginInterface.Manifest.AssemblyVersion}", ImageFormatSupport.IsSupported, log,
                operations: ownedOperations);

            // Undo, Redo, Save and Revert as both editors' shared action bar offers them.
            var documentCommands = new EditorDocumentCommands(profileService, editorSession);

            // The Plate menu both editors' action bar shares (interface task 1): My Plates' card menu's
            // actions, run and reported where the Plate is being edited, with its own Export dialog.
            var editorPlateFileDialogs = new FileDialogManager();
            editorPlateMenu = new EditorPlateMenu(
                new PlateMenu(
                    new PlateActions(plateLibrary, templateLibrary, packageService, profileService, editorSession, new PlateOperationRunner(log), log),
                    plateLibrary, profileService, editorSession, thumbnailService, editorPlateFileDialogs),
                plateLibrary, characterIdentityService, documentCommands, editorPlateFileDialogs, plateId => profileViewWindow!.ShowPlate(plateId));

            basicProfileEditorWindow = new BasicProfileEditorWindow(
                profileService, editorSession, basicEditorSession, imageTextureCache, renderResources, basicFileDialogManager, gameTitleCatalog, jobCatalog,
                OpenAdvancedEditor, OpenMyPlates, editorSurfaces, documentCommands, keyboardShortcutService, editorPlateMenu);
            profileEditorWindow = new ProfileEditorWindow(
                profileService, editorSession, keyboardShortcutService, renderResources, fileDialogManager, OpenBasicEditor, OpenMyPlates, editorSurfaces, documentCommands, editorPlateMenu);
            editorSurfaces.Attach(basicProfileEditorWindow, profileEditorWindow);
            // The one place "this character's Active Plate" is resolved (the viewer's default request).
            var activePlates = new ActivePlateResolver(plateLibrary, () => characterIdentityService.CurrentCharacter);
            profileViewWindow = new ProfileViewWindow(profileService, plateLibrary, activePlates, renderResources, OpenMyPlates);

            packageImportWindow = new PackageImportWindow(packageService, renderResources, (plateId, name) => plateLibraryWindow!.OnPlateImported(plateId, name));
            plateLibraryWindow = new PlateLibraryWindow(
                plateLibrary, templateLibrary, profileService, editorSession, characterIdentityService, thumbnailService, thumbnailTextures,
                templateThumbnailService, templateThumbnailTextures, renderResources, OpenBasicEditor, OpenAdvancedEditor, () => editorSurfaces.ActiveSurface,
                basicGuidance, profileViewWindow.ShowPlate, profileViewWindow.ShowDocument,
                packageService, new FileDialogManager(), packageImportWindow.Begin, log);

            WindowSystem.AddWindow(plateLibraryWindow);
            WindowSystem.AddWindow(basicProfileEditorWindow);
            WindowSystem.AddWindow(profileEditorWindow);
            WindowSystem.AddWindow(profileViewWindow);
            WindowSystem.AddWindow(packageImportWindow);

            // The interface's own fonts (the game's Axis face for headings; built by Dalamud when it can).
            fonts = new AetherFonts(PluginInterface.UiBuilder.FontAtlas);
            AetherFonts.Current = fonts;
            startup.OnFailure("interface fonts", fonts.Dispose);

            // The tutorial: its state lives in the configuration beside the guidance flag; its
            // windows go after every other AetherFrame window, so the spotlight sees this frame's
            // anchors. Whether to offer it is decided once the Library has loaded (see LoadAsync).
            onboarding = new OnboardingCoordinator(new ConfigurationTutorialStore(Configuration, log), TutorialScript.Chapters, TutorialScript.Version);
            var tutorialHost = new TutorialHost(this);
            tutorialOverlay = new TutorialOverlay(
                onboarding, tutorialHost, [plateLibraryWindow, basicProfileEditorWindow, profileEditorWindow, profileViewWindow, packageImportWindow]);

            // A player taking the tour is being shown both editors: the one-time Basic suggestion
            // would only get in the way of a step, so it counts as handled once the tour starts.
            onboarding.Started += basicGuidance.MarkHandled;
            var helpMenu = new HelpMenu(onboarding, tutorialHost);
            plateLibraryWindow.Help = helpMenu;
            basicProfileEditorWindow.Help = helpMenu;
            profileEditorWindow.Help = helpMenu;
            foreach (var window in tutorialOverlay.Windows)
            {
                WindowSystem.AddWindow(window);
            }

            startup.OnFailure("windows", WindowSystem.RemoveAllWindows);

            // /aetherframe and its /af alias, both on this one handler.
            commands = new AetherFrameCommandRegistration(new DalamudCommandRegistrar(CommandManager), log);
            commands.Register(new AetherFrameCommandHandler(ToggleMainUi, profileViewWindow.ShowActivePlate, ShowVersion));
            startup.OnFailure("commands", commands.Unregister);

            // Character details refresh on their own every half second; a login or logout also
            // refreshes them immediately.
            ClientState.Login += OnLogin;
            ClientState.Logout += OnLogout;
            startup.OnFailure("login events", () =>
            {
                ClientState.Login -= OnLogin;
                ClientState.Logout -= OnLogout;
            });

            PluginInterface.UiBuilder.Draw += DrawUi;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
            startup.OnFailure("drawing", () =>
            {
                PluginInterface.UiBuilder.Draw -= DrawUi;
                PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
                PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
            });

#if AETHERFRAME_NETWORK_PREVIEW
            // Starts in the background. Until the player acts it writes only its lock file (creating
            // the persona folder) and the capability probe's scratch files, in the temp folder and
            // deleted again. A load that fails after this closes it, and its lock is released by
            // whichever of its own work ends last.
            var configDirectory = PluginInterface.ConfigDirectory.FullName;
            personaSession = PersonaSessionHost.Create(configDirectory, log, ownedOperations);
            startup.OnFailure("personas", personaSession.Close);

            // Personas are hidden (V4): a character's key is made by the Sharing window, and no
            // window lists or switches them any more (N2-9c).
            personaSession.Start();

            // The share check (N2-6c), reached from a Plate's menu in My Plates. Image preparation's
            // known-answer check runs once a session, when the first check needs it (D5's N2-6 note).
            var imageCodec = new DalamudImageCodec(TextureProvider, TextureReadback);
            var managedImages = new ManagedImageFiles(assetStorageService);
            var preparationCheck = new Lazy<Task<bool>>(() => ImagePreparationCheck.RunAsync(imageCodec, log, ownedOperations));
            ShareCheck NewShareCheck() => new(new ShareCheckSeams
            {
                OpenSavedPlate = plateLibrary.OpenDocumentForEditing,
                Prewarm = fontService.EnsurePrewarmed,
                Measurements = new RendererPlateMeasurements(renderResources, assetStorageService),
                SelfTest = () => preparationCheck.Value,
                Prepare = (requirements, cancellation) => ImagePreparer.PrepareAllAsync(requirements, managedImages, imageCodec, cancellation),
                BeginOperation = () => ownedOperations.TryBegin(out var lease) ? lease : null,
                Stopping = ownedOperations.Stopping,
                Log = log.Information,
            });
            shareCheckWindow = new ShareCheckWindow(NewShareCheck(), TextureProvider);
            WindowSystem.AddWindow(shareCheckWindow);
            plateLibraryWindow.CheckSharing = shareCheckWindow.Open;
            editorPlateMenu.Menu.CheckSharing = shareCheckWindow.Open;

            // Sharing (N2-9b), reached from My Plates' header. Nothing is sent until the player
            // turns sharing on for a character, and then only on the player's action (R2).
            sharingConnection = new SharingConnection(AetherFrameBuildInfo.Current.ProductVersion);
            var characterSharing = new CharacterSharing(
                personaSession.TryRun,
                new SharingStateFile(PersonaSessionHost.PersonasDirectory(configDirectory)),
                new PublicationFiles(PersonaSessionHost.PersonasDirectory(configDirectory)),
                sharingConnection.Client,
                AetherFrameBuildInfo.Current.ProductVersion,
                () => DateTimeOffset.UtcNow,
                ownedOperations.Stopping,
                log.Information);
            // Publishing the Active Plate live (N2-9c): a save, a new Active Plate, or sharing
            // starting or resuming builds a candidate with a share check of its own, a frame at a
            // time, and hands it to the sharing service.
            livePublisher = new LivePublisher(characterSharing, NewShareCheck(), () => characterIdentityService.CurrentCharacter, plateLibrary.GetActivePlateId);
            plateLibrary.PlateSaved += livePublisher.PlateSaved;
            sharingWindow = new SharingWindow(characterSharing, livePublisher, TextureProvider, personaSession, () => characterIdentityService.CurrentCharacter, System.IO.Path.Combine(PersonaSessionHost.PersonasDirectory(configDirectory), SharingStateFile.FileName));
            WindowSystem.AddWindow(sharingWindow);
            plateLibraryWindow.OpenSharing = () => sharingWindow.IsOpen = true;
            plateLibraryWindow.IsShared = plateId => characterIdentityService.CurrentCharacter is { } shown
                && characterSharing.View.Find(shown.ContentId) is { Stage: SharingStage.Shared }
                && plateLibrary.GetActivePlateId(shown.ContentId) == plateId;
#endif

            // Names the exact build in dalamud.log, so a stale dev DLL is obvious.
            Log.Information($"==={AetherFrameBuildInfo.Current.Describe()} loaded ({PluginInterface.Manifest.Name})===");

            startup.Complete();
        }
        catch
        {
            startup.RollBack();
            throw;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        // Temporary files left by an import or a thumbnail generation the game closing interrupted;
        // only AetherFrame's own, under names only it writes, and before any UI can start another.
        packageService.SweepStaging();
        assetStorageService.SweepStaging();
        thumbnailService.SweepTemporaryFiles();
        templateThumbnailService.SweepTemporaryFiles();

        // The Library loads on a framework tick (its reads and any migration write are dispatched
        // there, like every Library operation); this task is what that tick's work completes.
        try
        {
            await plateLibrary.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ThrowIfLoadStoppedAsync(ex, cancellationToken).ConfigureAwait(false);

            // Nothing on disk is touched by a failed load; My Plates says it couldn't load.
            Log.Error(LogPrivacy.ForLog(ex), "AetherFrame could not load the Plate Library.");
            plateLibraryWindow.MarkLoadFailed();
        }

        try
        {
            await templateLibrary.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ThrowIfLoadStoppedAsync(ex, cancellationToken).ConfigureAwait(false);

            // Nothing on disk is touched by a failed load; Templates says the saved ones couldn't
            // load, and built-in Templates stay usable, so Create Plate still works.
            Log.Error(LogPrivacy.ForLog(ex), "AetherFrame could not load the Template Library.");
        }

        // Now that the Library's state is known: is this a new player (offer the tutorial) or an
        // established install (never offer unasked)? Decided on the framework thread, where the
        // tutorial's state is read while drawing; reads the Library's counts only, changes nothing
        // in it, and can never fail the load.
        try
        {
            await Framework.RunOnTick(ResolveFirstRun, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A stopped load tears the plugin down here, like the Library loads above; any other
            // failure only means the offer isn't made this launch.
            await ThrowIfLoadStoppedAsync(ex, cancellationToken).ConfigureAwait(false);
            Log.Warning(LogPrivacy.ForLog(ex), "AetherFrame could not decide whether to offer the tutorial.");
        }
    }

    private void ResolveFirstRun()
    {
        // Both Libraries must have loaded for their counts to mean anything: an install whose only
        // saved work is a Template must not look new because the Template Library failed to load.
        var libraryLoaded = plateLibrary.IsLoaded && templateLibrary.IsLoaded;
        var plateCount = plateLibrary.IsLoaded ? plateLibrary.GetOrderedPlates().Count : 0;
        var userTemplateCount = 0;
        if (templateLibrary.IsLoaded)
        {
            foreach (var template in templateLibrary.GetOrderedTemplates())
            {
                if (!template.IsBuiltIn)
                {
                    userTemplateCount++;
                }
            }
        }

        onboarding.ResolveFirstRun(configurationFound, configurationUnreadable, libraryLoaded, plateCount, userTemplateCount);
        Log.Information($"AetherFrame tutorial: {onboarding.LastDecision} (install {onboarding.Preferences.Install}, status {onboarding.Preferences.Status}).");
    }

    /// <summary>
    /// Every frame: the tutorial's windows follow its state, then every window draws. Dalamud
    /// guards each window's Draw but not its PreDraw, so if one throws there, whatever AetherFrame
    /// style is still pushed is popped here before the exception reaches Dalamud, and no other
    /// window is drawn in AetherFrame's colors.
    /// </summary>
    private void DrawUi()
    {
        try
        {
            tutorialOverlay.Update();
#if AETHERFRAME_NETWORK_PREVIEW
            livePublisher.OnFrame();
#endif
            WindowSystem.Draw();
            editorPlateMenu.EndFrame();
        }
        finally
        {
            AetherStyle.RecoverOutstanding();
        }
    }

    /// <summary>
    /// A load canceled by Dalamud, cut short because the plugin is already unloading, or
    /// interrupted by the game closing (the framework stops running dispatched work, which ends the
    /// load with a cancellation of its own) isn't a load failure: per
    /// <see cref="IAsyncDalamudPlugin.LoadAsync"/>, it ends the load with
    /// <see cref="OperationCanceledException"/>. Dalamud never disposes an instance whose LoadAsync
    /// threw (it disposes only the service scope), so the plugin's own teardown — the draw and
    /// login hooks, windows, textures, fonts and the shutdown of any operation already started — runs
    /// here first.
    /// </summary>
    private async Task ThrowIfLoadStoppedAsync(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested && !ownedOperations.IsShuttingDown)
        {
            return;
        }

        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(LogPrivacy.ForLog(ex), "AetherFrame could not clean up after its load was stopped.");
        }

        throw new OperationCanceledException("AetherFrame stopped loading because it is unloading.", exception, cancellationToken);
    }

    /// <summary>
    /// Dalamud calls this directly on whichever thread is unloading the plugin (it only moves a sync
    /// plugin's Dispose to the framework thread), and keeps the plugin's services alive until it
    /// returns. Everything the windows and <see cref="WindowSystem"/> use is torn down on the
    /// framework thread, where Draw runs; the waits in between never depend on that thread.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
#if AETHERFRAME_NETWORK_PREVIEW
        // Before anything else, the persona session takes no new work, and gives up its lock now,
        // or as soon as its own work in flight ends: never tied to the plugin's other operations,
        // so a reloaded AetherFrame finds the lock free as early as possible (P3).
        personaSession.Close();
#endif

        // First, nothing new can start: no drawing, menus, commands, login events or shortcuts.
        await OnFrameworkThreadAsync("UI shutdown", StopNewWork).ConfigureAwait(false);

        // Then any save, rename, import, export, … already running finishes before anything it uses
        // is disposed (see PluginShutdown for what happens if one outlasts the timeout).
        await PluginShutdown.RunAsync(
            ownedOperations,
            OwnedOperations.DefaultShutdownTimeout,
            () => OnFrameworkThreadAsync("window and texture disposal", DisposeUnusedByOperations),
            DisposeUsedByOperations,
            log).ConfigureAwait(false);
    }

    private Task OnFrameworkThreadAsync(string name, Action teardown) =>
        FrameworkThreadTeardown.RunAsync(
            name, teardown, Framework.RunOnFrameworkThread, () => Framework.IsFrameworkUnloading, FrameworkThreadTeardown.DefaultTimeout, log);

    private void StopNewWork()
    {
        PluginInterface.UiBuilder.Draw -= DrawUi;

        // A running tutorial is remembered where it stopped; nothing else of it needs the game.
        onboarding.Suspend();
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        commands.Unregister();
        WindowSystem.RemoveAllWindows();
        keyboardShortcutService.Dispose();
    }

    /// <summary>Windows, textures and fonts: drawing only, never used by an owned file operation.
    /// (An import still running keeps its staged files: the import window disposes them only once
    /// the import ends.)</summary>
    private void DisposeUnusedByOperations()
    {
        plateLibraryWindow.Dispose();
        basicProfileEditorWindow.Dispose();
        profileEditorWindow.Dispose();
        profileViewWindow.Dispose();
        packageImportWindow.Dispose();
#if AETHERFRAME_NETWORK_PREVIEW
        shareCheckWindow.Dispose();
        plateLibrary.PlateSaved -= livePublisher.PlateSaved;
        livePublisher.Dispose();
        sharingWindow.Dispose();
#endif
        imageTextureCache.Clear();
        thumbnailTextures.Clear();
        templateThumbnailTextures.Clear();
        templateThumbnailService.Dispose();
        proceduralTextureCache.Dispose();
        builtInArtTextureCache.Dispose();
        fontService.Dispose();
        fonts.Dispose();
    }

    /// <summary>The Plate thumbnail service: a save or delete still running calls into it (via
    /// PlateSaved and PlateDeleted), so it's disposed only once every owned operation has ended.</summary>
    private void DisposeUsedByOperations()
    {
        thumbnailService.Dispose();
#if AETHERFRAME_NETWORK_PREVIEW
        sharingConnection.Dispose();
#endif
    }

    private void OnLogin() => characterIdentityService.InvalidateCharacterInfo();

    private void OnLogout(int type, int code) => characterIdentityService.InvalidateCharacterInfo();

    /// <summary><c>/aetherframe version</c> (or <c>/af version</c>): the running build, in chat.</summary>
    private static void ShowVersion() => ChatGui.Print(AetherFrameBuildInfo.Current.Describe());

    /// <summary>The main entry point is My Plates.</summary>
    public void ToggleMainUi() => plateLibraryWindow.Toggle();

    /// <summary>
    /// The editors' My Plates button: opens My Plates, or — when it's already open, perhaps behind
    /// the editor — brings it to the front. Never closes it.
    /// </summary>
    private void OpenMyPlates()
    {
        plateLibraryWindow.IsOpen = true;
        plateLibraryWindow.BringToFront();
    }

    /// <summary>
    /// Basic and Advanced are two surfaces over one editing session; only one is open at a time.
    /// Every way into the Basic editor comes through here, and having opened it means the one-time
    /// Basic suggestion is no longer needed.
    /// </summary>
    private void OpenBasicEditor()
    {
        basicGuidance.MarkHandled();
        editorSurfaces.Show(EditorSurfaceKind.Basic);
    }

    private void OpenAdvancedEditor() => editorSurfaces.Show(EditorSurfaceKind.Advanced);

    /// <summary>
    /// What the tutorial sees of the interface (a value, read fresh every frame) and the few safe
    /// things a card may do: open or bring forward a window the player could open themselves.
    /// Nothing here touches a Plate.
    /// </summary>
    private sealed class TutorialHost(Plugin plugin) : ITutorialHost
    {
        public TutorialContextSnapshot Snapshot()
        {
            var profile = plugin.profileService.CurrentProfile;
            ProfileElement? selected = null;
            if (profile is not null && plugin.editorSession.SelectedElementId is { } selectedId)
            {
                foreach (var element in profile.Elements)
                {
                    if (element.Id == selectedId)
                    {
                        selected = element;
                        break;
                    }
                }
            }

            var library = plugin.plateLibraryWindow;
            return new TutorialContextSnapshot(
                MyPlatesOpen: library.IsOpen,
                TemplatesViewOpen: library.IsOpen && library.TemplatesViewShowing,
                TemplateChooserOpen: library.IsOpen && library.TemplateChooserShowing,
                ActiveEditor: plugin.editorSurfaces.ActiveSurface,
                PlateOpen: profile is not null,
                PlateCount: plugin.plateLibrary.IsLoaded ? plugin.plateLibrary.GetOrderedPlates().Count : 0,
                ElementSelected: selected is not null,
                TextElementSelected: selected is TextProfileElement);
        }

        public void Perform(TutorialAction action)
        {
            switch (action)
            {
                case TutorialAction.OpenMyPlates:
                    plugin.OpenMyPlates();
                    break;
                case TutorialAction.OpenBasicEditor when plugin.profileService.CurrentProfile is not null:
                    plugin.OpenBasicEditor();
                    break;
                case TutorialAction.OpenAdvancedEditor when plugin.profileService.CurrentProfile is not null:
                    plugin.OpenAdvancedEditor();
                    break;
                case TutorialAction.OpenBasicEditor:
                case TutorialAction.OpenAdvancedEditor:
                    // No Plate is open to edit: the way there is My Plates.
                    plugin.OpenMyPlates();
                    break;
            }
        }
    }

    /// <summary>The tutorial's preferences, persisted in the plugin configuration beside the guidance flag.</summary>
    private sealed class ConfigurationTutorialStore(PluginConfiguration configuration, IAetherFrameLog log) : ITutorialPreferencesStore
    {
        public TutorialPreferences Preferences => configuration.Tutorial ??= new TutorialPreferences();

        /// <summary>Never throws: failing to remember only means the offer may be shown again.</summary>
        public void Save()
        {
            configuration.Version = Math.Max(configuration.Version, PluginConfiguration.CurrentVersion);
            try
            {
                PluginInterface.SavePluginConfig(configuration);
            }
            catch (Exception ex)
            {
                log.Error(ex, "AetherFrame could not save its configuration.");
            }
        }
    }

    /// <summary>The guidance flag, persisted in the plugin configuration.</summary>
    private sealed class ConfigurationGuidanceStore(PluginConfiguration configuration, IAetherFrameLog log) : IBasicGuidanceStore
    {
        public bool BasicGuidanceHandled
        {
            get => configuration.BasicGuidanceHandled;
            set => configuration.BasicGuidanceHandled = value;
        }

        /// <summary>Never throws: failing to remember the flag only means the suggestion may show again.</summary>
        public void Save()
        {
            // A configuration saved by a newer version keeps its version (and, through the
            // extension data, its settings): it is still that version's file, only with this flag.
            configuration.Version = Math.Max(configuration.Version, PluginConfiguration.CurrentVersion);
            try
            {
                PluginInterface.SavePluginConfig(configuration);
            }
            catch (Exception ex)
            {
                log.Error(ex, "AetherFrame could not save its configuration.");
            }
        }
    }
}
