using System.Windows;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class HelpWindow : Window
{
    public HelpWindow(HelpItem item, DataService? data = null)
    {
        InitializeComponent();
        Title = $"Safety Gate Support - {item.Title}";
        SectionText.Text = item.Section;
        TitleText.Text = item.Title;
        if (data is not null && item.PhotoPath.Length > 0)
        {
            var cancel = new CancellationTokenSource();
            Closed += (_, _) => cancel.Cancel();
            Loaded += async (_, _) =>
            {
                try
                {
                    var photo = await IdentificationPhotoLoader.LoadAsync(data.Resolve(item.PhotoPath), 1200, cancel.Token);
                    if (cancel.IsCancellationRequested) return;
                    ProblemPhoto.Source = photo.Source;
                    ProblemPhoto.Visibility = photo.Source is null ? Visibility.Collapsed : Visibility.Visible;
                    ProblemPhotoStatus.Text = photo.Source is null ? "Photo unavailable. Fix instructions are below." : "";
                }
                catch (OperationCanceledException) { }
                catch { if (!cancel.IsCancellationRequested) ProblemPhotoStatus.Text = "Photo unavailable. Fix instructions are below."; }
            };
        }
        BodyText.Text = string.IsNullOrWhiteSpace(item.Body) ? "No help text has been entered for this topic." : item.Body;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
