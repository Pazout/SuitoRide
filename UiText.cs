using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Markup;

namespace SuitoRide;

internal static class UiText
{
    internal sealed record LanguageChoice(string Code, string Name);

    public static IReadOnlyList<LanguageChoice> LanguageChoices { get; } =
    [
        new("cs", "Čeština"), new("en", "English"), new("de", "Deutsch"),
        new("fr", "Français"), new("es", "Español"), new("ja", "日本語"),
        new("pl", "Polski"), new("sk", "Slovenčina")
    ];
    static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide", "language.txt");
    public static string Language { get; } = ResolveLanguage(CultureInfo.CurrentCulture, ReadPreference());

    static bool SupportsLanguage(string? code) => LanguageChoices.Any(choice => choice.Code == code);

    internal static string ResolveLanguage(CultureInfo culture, string? preferred = null)
    {
        if (preferred != null && SupportsLanguage(preferred)) return preferred;
        var regional = culture.TwoLetterISOLanguageName;
        return SupportsLanguage(regional) ? regional : "en";
    }

    public static string ReadPreference()
    {
        try
        {
            if (File.Exists(PreferencePath))
            {
                var code = File.ReadAllText(PreferencePath).Trim();
                if (SupportsLanguage(code)) return code;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "";
    }

    public static void SavePreference(string code)
    {
        if (code.Length == 0)
        {
            File.Delete(PreferencePath);
            return;
        }
        if (!SupportsLanguage(code)) throw new ArgumentException(nameof(code));
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
        File.WriteAllText(PreferencePath, code);
    }

    static readonly Dictionary<string,string> Texts = Load(Language);

    static Dictionary<string,string> Load(string language)
    {
        if(language=="cs")return new(StringComparer.Ordinal);
        using var stream=typeof(UiText).Assembly.GetManifestResourceStream($"SuitoRide.Localization.{language}.json");
        return stream==null ? new(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string,string>>(stream) ?? new(StringComparer.Ordinal);
    }

    public static string Get(string source) => Texts.GetValueOrDefault(source,source);

    internal static string ActionLabel(string source, bool prompt) =>
        Get(source) + (prompt ? "..." : "");

    public static string Format(FormattableString value) =>
        string.Format(CultureInfo.CurrentCulture,Get(value.Format),value.GetArguments());
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class UiExtension(string source) : MarkupExtension
{
    public bool Prompt { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.ActionLabel(source, Prompt);
}
