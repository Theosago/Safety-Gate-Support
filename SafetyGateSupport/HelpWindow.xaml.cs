using System.Windows;
using SafetyGateSupport.Models;

namespace SafetyGateSupport;

public partial class HelpWindow : Window
{
    public HelpWindow(HelpItem item)
    {
        InitializeComponent();
        Title = $"Safety Gate Support - {item.Title}";
        SectionText.Text = item.Section;
        TitleText.Text = item.Title;
        BodyText.Text = string.IsNullOrWhiteSpace(item.Body) ? "No help text has been entered for this topic." : item.Body;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
