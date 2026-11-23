using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using YouTubeAutoPost.Models;
using YouTubeAutoPost.Services;
using WinRT.Interop;

namespace YouTubeAutoPost;

public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly YouTubeAuthService _auth = new();
    private readonly YouTubeUploader _uploader = new();
    private readonly YouTubeDuplicateService _duplicateService = new();
    private readonly ObservableCollection<string> _log = new();
    private AppSettings _settings = new();
    private Google.Apis.YouTube.v3.YouTubeService? _youtube;
    private CancellationTokenSource? _syncCts;
    private TaskCompletionSource? _syncStopped;
    private TaskCompletionSource? _maintenanceStopped;
    private CancellationTokenSource? _maintenanceCts;
    private IntPtr _windowHandle;
    private bool _trayIconAdded;
    private IntPtr _trayIconHandle;
    private NativeMethods.SubclassProc? _trayWindowProc;
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 42;
    private uint _taskbarCreatedMessage;
    private AppWindow? _appWindow;
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? _autoPublishTimer;
    private bool _allowClose;
    private bool _isUiInitialized;
    private bool _maintenanceRunning;
    private readonly bool _startHiddenToTray;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".webm",
        ".mpeg", ".mpg", ".3gp", ".ts", ".mts"
    };

    private sealed record LocalVideoMatch(
        string Path, string Title, long Length, DateTime LastWriteTimeUtc, DuplicateVideo YouTubeVideo);

    public MainWindow(bool startHiddenToTray = false)
    {
        _startHiddenToTray = startHiddenToTray;
        AppLogger.Write("MainWindow : début du constructeur.");
        // Le pré-diagnostic XamlReader.Load est désactivé ici : il instancie de nombreux
        // contrôles avant le XAML compilé et pourrait perturber le chargement de la fenêtre.
        AppLogger.Write("Pré-diagnostic XAML désactivé pour tester InitializeComponent dans un état WinUI vierge.");

        var xamlLoadCompleted = 0;
        _ = Task.Run(async () =>
        {
            for (var tick = 1; tick <= 20 && Volatile.Read(ref xamlLoadCompleted) == 0; tick++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (Volatile.Read(ref xamlLoadCompleted) == 0)
                    AppLogger.Write($"ATTENTION : InitializeComponent est toujours en cours après {tick * 3} secondes.");
            }
        });

        try
        {
            AppLogger.Write("Appel de InitializeComponent()...");
            InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);
            Interlocked.Exchange(ref xamlLoadCompleted, 1);
            AppLogger.Write("MainWindow : InitializeComponent terminé.");
        }
        catch (Exception ex)
        {
            AppLogger.Write($"ÉCHEC XAML MainWindow.InitializeComponent — type={ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}", ex);
            var line = ex.GetType().GetProperty("LineNumber")?.GetValue(ex);
            var position = ex.GetType().GetProperty("LinePosition")?.GetValue(ex);
            AppLogger.Write($"Détails XAML : ligne={line ?? "inconnue"}, position={position ?? "inconnue"}");
            AppLogger.Write("Lancement du diagnostic XAML élément par élément.");
            XamlDiagnostics.Diagnose(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                AppLogger.Write($"Exception interne : {inner.GetType().FullName}, HRESULT=0x{inner.HResult:X8}", inner);
            throw;
        }

        LocalizationService.Apply(Content as DependencyObject);
        AutoPublishToggle.OnContent = LocalizationService.Translate("Automatique");
        AutoPublishToggle.OffContent = LocalizationService.Translate("Manuel");
        var titleIconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (File.Exists(titleIconPath))
            TitleBarIcon.Source = new BitmapImage(new Uri(titleIconPath));
        AppLogger.Write($"Langue interface : {LocalizationService.LanguageCode}.");
        if (LogList is not null)
        {
            LogList.ItemsSource = _log;
            AppLogger.Write("Contrôle LogList initialisé.");
        }
        else
        {
            AppLogger.Write("AVERTISSEMENT : LogList est null après InitializeComponent.");
        }
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        if (_appWindow is not null)
        {
            _appWindow.Title = LocalizationService.Translate("YouTube AutoPost");
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(540, 575));
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 540;
                presenter.PreferredMinimumHeight = 575;
                presenter.PreferredMaximumWidth = 540;
                presenter.PreferredMaximumHeight = 575;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = true;

                // Barre de titre personnalisée, alignée sur les dimensions et les gestes
                // de Windows 11. La zone de déplacement est gérée par SetTitleBar.
                presenter.SetBorderAndTitleBar(true, false);
                AppLogger.Write("Barre de titre personnalisée Windows 11 active ; bouton Agrandir absent.");
            }
            _appWindow.Closing += AppWindow_Closing;
        }
        AppLogger.Write("MainWindow : initialisation de l'icône de notification.");
        InitializeTray();
        RootLayout.Loaded += MainWindow_Loaded;
        AppLogger.Write("MainWindow : constructeur terminé.");
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        NativeMethods.ShowWindow(WindowNative.GetWindowHandle(this), NativeMethods.SW_MINIMIZE);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        // Reprend exactement le comportement du bouton Fermer Windows :
        // masquage dans la zone de notification ou fermeture selon les réglages.
        HideToTray();
    }

    private void CloseWindowButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        CloseWindowButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
        CloseWindowButton.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private void CloseWindowButton_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        CloseWindowButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        CloseWindowButton.ClearValue(Control.ForegroundProperty);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppLogger.Write("Chargement des paramètres et de l'interface.");
        _settings = await _settingsStore.LoadAsync();
        StartupRegistration.SetEnabled(_settings.StartWithWindows);
        if (_startHiddenToTray && _settings.MinimizeToTray)
            NativeMethods.ShowWindow(WindowNative.GetWindowHandle(this), NativeMethods.SW_HIDE);
        FolderTextBox.Text = _settings.FolderPath;
        AutoPublishToggle.IsOn = _settings.AutoPublish;
        TargetModeCombo.SelectedIndex = _settings.TargetMode == "playlist" ? 1 : 0;
        PrivacyCombo.SelectedIndex = _settings.Privacy switch { "public" => 0, "private" => 2, _ => 1 };
        // Les événements SelectionChanged peuvent se déclencher pendant la restauration.
        _isUiInitialized = true;
        PlaylistCombo.IsEnabled = TargetModeCombo.SelectedIndex == 1;

        ImportOAuthButton.Visibility = _auth.HasClientSecrets
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_auth.HasClientSecrets && _auth.HasSavedToken)
        {
            // Réutilise le jeton OAuth mémorisé : s'il est encore valide, aucun clic
            // ni nouvelle authentification ne sont nécessaires.
            try
            {
                await CompleteYouTubeConnectionAsync();
                AddLog("Session YouTube restaurée automatiquement.");
            }
            catch (Exception ex)
            {
                _youtube = null;
                ChannelText.Text = "Non connecté";
                ConnectButton.Visibility = Visibility.Visible;
                AddLog($"La session YouTube doit être renouvelée : {ex.Message}");
            }
        }
        else
        {
            ConnectButton.Visibility = Visibility.Visible;
            if (!_auth.HasClientSecrets)
                AddLog("Ajoute le JSON OAuth fourni par Google pour commencer.");
        }
        ConfigureAutoPublishTimer();
        AppLogger.Write("Chargement initial terminé.");
    }

    private void InitializeTray()
    {
        _windowHandle = WindowNative.GetWindowHandle(this);
        _trayWindowProc = TrayWindowProc;
        if (!NativeMethods.SetWindowSubclass(_windowHandle, _trayWindowProc, new UIntPtr(1), UIntPtr.Zero))
            throw new InvalidOperationException("Impossible d'initialiser l'icône de zone de notification.");

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        _trayIconHandle = File.Exists(iconPath) ? NativeMethods.LoadIconFromFile(iconPath) : IntPtr.Zero;
        if (!TryAddTrayIcon())
            throw new InvalidOperationException("Impossible d'ajouter l'icône dans la zone de notification.");
    }

    private IntPtr TrayWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            // Explorer peut redémarrer (ou recréer la barre des tâches) : Windows
            // supprime alors les icônes natives. On réinscrit la nôtre.
            _trayIconAdded = false;
            TryAddTrayIcon();
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_SHOW_EXISTING_INSTANCE)
        {
            RestoreFromTray();
            return IntPtr.Zero;
        }

        // IsMaximizable=false masque le bouton natif. Bloque aussi le double-clic
        // sur le titre et les commandes système qui pourraient agrandir la fenêtre.
        if (message == NativeMethods.WM_NCLBUTTONDBLCLK &&
            wParam.ToUInt64() == NativeMethods.HTCAPTION)
            return IntPtr.Zero;

        if (message == NativeMethods.WM_SYSCOMMAND &&
            (wParam.ToUInt64() & 0xFFF0UL) == NativeMethods.SC_MAXIMIZE)
            return IntPtr.Zero;

        if (message == TrayCallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64());
            if (notification == NativeMethods.WM_LBUTTONDBLCLK)
                RestoreFromTray();
            else if (notification == NativeMethods.WM_RBUTTONUP || notification == NativeMethods.WM_CONTEXTMENU)
                ShowTrayMenu();
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, NativeMethods.ID_TRAY_OPEN, LocalizationService.Translate("Ouvrir YouTube AutoPost"));
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, NativeMethods.ID_TRAY_SYNC, LocalizationService.Translate("Synchroniser maintenant"));
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | (_settings.StartWithWindows ? NativeMethods.MF_CHECKED : 0), NativeMethods.ID_TRAY_STARTUP, LocalizationService.Translate("Lancer au démarrage de Windows"));
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, NativeMethods.ID_TRAY_QUIT, LocalizationService.Translate("Quitter"));

        NativeMethods.GetCursorPos(out var point);
        NativeMethods.SetForegroundWindow(_windowHandle);
        var selected = NativeMethods.TrackPopupMenu(menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
            point.X, point.Y, 0, _windowHandle, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessage(_windowHandle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        switch (selected)
        {
            case NativeMethods.ID_TRAY_OPEN:
                RestoreFromTray();
                break;
            case NativeMethods.ID_TRAY_SYNC:
                _dispatcherQueue.TryEnqueue(async () => await StartSyncAsync());
                break;
            case NativeMethods.ID_TRAY_STARTUP:
                _dispatcherQueue.TryEnqueue(async () => await ToggleStartupRegistrationAsync());
                break;
            case NativeMethods.ID_TRAY_QUIT:
                _dispatcherQueue.TryEnqueue(QuitApplication);
                break;
        }
    }

    private async Task ToggleStartupRegistrationAsync()
    {
        var enabled = !_settings.StartWithWindows;
        if (!StartupRegistration.SetEnabled(enabled))
        {
            AddLog("Impossible de modifier le démarrage automatique de Windows.");
            return;
        }

        _settings.StartWithWindows = enabled;
        await _settingsStore.SaveAsync(_settings);
        AddLog(enabled ? "Démarrage automatique activé." : "Démarrage automatique désactivé.");
    }

    private bool TryAddTrayIcon()
    {
        if (_trayIconAdded) return true;
        var data = NativeMethods.CreateTrayData(_windowHandle, TrayCallbackMessage, "YouTube AutoPost", _trayIconHandle);
        _trayIconAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        AppLogger.Write(_trayIconAdded
            ? "Icône YouTube AutoPost ajoutée à la zone de notification."
            : "ÉCHEC : impossible d'ajouter l'icône YouTube AutoPost à la zone de notification.");
        return _trayIconAdded;
    }

    private void RemoveTrayIcon()
    {
        if (_trayIconAdded)
        {
            var data = NativeMethods.CreateTrayData(_windowHandle, TrayCallbackMessage, "YouTube AutoPost", _trayIconHandle);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _trayIconAdded = false;
        }

        if (_windowHandle != IntPtr.Zero)
            NativeMethods.RemoveWindowSubclass(_windowHandle, _trayWindowProc!, new UIntPtr(1));
        _trayWindowProc = null;
        if (_trayIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        // Le bouton Fermer masque toujours l'application dans la zone de notification.
        // Seule la commande explicite "Quitter" termine réellement le processus.
        if (!TryAddTrayIcon())
        {
            AddLog("Impossible de masquer l'application : l'icône de zone de notification n'est pas disponible.");
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }

    private void RestoreFromTray()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        Activate();
        NativeMethods.BringWindowToTop(hwnd);
        var focused = NativeMethods.SetForegroundWindow(hwnd);
        AppLogger.Write($"Réactivation demandée depuis une seconde ouverture : focus Windows={(focused ? "accordé" : "refusé")}.");
    }

    private async void QuitApplication()
    {
        _syncCts?.Cancel();
        if (_syncStopped is not null)
            await _syncStopped.Task;
        if (_maintenanceStopped is not null)
            await _maintenanceStopped.Task;

        _allowClose = true;
        RemoveTrayIcon();
        Close();
    }

    private async void ImportOAuth_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            await _auth.ImportClientSecretsAsync(file.Path);
            ImportOAuthButton.Visibility = Visibility.Collapsed;
            ConnectButton.Visibility = Visibility.Visible;
            AddLog("Fichier OAuth importé dans le dossier privé de l'application.");
            StatusText.Text = "Identifiants OAuth prêts. Clique sur Connecter.";
        }
        catch (Exception ex)
        {
            ShowError("Import OAuth impossible", ex);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            await CompleteYouTubeConnectionAsync();
            AddLog("Connexion YouTube réussie.");
        }
        catch (Exception ex)
        {
            _youtube = null;
            ConnectButton.Visibility = Visibility.Visible;
            ShowError("Connexion YouTube impossible", ex);
            ChannelText.Text = "Non connecté";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task CompleteYouTubeConnectionAsync()
    {
        _youtube = await _auth.ConnectAsync(CancellationToken.None);
        var (channel, playlists) = await _auth.LoadDestinationsAsync(_youtube, CancellationToken.None);
        ChannelText.Inlines.Clear();
        ChannelText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = "Connecté",
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        });
        ChannelText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  ·  " });
        ChannelText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = channel is null ? "Aucune chaîne trouvée pour ce compte." : channel.Title
        });
        if (channel is not null)
        {
            ChannelText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  ·  " });
            ChannelText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = channel.CustomUrl is not null
                    ? (channel.CustomUrl.StartsWith("@", StringComparison.Ordinal) ? channel.CustomUrl : "@" + channel.CustomUrl)
                    : channel.Id
            });
        }
        PlaylistCombo.ItemsSource = playlists;
        PlaylistCombo.DisplayMemberPath = "Title";
        PlaylistCombo.SelectedValuePath = "Id";
        var selected = playlists.FirstOrDefault(p => p.Id == _settings.SelectedPlaylistId);
        PlaylistCombo.SelectedItem = selected ?? playlists.FirstOrDefault();
        if (PlaylistCombo.SelectedItem is PlaylistChoice choice)
        {
            _settings.SelectedPlaylistId = choice.Id;
            _settings.SelectedPlaylistTitle = choice.Title;
        }
        ConnectButton.Visibility = Visibility.Collapsed;
        AddLog($"Compte connecté. {playlists.Count} playlist(s) accessible(s).");
        await SaveSettingsAsync();
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _settings.FolderPath = folder.Path;
        FolderTextBox.Text = folder.Path;
        await SaveSettingsAsync();
    }

    private async void TargetMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized) return;
        PlaylistCombo.IsEnabled = TargetModeCombo.SelectedIndex == 1;
        await SaveSettingsAsync();
    }

    private async void SettingsSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized) return;
        await SaveSettingsAsync();
    }

    private void LogsToggle_Click(object sender, RoutedEventArgs e)
    {
        var showLogs = LogsPanel.Visibility != Visibility.Visible;
        LogsPanel.Visibility = showLogs ? Visibility.Visible : Visibility.Collapsed;
        MainContentScrollViewer.Visibility = showLogs ? Visibility.Collapsed : Visibility.Visible;
        LogsToggleButton.Content = LocalizationService.Translate(showLogs ? "Masquer les logs" : "Afficher les logs");
        if (showLogs && _log.Count > 0)
            LogList.ScrollIntoView(_log[^1]);
    }

    private async void AutoPublish_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;
        _settings.AutoPublish = AutoPublishToggle.IsOn;
        await SaveSettingsAsync();
        ConfigureAutoPublishTimer();
        AddLog(_settings.AutoPublish
            ? "Publication automatique activée : vérification à chaque heure pile."
            : "Publication automatique désactivée.");
    }

    private void ConfigureAutoPublishTimer()
    {
        _autoPublishTimer ??= _dispatcherQueue.CreateTimer();
        _autoPublishTimer.Tick -= AutoPublishTimer_Tick;
        _autoPublishTimer.Tick += AutoPublishTimer_Tick;
        _autoPublishTimer.Stop();
        if (!_settings.AutoPublish || !_isUiInitialized)
            return;

        _autoPublishTimer.IsRepeating = false;
        var now = DateTime.Now;
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
        _autoPublishTimer.Interval = nextHour - now;
        _autoPublishTimer.Start();
        AppLogger.Write($"Prochaine synchronisation automatique prévue à {nextHour:HH:mm}.");
    }

    private async void AutoPublishTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _autoPublishTimer?.Stop();
        ConfigureAutoPublishTimer();
        await StartSyncAsync(automatic: true);
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await StartSyncAsync();

    private async void Clean_Click(object sender, RoutedEventArgs e) =>
        await RunCleanupAsync();

    private async Task RunCleanupAsync()
    {
        if (_syncCts is not null || _maintenanceRunning) return;
        if (_youtube is null)
        {
            StatusText.Text = "Connecte d'abord ton compte YouTube.";
            RestoreFromTray();
            return;
        }

        var localFolder = FolderTextBox.Text;
        if (string.IsNullOrWhiteSpace(localFolder) || !Directory.Exists(localFolder))
        {
            await ShowInfoDialogAsync("Dossier local introuvable",
                "Sélectionne d'abord un dossier vidéo accessible. Aucun nettoyage n'a été effectué.");
            return;
        }

        _maintenanceRunning = true;
        _maintenanceStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _maintenanceCts = new CancellationTokenSource();
        Grid.SetColumn(StopButton, 2);
        CleanButton.Visibility = Visibility.Collapsed;
        SyncButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        LogsToggleButton.IsEnabled = false;

        var youtubeDeleted = 0;
        var youtubeUnavailable = 0;
        var localDeleted = 0;
        var localKept = 0;

        try
        {
            StatusText.Text = "Étape 1/2 : analyse des doublons sur YouTube…";
            AddLog("Nettoyage démarré : première passe sur les vidéos YouTube.");
            var groups = await _duplicateService.FindDuplicateGroupsAsync(_youtube, _maintenanceCts!.Token);

            foreach (var group in groups)
            {
                var remaining = group.Videos.Count;
                foreach (var video in group.Videos)
                {
                    if (remaining <= 1)
                    {
                        AddLog($"Une copie conservée pour « {group.Title} ».");
                        break;
                    }

                    StatusText.Text = $"Étape 1/2 : {group.Title}";
                    var decision = await ShowDuplicateDialogAsync(video, remaining);
                    if (decision != ContentDialogResult.Primary)
                    {
                        AddLog($"Vidéo YouTube conservée : {video.Title} ({video.Id}).");
                        continue;
                    }

                    try
                    {
                        var removed = await _duplicateService.DeleteIfStillAvailableAsync(
                            _youtube, video, _maintenanceCts!.Token);
                        if (removed)
                        {
                            remaining--;
                            youtubeDeleted++;
                            AddLog($"Doublon supprimé de YouTube : {video.Title} ({video.Id}).");
                        }
                        else
                        {
                            remaining = 1;
                            youtubeUnavailable++;
                            AddLog($"Suppression YouTube ignorée après revalidation : {video.Title} ({video.Id}).");
                            await ShowInfoDialogAsync("Vidéo conservée",
                                "La vidéo n'a pas été supprimée : elle n'est plus disponible comme doublon exact, ou une seule copie doit être conservée.");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write("Suppression YouTube impossible", ex);
                        AddLog($"Nettoyage YouTube interrompu : {ex.Message}");
                        await ShowInfoDialogAsync("Suppression YouTube impossible",
                            $"{ex.Message}\n\nLa phase de nettoyage local n'a pas été lancée.");
                        return;
                    }
                }
            }

            StatusText.Text = "Nettoyage YouTube terminé. Préparation de l'analyse locale…";
            AddLog($"Première passe terminée : {youtubeDeleted} vidéo(s) supprimée(s) de YouTube, {youtubeUnavailable} revalidation(s) sans suppression.");
            await ShowInfoDialogAsync("Nettoyage YouTube terminé",
                $"Première passe terminée.\nVidéos supprimées de YouTube : {youtubeDeleted}\nVidéos conservées après revalidation : {youtubeUnavailable}\n\nL'analyse des fichiers locaux va maintenant commencer.");

            StatusText.Text = "Étape 2/2 : analyse des fichiers locaux et de YouTube…";
            var channelVideos = await _duplicateService.GetChannelVideosAsync(_youtube, _maintenanceCts!.Token);
            var localMatches = await Task.Run(() => FindLocalMatches(localFolder, channelVideos));

            if (localMatches.Count == 0)
            {
                StatusText.Text = "Nettoyage terminé : aucun fichier local déjà publié n'a été trouvé.";
                AddLog("Deuxième passe terminée : aucun nom de fichier local ne correspond à un titre YouTube.");
                await ShowInfoDialogAsync("Nettoyage terminé",
                    "Aucun fichier local ne correspond exactement au titre d'une vidéo encore présente sur ta chaîne.");
                return;
            }

            AddLog($"{localMatches.Count} fichier(s) local(aux) correspondent à une vidéo de la chaîne.");
            var validateAllLocalFiles = false;
            foreach (var match in localMatches)
            {
                StatusText.Text = $"Étape 2/2 : vérification locale — {Path.GetFileName(match.Path)}";
                if (!validateAllLocalFiles)
                {
                    var decision = await ShowLocalFileDialogAsync(match);
                    if (decision == ContentDialogResult.Secondary)
                    {
                        validateAllLocalFiles = true;
                        AddLog("Validation globale confirmée : tous les fichiers locaux restants seront vérifiés puis supprimés s'ils correspondent toujours.");
                    }
                    else if (decision != ContentDialogResult.Primary)
                    {
                        localKept++;
                        AddLog($"Fichier local conservé : {match.Path}");
                        continue;
                    }
                }

                if (!IsSameLocalFile(match))
                {
                    localKept++;
                    AddLog($"Suppression locale annulée : fichier absent ou modifié depuis l'analyse ({match.Path}).");
                    await ShowInfoDialogAsync("Fichier modifié",
                        "Le fichier a changé ou n'existe plus depuis l'analyse. Il n'a pas été supprimé.");
                    continue;
                }

                // Revalide le titre sur la chaîne juste avant de toucher au fichier local.
                var stillPublished = await _duplicateService.IsVideoStillAvailableAsync(
                    _youtube, match.YouTubeVideo, _maintenanceCts!.Token);
                if (!stillPublished)
                {
                    localKept++;
                    AddLog($"Fichier conservé : aucun titre YouTube identique n'a été retrouvé ({match.Path}).");
                    await ShowInfoDialogAsync("Vidéo YouTube introuvable",
                        "La vidéo correspondante n'est plus présente sur ta chaîne. Le fichier local a été conservé.");
                    continue;
                }

                // Deuxième contrôle local après l'appel réseau, puis suppression permanente
                // par File.Delete : aucune mise à la corbeille.
                if (!IsSameLocalFile(match))
                {
                    localKept++;
                    AddLog($"Suppression locale annulée : le fichier a changé pendant la vérification YouTube ({match.Path}).");
                    await ShowInfoDialogAsync("Fichier modifié",
                        "Le fichier a changé pendant la vérification YouTube. Il n'a pas été supprimé.");
                    continue;
                }

                try
                {
                    File.Delete(match.Path);
                    localDeleted++;
                    AddLog($"Fichier supprimé définitivement du PC : {match.Path}");
                }
                catch (Exception ex)
                {
                    localKept++;
                    AppLogger.Write("Suppression définitive du fichier local impossible", ex);
                    AddLog($"Suppression locale impossible : {match.Path} — {ex.Message}");
                    await ShowInfoDialogAsync("Suppression locale impossible", ex.Message);
                }
            }

            StatusText.Text = $"Nettoyage terminé : {youtubeDeleted} suppression(s) YouTube, {localDeleted} fichier(s) supprimé(s) du PC.";
            AddLog($"Nettoyage complet : YouTube {youtubeDeleted} supprimée(s), PC {localDeleted} supprimé(s), {localKept} fichier(s) conservé(s).");
            await ShowInfoDialogAsync("Nettoyage terminé",
                $"YouTube : {youtubeDeleted} doublon(s) supprimé(s).\nPC : {localDeleted} fichier(s) supprimé(s) définitivement.\nFichiers locaux conservés : {localKept}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Nettoyage arrêté.";
            AddLog("Nettoyage annulé à la demande de l'utilisateur.");
        }
        catch (Exception ex)
        {
            ShowError("Nettoyage impossible", ex);
            StatusText.Text = "Le nettoyage a été interrompu.";
        }
        finally
        {
            _maintenanceCts?.Dispose();
            _maintenanceCts = null;
            _maintenanceRunning = false;
            _maintenanceStopped?.TrySetResult();
            _maintenanceStopped = null;
            StopButton.Visibility = Visibility.Collapsed;
            SyncButton.Visibility = Visibility.Visible;
            CleanButton.Visibility = Visibility.Visible;
            LogsToggleButton.IsEnabled = true;
        }
    }

    private static List<LocalVideoMatch> FindLocalMatches(
        string folder, IReadOnlyList<DuplicateVideo> channelVideos)
    {
        var videosByTitle = channelVideos
            .GroupBy(video => video.Title, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var matches = new List<LocalVideoMatch>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(path)))
                continue;

            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
                continue;

            var title = Path.GetFileNameWithoutExtension(info.Name);
            // Ne tronque pas côté nettoyage local : une correspondance doit être
            // strictement exacte. Un nom de plus de 100 caractères ne peut pas
            // correspondre au titre YouTube tronqué par la limite de la plateforme.
            if (title.Length > 100)
                continue;

            if (videosByTitle.TryGetValue(title, out var youtubeVideo))
                matches.Add(new LocalVideoMatch(info.FullName, title, info.Length, info.LastWriteTimeUtc, youtubeVideo));
        }

        return matches;
    }

    private static bool IsSameLocalFile(LocalVideoMatch match)
    {
        var info = new FileInfo(match.Path);
        return info.Exists && info.Length == match.Length && info.LastWriteTimeUtc == match.LastWriteTimeUtc;
    }

    private async Task<ContentDialogResult> ShowDuplicateDialogAsync(
        DuplicateVideo video, int remainingCopies)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 460 };
        if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl) &&
            Uri.TryCreate(video.ThumbnailUrl, UriKind.Absolute, out var thumbnailUri))
        {
            content.Children.Add(new Image
            {
                Source = new BitmapImage(thumbnailUri),
                Height = 220,
                Stretch = Stretch.UniformToFill
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = video.Title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock { Text = $"Durée : {video.Duration}" });
        content.Children.Add(new TextBlock
        {
            Text = $"Copies encore présentes avec ce titre : {remainingCopies}. Une copie au minimum sera conservée.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Attention : la suppression est définitive sur YouTube.",
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "Doublon détecté sur YouTube",
            Content = content,
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Passer",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        return await dialog.ShowAsync();
    }

    private async Task<ContentDialogResult> ShowLocalFileDialogAsync(LocalVideoMatch match)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 460 };
        if (!string.IsNullOrWhiteSpace(match.YouTubeVideo.ThumbnailUrl) &&
            Uri.TryCreate(match.YouTubeVideo.ThumbnailUrl, UriKind.Absolute, out var thumbnailUri))
        {
            content.Children.Add(new Image
            {
                Source = new BitmapImage(thumbnailUri),
                Height = 220,
                Stretch = Stretch.UniformToFill
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = match.Title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = match.Path,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Une vidéo de ta chaîne possède exactement ce titre.\nTaille locale : {match.Length / 1024d / 1024d:F1} Mo.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "La suppression sera définitive : le fichier ne passera pas par la corbeille.",
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(match.YouTubeVideo.Id))
        {
            content.Children.Add(new HyperlinkButton
            {
                Content = LocalizationService.Translate("Vérifier la vidéo sur YouTube"),
                NavigateUri = new Uri($"https://www.youtube.com/watch?v={match.YouTubeVideo.Id}"),
                HorizontalAlignment = HorizontalAlignment.Left
            });
        }

        var dialog = new ContentDialog
        {
            Title = "Copie locale déjà publiée",
            Content = content,
            PrimaryButtonText = "Supprimer définitivement",
            SecondaryButtonText = LocalizationService.Translate("Tout valider"),
            CloseButtonText = LocalizationService.Translate("Annuler"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        return await dialog.ShowAsync();
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task StartSyncAsync(bool automatic = false)
    {
        if (_syncCts is not null || _maintenanceRunning)
        {
            if (automatic) AddLog("Synchronisation automatique ignorée : une opération est déjà en cours.");
            return;
        }
        if (_youtube is null)
        {
            if (automatic)
            {
                AddLog("Synchronisation automatique ignorée : le compte YouTube n'est pas connecté.");
                return;
            }
            StatusText.Text = "Connecte d'abord ton compte YouTube.";
            RestoreFromTray();
            return;
        }

        ReadControlsIntoSettings();
        await SaveSettingsAsync();
        _syncCts = new CancellationTokenSource();
        _syncStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Grid.SetColumn(StopButton, 3);
        SyncButton.Visibility = Visibility.Collapsed;
        CleanButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        LogsToggleButton.IsEnabled = false;
        AddLog(automatic ? "Synchronisation automatique démarrée." : "Synchronisation manuelle démarrée.");

        var progress = new Progress<SyncProgress>(p =>
        {
            StatusText.Text = p.Message;
            if (!string.IsNullOrWhiteSpace(p.CurrentFile))
                AddLog($"{p.CurrentFile} — {p.Message}");
        });

        try
        {
            await _uploader.SyncAsync(_youtube, _settings, progress, _syncCts.Token);
            AddLog("Synchronisation terminée.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Synchronisation arrêtée.";
            AddLog("Synchronisation annulée.");
        }
        catch (Exception ex)
        {
            ShowError("Erreur pendant la synchronisation", ex);
        }
        finally
        {
            _syncCts.Dispose();
            _syncCts = null;
            _syncStopped?.TrySetResult();
            _syncStopped = null;
            StopButton.Visibility = Visibility.Collapsed;
            SyncButton.Visibility = Visibility.Visible;
            CleanButton.Visibility = Visibility.Visible;
            LogsToggleButton.IsEnabled = true;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _syncCts?.Cancel();
        _maintenanceCts?.Cancel();
    }

    private void ReadControlsIntoSettings()
    {
        _settings.FolderPath = FolderTextBox.Text;
        _settings.TargetMode = TargetModeCombo.SelectedIndex == 1 ? "playlist" : "channel";
        _settings.Privacy = (PrivacyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "unlisted";
        if (PlaylistCombo.SelectedItem is PlaylistChoice choice)
        {
            _settings.SelectedPlaylistId = choice.Id;
            _settings.SelectedPlaylistTitle = choice.Title;
        }
    }

    private async Task SaveSettingsAsync()
    {
        ReadControlsIntoSettings();
        await _settingsStore.SaveAsync(_settings);
    }

    private void AddLog(string message)
    {
        AppLogger.Write(message);
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _log.Add(line);
        while (_log.Count > 150) _log.RemoveAt(0);
        if (LogsPanel.Visibility == Visibility.Visible)
            LogList.ScrollIntoView(line);
    }

    private async void ShowError(string title, Exception ex)
    {
        AppLogger.Write(title, ex);
        AddLog($"{title} : {ex.Message}");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = ex.Message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}

internal static class NativeMethods
{
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_RESTORE = 9;
    internal const uint GW_OWNER = 4;
    internal const uint WM_APP = 0x8000;
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_SHOW_EXISTING_INSTANCE = WM_APP + 43;
    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    internal const uint HTCAPTION = 2;
    internal const ulong SC_MAXIMIZE = 0xF030UL;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_CONTEXTMENU = 0x007B;
    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint ID_TRAY_OPEN = 1001;
    internal const uint ID_TRAY_SYNC = 1002;
    internal const uint ID_TRAY_QUIT = 1003;
    internal const uint ID_TRAY_STARTUP = 1004;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        internal uint cbSize;
        internal IntPtr hWnd;
        internal uint uID;
        internal uint uFlags;
        internal uint uCallbackMessage;
        internal IntPtr hIcon;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string szTip;
        internal uint dwState;
        internal uint dwStateMask;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string szInfo;
        internal uint uTimeoutOrVersion;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string szInfoTitle;
        internal uint dwInfoFlags;
        internal Guid guidItem;
        internal IntPtr hBalloonIcon;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    internal delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool AllowSetForegroundWindow(uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    internal static NOTIFYICONDATA CreateTrayData(IntPtr hwnd, uint callbackMessage, string tooltip, IntPtr iconHandle)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = callbackMessage,
            hIcon = iconHandle != IntPtr.Zero ? iconHandle : LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            szTip = tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInstance, string name, uint type, int cx, int cy, uint flags);

    internal static IntPtr LoadIconFromFile(string path) => LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [System.Runtime.InteropServices.DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    internal static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [System.Runtime.InteropServices.DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    internal static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [System.Runtime.InteropServices.DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
