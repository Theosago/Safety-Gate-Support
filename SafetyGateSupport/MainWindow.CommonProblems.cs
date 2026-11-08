using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class MainWindow
{
    private CancellationTokenSource? _commonProblemPhotos;
    public sealed class CommonProblemCard : INotifyPropertyChanged
    {
        public required HelpItem Topic { get; init; }
        private BitmapSource? _photo;
        public BitmapSource? Photo { get => _photo; set { _photo = value; PropertyChanged?.Invoke(this, new(nameof(Photo))); } }
        private string _message = "Loading photo…";
        public string PhotoMessage { get => _message; set { _message = value; PropertyChanged?.Invoke(this, new(nameof(PhotoMessage))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private void BuildCommonProblems()
    {
        _commonProblemPhotos?.Cancel();
        var cards = _help.Where(h => h.Visible && h.Section.Equals("Common Problems", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.SortOrder).ThenBy(h => h.Title).Select(h => new CommonProblemCard { Topic = h }).ToList();
        CommonProblemsList.ItemsSource = cards;
        CommonProblemsEmpty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var cancel = new CancellationTokenSource(); _commonProblemPhotos = cancel;
        _ = LoadCommonProblemPhotosAsync(cards, cancel);
    }
    private async Task LoadCommonProblemPhotosAsync(List<CommonProblemCard> cards, CancellationTokenSource cancel)
    {
        try
        {
            foreach (var card in cards)
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (card.Topic.Photos.Count == 0) { card.PhotoMessage = "No photo supplied — open for instructions"; continue; }
                try
                {
                    var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(card.Topic.Photos[0]), 640, cancel.Token);
                    if (cancel.IsCancellationRequested || _isClosed) return;
                    card.Photo = result.Source; card.PhotoMessage = "Photo unavailable — open for instructions";
                }
                catch (OperationCanceledException) { throw; }
                catch { card.PhotoMessage = "Photo unavailable — open for instructions"; }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_commonProblemPhotos, cancel)) _commonProblemPhotos = null; cancel.Dispose(); }
    }
    private void CommonProblem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HelpItem item }) OpenCommonProblem(item);
    }
    private bool _commonProblemsExpanded = true;
    private FrameworkElement? _problemOrigin;
    private CancellationTokenSource? _problemDetailPhotos;
    public sealed class ProblemPhoto : INotifyPropertyChanged
    {
        private BitmapSource? _source;
        public BitmapSource? Source { get => _source; set { _source = value; PropertyChanged?.Invoke(this, new(nameof(Source))); } }
        private string _status = "Loading photo…";
        public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private void ToggleCommonProblems_Click(object sender, RoutedEventArgs e)
    {
        SetCommonProblemsExpanded(!_commonProblemsExpanded);
        if (_commonProblemsExpanded) SetPartsRail(false);
    }
    private void SetCommonProblemsExpanded(bool expanded)
    {
        _commonProblemsExpanded = expanded;
        CommonProblemsBody.Visibility = _commonProblemsExpanded ? Visibility.Visible : Visibility.Collapsed;
        CommonProblemsRail.Width = _commonProblemsExpanded ? 300 : 44;
        CommonProblemsToggle.Content = _commonProblemsExpanded ? "Common Problems ▶" : "◀";
    }
    private void OpenCommonProblem(HelpItem item)
    {
        if (!CloseInlineTool()) return;
        _problemOrigin = SystemsView.IsVisible ? SystemsView : TroubleshootView.IsVisible ? TroubleshootView : HomeShell;
        SaveSharedNotesNow(); HideAllViews();
        CommonProblemView.Visibility = Visibility.Visible; CommonProblemTitle.Text = item.Title; CommonProblemInstructions.Text = item.Body;
        CommonProblemScroll.ScrollToTop();
        var photos = item.Photos.Select(_ => new ProblemPhoto()).ToList(); CommonProblemGallery.ItemsSource = photos;
        var cancel = new CancellationTokenSource(); _problemDetailPhotos = cancel;
        _ = LoadProblemDetailAsync(item.Photos, photos, cancel);
    }
    private async Task LoadProblemDetailAsync(IReadOnlyList<string> paths, List<ProblemPhoto> photos, CancellationTokenSource cancel)
    {
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                try
                {
                    var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(paths[i]), 1200, cancel.Token);
                    if (cancel.IsCancellationRequested || _isClosed) return;
                    photos[i].Source = result.Source; photos[i].Status = result.Source is null ? "Photo unavailable. Instructions are below." : "";
                }
                catch (OperationCanceledException) { throw; }
                catch { if (!cancel.IsCancellationRequested) photos[i].Status = "Photo unavailable. Instructions are below."; }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_problemDetailPhotos, cancel)) _problemDetailPhotos = null; cancel.Dispose(); }
    }
    private void CommonProblemBack_Click(object sender, RoutedEventArgs e)
    {
        if (_problemOrigin == SystemsView && SelectedUnit is not null) ShowSystems();
        else if (_problemOrigin == TroubleshootView) ShowTroubleshoot();
        else ShowHome();
    }
}
