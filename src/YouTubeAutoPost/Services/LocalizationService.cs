using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace YouTubeAutoPost.Services;

public static class LocalizationService
{
    private static readonly Dictionary<string, string> Strings = LoadStrings();
    public static string LanguageCode { get; } = SelectLanguage();

    public static string Translate(string source) =>
        Strings.TryGetValue(source, out var translated) ? translated : source;

    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;
        ApplyToElement(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            Apply(VisualTreeHelper.GetChild(root, i));
    }

    private static void ApplyToElement(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock textBlock:
                textBlock.Text = Translate(textBlock.Text);
                break;
            case TextBox textBox:
                textBox.PlaceholderText = Translate(textBox.PlaceholderText);
                break;
            case ComboBox comboBox:
                comboBox.PlaceholderText = Translate(comboBox.PlaceholderText);
                foreach (var item in comboBox.Items)
                {
                    if (item is ComboBoxItem comboBoxItem && comboBoxItem.Content is string itemText)
                        comboBoxItem.Content = Translate(itemText);
                }
                break;
            case ContentControl contentControl when contentControl.Content is string content:
                contentControl.Content = Translate(content);
                break;
        }
    }

    private static string SelectLanguage()
    {
        var systemLanguage = CultureInfo.CurrentUICulture.Name;
        return systemLanguage.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr-FR" : "en-US";
    }

    private static Dictionary<string, string> LoadStrings()
    {
        var language = SelectLanguage();
        var path = Path.Combine(AppContext.BaseDirectory, "lang", language, "strings.json");
        if (!File.Exists(path))
        {
            language = "en-US";
            path = Path.Combine(AppContext.BaseDirectory, "lang", language, "strings.json");
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (Exception ex)
        {
            AppLogger.Write($"Impossible de charger la langue {language}; textes source conservés.", ex);
            return new();
        }
    }
}
