using System.Windows;

namespace SuitoRide;

internal abstract class SuitoDialog : Window
{
    protected SuitoDialog()
    {
        Language=System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
        SetResourceReference(BackgroundProperty, "SuitoWindowBackground");
        SetResourceReference(ForegroundProperty, "SuitoWindowForeground");
    }
}
