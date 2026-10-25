using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YogaITS.Tray.UI;

internal static class WarmUiTree
{
    public static void ApplyTemplates(UIElement node)
    {
        if(node is Control control) control.ApplyTemplate();
        IEnumerable<UIElement> children=node switch
        {
            Panel panel=>panel.Children,
            Border {Child: UIElement child}=>[child],
            ContentControl {Content: UIElement child}=>[child],
            _=>[]
        };
        foreach(var child in children) ApplyTemplates(child);
    }
}
