using System.Globalization;
using System.Text.Json;
using System.Windows.Markup;

namespace SuitoRide;

internal static class UiText
{
    static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
        { "cs", "de", "en", "es", "fr", "ja", "pl", "sk" };
    public static string Language { get; } = ResolveLanguage(CultureInfo.CurrentCulture);

    internal static string ResolveLanguage(CultureInfo culture) =>
        Languages.Contains(culture.TwoLetterISOLanguageName) ? culture.TwoLetterISOLanguageName : "en";

    static readonly Dictionary<string,string> Texts = Load(Language);

    static Dictionary<string,string> Load(string language)
    {
        if(language=="cs")return new(StringComparer.Ordinal);
        using var stream=typeof(UiText).Assembly.GetManifestResourceStream($"SuitoRide.Localization.{language}.json");
        return stream==null ? new(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string,string>>(stream) ?? new(StringComparer.Ordinal);
    }

    public static string Get(string source) => Texts.GetValueOrDefault(source,source);

    public static string Format(FormattableString value) =>
        string.Format(CultureInfo.CurrentCulture,Get(value.Format),value.GetArguments());
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class UiExtension(string source) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(source);
}
