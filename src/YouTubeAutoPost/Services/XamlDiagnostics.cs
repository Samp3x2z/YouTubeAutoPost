using System.Xml;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Markup;

namespace YouTubeAutoPost.Services;

internal static class XamlDiagnostics
{
    // Diagnostic temporaire : tester l'arbre XAML complet, puis chaque contrôle
    // et chaque conteneur isolément afin de faire remonter une erreur plus précise.
    private static readonly HashSet<string> TestableControls = new(StringComparer.Ordinal)
    {
        "Grid", "StackPanel", "TextBlock", "Button", "TextBox", "ComboBox",
        "ComboBoxItem", "CheckBox", "Border", "ScrollViewer", "ListView"
    };

    private static readonly HashSet<string> EventAttributes = new(StringComparer.Ordinal)
    {
        "Click", "SelectionChanged", "TextChanged", "Checked", "Unchecked", "Loaded"
    };

    public static void Diagnose(string path)
    {
        try
        {
            AppLogger.Write($"Diagnostic XAML isolé : lecture de {path}");
            if (!File.Exists(path))
            {
                // En publication, le compilateur WinUI peut ne pas copier les fichiers Page
                // dans le dossier de sortie. En développement, retrouver alors le XAML source
                // dans le dépôt situé au-dessus de artifacts\\YouTubeAutoPost.
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null)
                {
                    var sourceCandidate = Path.Combine(directory.FullName, "src", "YouTubeAutoPost", "MainWindow.xaml");
                    if (File.Exists(sourceCandidate))
                    {
                        AppLogger.Write($"XAML de diagnostic absent du dossier publié ; source retrouvée : {sourceCandidate}");
                        path = sourceCandidate;
                        break;
                    }
                    directory = directory.Parent;
                }
            }

            if (!File.Exists(path))
            {
                AppLogger.Write($"Diagnostic XAML impossible : fichier absent du dossier publié et du dépôt source ({path}).");
                return;
            }

            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            var sourceRoot = document.Root;
            if (sourceRoot is null)
            {
                AppLogger.Write("Diagnostic XAML : document vide.");
                return;
            }

            const string presentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            const string xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

            // Window n'est pas un élément visuel chargeable par XamlReader.Load.
            // On teste donc tout son contenu à partir du premier enfant visuel.
            var visualRoot = sourceRoot.Elements().FirstOrDefault();
            if (visualRoot is not null)
            {
                var visualTree = new XElement(visualRoot);
                visualTree.SetAttributeValue("xmlns", presentationNamespace);
                visualTree.SetAttributeValue(XNamespace.Xmlns + "x", xamlNamespace);
                RemoveRuntimeOnlyAttributes(visualTree);

                AppLogger.Write("Diagnostic XAML : test de l'arbre visuel complet.");
                try
                {
                    XamlReader.Load(visualTree.ToString(SaveOptions.DisableFormatting));
                    AppLogger.Write("Diagnostic XAML : arbre visuel complet chargé sans erreur.");
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"Diagnostic XAML : échec de l'arbre visuel complet, HRESULT=0x{ex.HResult:X8}.", ex);
                }
            }

            var tested = 0;
            var skipped = 0;

            foreach (var sourceElement in sourceRoot.Descendants())
            {
                var name = sourceElement.Name.LocalName;
                if (!TestableControls.Contains(name))
                {
                    skipped++;
                    continue;
                }

                // Tester aussi les conteneurs, en retirant leurs enfants pour isoler leurs propriétés.
                var testElement = new XElement(sourceElement);
                testElement.RemoveNodes();
                testElement.SetAttributeValue("xmlns", presentationNamespace);
                testElement.SetAttributeValue(XNamespace.Xmlns + "x", xamlNamespace);
                RemoveRuntimeOnlyAttributes(testElement);

                var lineInfo = (IXmlLineInfo)sourceElement;
                var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
                var pathDescription = string.Join("/", sourceElement.AncestorsAndSelf().Reverse().Select(e => e.Name.LocalName));
                tested++;

                AppLogger.Write($"XAML ISOLÉ DÉBUT #{tested} ligne={line} contrôle={pathDescription}");
                try
                {
                    var markup = testElement.ToString(SaveOptions.DisableFormatting);
                    XamlReader.Load(markup);
                    AppLogger.Write($"XAML ISOLÉ OK #{tested} ligne={line} contrôle={name}");
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"XAML ISOLÉ ÉCHEC #{tested} ligne={line} contrôle={name} HRESULT=0x{ex.HResult:X8} message={ex.Message}", ex);
                }
            }

            AppLogger.Write($"Diagnostic XAML isolé terminé : {tested} éléments testés, {skipped} éléments ignorés.");
        }
        catch (Exception ex)
        {
            AppLogger.Write("Le diagnostic XAML isolé lui-même a échoué.", ex);
        }
    }

    private static void RemoveRuntimeOnlyAttributes(XElement element)
    {
        foreach (var node in element.DescendantsAndSelf())
        {
            foreach (var attribute in node.Attributes().ToList())
            {
                if (attribute.Name.Namespace == XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") &&
                    attribute.Name.LocalName is "Name" or "Class")
                {
                    attribute.Remove();
                }
                else if (attribute.Name.Namespace == XNamespace.None &&
                         EventAttributes.Contains(attribute.Name.LocalName))
                {
                    attribute.Remove();
                }
            }
        }
    }
}
