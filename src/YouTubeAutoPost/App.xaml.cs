using Microsoft.UI.Xaml;
using System.Diagnostics;
using YouTubeAutoPost.Services;

namespace YouTubeAutoPost;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _instanceMutex;

    public App()
    {
        // Mutex nommé : une seule instance de YouTube AutoPost par session Windows.
        _instanceMutex = new Mutex(initiallyOwned: true, name: @"Local\YouTubeAutoPost_SingleInstance", createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Une seconde ouverture ne crée aucune fenêtre et ne montre aucun message :
            // elle demande à l'instance existante de se restaurer et de prendre le focus.
            ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }

        AppLogger.Write("=== Démarrage de YouTube AutoPost ===");
        AppLogger.Write($"Version Windows : {Environment.OSVersion}; .NET : {Environment.Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Write($"Exception non interceptée (IsTerminating={e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Write("Exception de tâche non observée", e.Exception);
            e.SetObserved();
        };
        UnhandledException += (_, e) =>
        {
            AppLogger.Write("Exception WinUI non interceptée", e.Exception);
        };

        try
        {
            InitializeComponent();
            AppLogger.Write("Initialisation XAML de l'application terminée.");
        }
        catch (Exception ex)
        {
            AppLogger.Write("Échec pendant InitializeComponent de l'application.", ex);
            throw;
        }
    }

    private static void ActivateExistingInstance()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var processName = currentProcess.ProcessName;
        var currentSessionId = currentProcess.SessionId;

        // Le mutex peut être acquis avant que la première fenêtre soit créée.
        // On attend donc brièvement que son HWND apparaisse, sans ouvrir de dialogue.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != currentSessionId)
                        continue;

                    var targetProcessId = (uint)process.Id;
                    var existingWindow = IntPtr.Zero;

                    NativeMethods.EnumWindows((hwnd, _) =>
                    {
                        NativeMethods.GetWindowThreadProcessId(hwnd, out var windowProcessId);
                        if (windowProcessId == targetProcessId &&
                            NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == IntPtr.Zero)
                        {
                            existingWindow = hwnd;
                            return false;
                        }

                        return true;
                    }, IntPtr.Zero);

                    if (existingWindow != IntPtr.Zero)
                    {
                        // Autorise l'instance existante à passer au premier plan,
                        // puis lui transmet un message privé traité par sa fenêtre.
                        NativeMethods.AllowSetForegroundWindow(targetProcessId);
                        if (NativeMethods.PostMessage(
                                existingWindow,
                                NativeMethods.WM_SHOW_EXISTING_INSTANCE,
                                IntPtr.Zero,
                                IntPtr.Zero))
                            return;
                    }
                }
            }

            Thread.Sleep(100);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLogger.Write("OnLaunched : création de la fenêtre principale.");
        try
        {
            _window = new MainWindow(string.Equals(args.Arguments?.Trim(), "--startup", StringComparison.OrdinalIgnoreCase));
            _window.Activate();
            AppLogger.Write("Fenêtre principale activée.");
        }
        catch (Exception ex)
        {
            AppLogger.Write("Échec pendant la création ou l'activation de la fenêtre principale.", ex);
            throw;
        }
    }
}
