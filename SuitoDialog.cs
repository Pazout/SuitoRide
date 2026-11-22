using System.Windows;

namespace SuitoRide;

internal abstract class SuitoDialog : Window
{
    protected SuitoDialog()
    {
        SetResourceReference(BackgroundProperty, "SuitoWindowBackground");
        SetResourceReference(ForegroundProperty, "SuitoWindowForeground");
    }
}
