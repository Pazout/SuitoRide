using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SuitoRide;

public sealed class ActionButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        nameof(Icon), typeof(Geometry), typeof(ActionButton));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public ActionButton() { }

    internal ActionButton(Geometry icon, string label, bool prompt = false)
    {
        Icon = icon;
        Content = UiText.ActionLabel(label, prompt);
    }
}

public static class ButtonIcons
{
    public static Geometry History { get; } = Draw("M2,2V7H7 M2,7 A6,6 0 1 1 3,12 M8,4V8L11,10");
    public static Geometry Settings { get; } = Draw("M2,4H5 M8,4H14 M6.5,2V6 M2,8H9 M12,8H14 M10.5,6V10 M2,12H5 M8,12H14 M6.5,10V14");
    public static Geometry Group { get; } = Draw("M9,4 A2.5,2.5 0 1 1 4,4 A2.5,2.5 0 1 1 9,4 M1,14V12 A3,3 0 0 1 4,9H9 A3,3 0 0 1 12,12V14Z M12,2 A2.5,2.5 0 0 1 12,7 M13,9 A3,3 0 0 1 15,12V14");
    public static Geometry Update { get; } = Draw("M8,1V10 M5,7L8,10 11,7 M2,10V14H14V10");
    public static Geometry Save { get; } = Draw("M2,2H11L14,5V14H2Z M5,2V6H10V2 M5,14V9H11V14");
    public static Geometry Folder { get; } = Draw("M1,13V3H6L8,5H15V13Z");
    public static Geometry OpenFolder { get; } = Draw("M1,13V3H6L8,5H14V7 M1,13L4,7H15L12,13Z");
    public static Geometry Dropbox { get; } = Draw("M8,4L4.5,1.5 1,4 4.5,6.5Z M8,4L11.5,1.5 15,4 11.5,6.5Z M8,9L4.5,6.5 1,9 4.5,11.5Z M8,9L11.5,6.5 15,9 11.5,11.5Z M5,13L8,15 11,13");
    public static Geometry Refresh { get; } = Draw("M14,2V6H10 M14,6 A6,6 0 1 0 14,10");
    public static Geometry Route { get; } = Draw("M4,5H10 A3,3 0 0 1 10,11H6 M4,5 A1.5,1.5 0 1 1 1,5 A1.5,1.5 0 1 1 4,5 M6,11 A1.5,1.5 0 1 1 3,11 A1.5,1.5 0 1 1 6,11");
    public static Geometry Edit { get; } = Draw("M2,14L3,10 11,2 14,5 6,13Z M9,4L12,7");
    public static Geometry Bike { get; } = Draw("M6,11 A2.5,2.5 0 1 1 1,11 A2.5,2.5 0 1 1 6,11 M15,11 A2.5,2.5 0 1 1 10,11 A2.5,2.5 0 1 1 15,11 M3.5,11L7,5 10,11H3.5 M12.5,11L10.5,4H13 M5,5H8");
    public static Geometry Heart { get; } = Draw("M8,14C6,12 1,8 1,5 A3.5,3.5 0 0 1 8,4 A3.5,3.5 0 0 1 15,5 C15,8 10,12 8,14Z");
    public static Geometry Fit { get; } = Draw("M1,5V1H5 M11,1H15V5 M15,11V15H11 M5,15H1V11 M4,10L7,7 9,9 12,5");
    public static Geometry Follow { get; } = Draw("M14,8 A6,6 0 1 1 2,8 A6,6 0 1 1 14,8 M8,0V4 M8,12V16 M0,8H4 M12,8H16 M8,7V9");
    public static Geometry Navigate { get; } = Draw("M1,7L15,1 9,15 7,9Z");
    public static Geometry Cube { get; } = Draw("M8,1L14,4V11L8,15 2,11V4Z M2,4L8,8 14,4 M8,8V15");
    public static Geometry Panorama { get; } = Draw("M1,4H15V12H1Z M2,11L5,8 8,11 11,7 14,10 M6,6H7");
    public static Geometry Play { get; } = Draw("M4,2L14,8 4,14Z");
    public static Geometry Demo { get; } = Draw("M1,3H15V13H1Z M6,5.5L11,8 6,10.5Z");
    public static Geometry Stop { get; } = Draw("M3,3H13V13H3Z");
    public static Geometry Battery { get; } = Draw("M1,5H13V11H1Z M15,7V9 M4,8H10");
    public static Geometry Close { get; } = Draw("M3,3L13,13 M13,3L3,13");
    public static Geometry Search { get; } = Draw("M11,6 A5,5 0 1 1 1,6 A5,5 0 1 1 11,6 M10,10L15,15");
    public static Geometry Connect { get; } = Draw("M5,2V5 M11,2V5 M3,5H13 M4,5V8 A4,4 0 0 0 12,8V5 M8,12V15");
    public static Geometry Disconnect { get; } = Draw("M5,2V5 M11,2V5 M3,5H13 M4,5V8 A4,4 0 0 0 12,8V5 M1,15L15,1");
    public static Geometry External { get; } = Draw("M7,3H2V14H13V9 M9,2H14V7 M14,2L7,9");
    public static Geometry Check { get; } = Draw("M2,8L6,12 14,4");
    public static Geometry Trash { get; } = Draw("M2,4H14 M6,4V2H10V4 M4,4L5,14H11L12,4 M7,7V11 M9,7V11");
    public static Geometry Upload { get; } = Draw("M8,11V2 M5,5L8,2 11,5 M2,10V14H14V10");
    public static Geometry Send { get; } = Draw("M1,2L15,8 1,14 4,8Z M4,8H15");
    public static Geometry Add { get; } = Draw("M8,2V14 M2,8H14");
    public static Geometry Server { get; } = Draw("M2,2H14V7H2Z M2,9H14V14H2Z M4,4.5H5 M4,11.5H5");
    public static Geometry Copy { get; } = Draw("M5,5H14V15H5Z M11,5V2H2V12H5");
    public static Geometry Join { get; } = Draw("M8,2H14V14H8 M1,8H10 M7,5L10,8 7,11");

    static Geometry Draw(string path)
    {
        var icon = Geometry.Parse(path);
        icon.Freeze();
        return icon;
    }
}
