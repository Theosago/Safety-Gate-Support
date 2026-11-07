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
                if (card.Topic.PhotoPath.Length == 0) { card.PhotoMessage = "No photo supplied — open for instructions"; continue; }
                try
                {
                    var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(card.Topic.PhotoPath), 640, cancel.Token);
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
        if (sender is Button { Tag: HelpItem item }) new HelpWindow(item, _data) { Owner = this }.ShowDialog();
    }
}
