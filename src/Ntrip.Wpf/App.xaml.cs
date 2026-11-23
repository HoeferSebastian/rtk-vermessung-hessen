using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Ntrip.Wpf;

public partial class App : Application
{
    public App()
    {
        // Bindings sollen die Systemkultur (Dezimalkomma) statt en-US verwenden.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
    }
}
