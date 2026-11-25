using System.Windows;
using System.Windows.Controls;

namespace SafetyGateSupport;

/// <summary>A small modal that offers a few clearly described options instead of Yes/No buttons.</summary>
public sealed class ChoiceDialog : Window
{
    private int _choice = -1;

    private ChoiceDialog(string title, string question, IReadOnlyList<(string Label, string Detail)> options)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = title; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        var heading = new TextBlock { Text = question, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(heading);
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = options[i].Label, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
            var detail = new TextBlock { Text = options[i].Detail, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            content.Children.Add(detail);
            var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10) };
            button.SetResourceReference(StyleProperty, "CardButton");
            button.Click += (_, _) => { _choice = index; DialogResult = true; };
            panel.Children.Add(button);
        }
        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "QuietButton");
        panel.Children.Add(cancel);
        Content = panel;
    }

    /// <summary>Returns the index of the chosen option, or -1 when cancelled.</summary>
    public static int Ask(Window owner, string title, string question, params (string Label, string Detail)[] options)
    {
        var dialog = new ChoiceDialog(title, question, options) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._choice : -1;
    }
}
