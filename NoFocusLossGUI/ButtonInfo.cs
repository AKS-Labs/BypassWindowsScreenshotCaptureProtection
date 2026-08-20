using System.Windows;

namespace NoFocusLossGUI
{
    /// <summary>
    /// Attached property that supplies the explanatory tooltip text shown when
    /// hovering the info ("i") badge inside an inject button.
    /// </summary>
    public static class ButtonInfo
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text", typeof(string), typeof(ButtonInfo), new PropertyMetadata(string.Empty));

        public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);

        public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);
    }
}