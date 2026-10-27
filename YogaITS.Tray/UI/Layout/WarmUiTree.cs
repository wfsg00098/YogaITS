using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YogaITS.Tray.UI.Layout;

internal static class WarmUiTree
{
    public static void ApplyTemplates(UIElement node)
    {
        foreach (var element in Elements(node))
            if (element is Control control) control.ApplyTemplate();
    }

    // Follow owned content, including collapsed/unloaded groups, not only the live visual tree.
    public static IEnumerable<UIElement> Elements(UIElement node)
    {
        yield return node;
        IEnumerable<UIElement> children=node switch
        {
            Panel panel=>panel.Children,
            Border {Child: UIElement child}=>[child],
            ContentControl {Content: UIElement child}=>[child],
            _=>[]
        };
        foreach (var child in children.ToArray())
            foreach (var element in Elements(child)) yield return element;
    }
}
