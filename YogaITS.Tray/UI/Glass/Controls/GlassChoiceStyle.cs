using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace YogaITS.Tray.UI.Glass.Controls;

internal static class GlassChoiceStyle
{
    internal static Style Create() => (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
                <Setter Property="HorizontalContentAlignment" Value="Center" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
                <Setter Property="UseSystemFocusVisuals" Value="True" />
                <Setter Property="Template"><Setter.Value>
                    <ControlTemplate TargetType="RadioButton">
                        <Grid Background="{TemplateBinding Background}" MinHeight="44">
                            <ContentPresenter Content="{TemplateBinding Content}"
                                HorizontalAlignment="Center" VerticalAlignment="Center"
                                Foreground="{TemplateBinding Foreground}" FontWeight="{TemplateBinding FontWeight}" FontSize="16" />
                        </Grid>
                    </ControlTemplate>
                </Setter.Value></Setter>
            </Style>
            """);
}
