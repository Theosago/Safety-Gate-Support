using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class SharedNotesWindow : Window
{
    private readonly DataService _data;
    private readonly SafetySystem _system;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _loading;
    private bool _dirty;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public SharedNotesWindow(DataService data, SafetySystem system)
    {
        InitializeComponent();
        _data = data;
        _system = system;
        SystemText.Text = $"{system.GateDisplay} - {system.UnitDisplay}   •   {system.Category}";
        _saveTimer.Tick += SaveTimer_Tick;
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        Closing += (sender, args) => SaveNow();
        LoadNotes();
    }

    private void LoadNotes()
    {
        _loading = true;
        try
        {
            NotesBox.Text = _data.LoadSystemNotes(_system);
            _lastWriteUtc = _data.GetSystemNotesLastWriteUtc(_system);
            StatusText.Text = "Saved to shared Q: data";
            _dirty = false;
        }
        catch (Exception ex) { StatusText.Text = "Unable to load notes: " + ex.Message; }
        finally { _loading = false; }
    }

    private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        StatusText.Text = "Saving…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveNow();
    }

    private void SaveNow()
    {
        if (!_dirty || _loading) return;
        try
        {
            _data.SaveSystemNotes(_system, NotesBox.Text);
            _lastWriteUtc = _data.GetSystemNotesLastWriteUtc(_system);
            _dirty = false;
            StatusText.Text = "Saved to shared Q: data";
        }
        catch (Exception ex) { StatusText.Text = "Could not save: " + ex.Message; }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_dirty || _loading || NotesBox.IsKeyboardFocusWithin) return;
        try
        {
            var latest = _data.GetSystemNotesLastWriteUtc(_system);
            if (latest <= _lastWriteUtc) return;
            _loading = true;
            NotesBox.Text = _data.LoadSystemNotes(_system);
            _lastWriteUtc = latest;
            StatusText.Text = "Updated from another user";
        }
        catch { }
        finally { _loading = false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
