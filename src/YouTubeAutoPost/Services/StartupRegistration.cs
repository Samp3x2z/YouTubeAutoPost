using Microsoft.Win32;

namespace YouTubeAutoPost.Services;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YouTubeAutoPost";

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLogger.Write("Démarrage automatique désactivé.");
                return true;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                AppLogger.Write("Impossible de déterminer le chemin actuel de l'exécutable pour le démarrage automatique.");
                return false;
            }

            // Réécrit le chemin courant : après un déplacement, une relance manuelle
            // de l'application répare automatiquement l'entrée de démarrage.
            key.SetValue(ValueName, $"\"{executablePath}\" --startup", RegistryValueKind.String);
            AppLogger.Write($"Démarrage automatique configuré : {executablePath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Write("Impossible de mettre à jour le démarrage automatique Windows.", ex);
            return false;
        }
    }
}
