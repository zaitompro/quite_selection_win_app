using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace QuietSelection.App.Views;

/// <summary>
/// Building blocks in the Windows 11 Settings style: cards, setting rows, badges and info bars.
/// Colors come from the Fluent theme, so light, dark and high-contrast follow Windows.
/// Spacing uses a 4 px grid; type follows the Windows ramp (12/14/20/28).
/// </summary>
internal static class Ui
{
    internal const double Body = 14, Caption = 12, Subtitle = 20, Title = 28;

    internal static FrameworkElement PageTitle(string text) =>
        new TextBlock { Text = text, FontSize = Title, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 20), TextWrapping = TextWrapping.Wrap };

    internal static FrameworkElement Section(string text) =>
        new TextBlock { Text = text, FontSize = Body, FontWeight = FontWeights.SemiBold, Margin = new(0, 20, 0, 8), TextWrapping = TextWrapping.Wrap };

    internal static TextBlock Secondary(string text, double size = Caption)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    internal static Border Card(UIElement content, double bottom = 4)
    {
        var card = new Border { Child = content, CornerRadius = new(8), BorderThickness = new(1), Padding = new(16, 12, 16, 12), Margin = new(0, 0, 0, bottom) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    /// <summary>Title and description on the left, a control on the right; wraps under on narrow windows.</summary>
    internal static Border SettingRow(string glyph, string title, string? description, UIElement? control, double bottom = 4)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = Icon(glyph, 20); icon.Margin = new(0, 0, 16, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description)) text.Children.Add(Secondary(description));
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(element, AutomationProperties.GetName(element) is { Length: > 0 } name ? name : title);
            Grid.SetColumn(element, 2); grid.Children.Add(element);
        }
        return Card(grid, bottom);
    }

    internal static TextBlock Icon(string glyph, double size = 16)
    {
        var block = new GlyphText { Text = glyph, FontSize = size, Width = size + 4, TextAlignment = TextAlignment.Center };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
        return block;
    }

    /// <summary>Decorative icon: no UI Automation peer, so screen readers skip the private-use glyph.</summary>
    private sealed class GlyphText : TextBlock
    {
        protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;
    }

    internal static Button Button(string label, Action click, bool accent = false, string? automationName = null)
    {
        var button = new Button { Content = label, Padding = new(12, 5, 12, 6), MinHeight = 32, MinWidth = 96, Margin = new(0, 0, 8, 0) };
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        if (automationName is not null) AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => click();
        return button;
    }

    internal static CheckBox Toggle(bool value, Action<bool> changed, string automationName)
    {
        var box = new CheckBox { IsChecked = value, MinWidth = 0, Content = value ? "Вкл." : "Выкл." };
        AutomationProperties.SetName(box, automationName);
        box.Checked += (_, _) => { box.Content = "Вкл."; changed(true); };
        box.Unchecked += (_, _) => { box.Content = "Выкл."; changed(false); };
        return box;
    }

    internal enum Tone { Neutral, Success, Caution, Critical, Accent }

    internal static Border Badge(string text, Tone tone)
    {
        var label = new TextBlock { Text = text, FontSize = Caption, Margin = new(8, 1, 8, 2) };
        var badge = new Border { Child = label, CornerRadius = new(10), Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, tone switch
        {
            Tone.Success => "SystemFillColorSuccessBackgroundBrush",
            Tone.Caution => "SystemFillColorCautionBackgroundBrush",
            Tone.Critical => "SystemFillColorCriticalBackgroundBrush",
            _ => "SubtleFillColorSecondaryBrush"
        });
        label.SetResourceReference(TextBlock.ForegroundProperty, tone == Tone.Accent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        return badge;
    }

    /// <summary>An inline message with an icon and optional actions, as in Windows Settings.</summary>
    internal static Border InfoBar(Tone tone, string title, string? message, params UIElement[] actions)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var icon = Icon(tone switch { Tone.Success => "\uE73E", Tone.Critical => "\uEA39", Tone.Caution => "\uE7BA", _ => "\uE946" }, 16);
        icon.Margin = new(0, 2, 12, 0); icon.VerticalAlignment = VerticalAlignment.Top;
        icon.SetResourceReference(TextBlock.ForegroundProperty, tone switch
        {
            Tone.Success => "SystemFillColorSuccessBrush", Tone.Critical => "SystemFillColorCriticalBrush",
            Tone.Caution => "SystemFillColorCautionBrush", _ => "AccentTextFillColorPrimaryBrush"
        });
        grid.Children.Add(icon);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(message)) text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 0) });
        if (actions.Length > 0)
        {
            var row = new WrapPanel { Margin = new(0, 8, 0, 0) };
            foreach (var action in actions) row.Children.Add(action);
            text.Children.Add(row);
        }
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var bar = new Border { Child = grid, CornerRadius = new(8), BorderThickness = new(1), Padding = new(16, 12, 16, 12), Margin = new(0, 0, 0, 8) };
        bar.SetResourceReference(Border.BackgroundProperty, tone switch
        {
            Tone.Success => "SystemFillColorSuccessBackgroundBrush", Tone.Critical => "SystemFillColorCriticalBackgroundBrush",
            Tone.Caution => "SystemFillColorCautionBackgroundBrush", _ => "SystemFillColorAttentionBackgroundBrush"
        });
        bar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        AutomationProperties.SetName(bar, title);
        return bar;
    }

    /// <summary>Navigation entries: an accent pill marks the selected page, hover uses a subtle fill.</summary>
    internal static Style NavigationItemStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
          <Setter Property="Margin" Value="0,1,0,1"/>
          <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Grid>
                  <Border x:Name="Back" CornerRadius="4" Background="Transparent"/>
                  <Border x:Name="Pill" Width="3" Height="16" CornerRadius="1.5" HorizontalAlignment="Left" Visibility="Collapsed"
                          Background="{DynamicResource AccentFillColorDefaultBrush}"/>
                  <Border x:Name="Focus" CornerRadius="4" BorderThickness="2" BorderBrush="Transparent"/>
                  <ContentPresenter Margin="12,9,12,9"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Back" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/>
                  </Trigger>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="Back" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/>
                    <Setter TargetName="Pill" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True">
                    <Setter TargetName="Focus" Property="BorderBrush" Value="{DynamicResource TextFillColorPrimaryBrush}"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
