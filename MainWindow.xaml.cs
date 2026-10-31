using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MusicFileParser.Models;
using MusicFileParser.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MusicFileParser;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<LogSection> _sections = [];
    private LogSection? _notes;
    private LogSection? _folder;
    private bool _isProcessing;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _sections;
        PlaceWindow();
    }

    private void PlaceWindow()
    {
        const int width = 980;
        const int height = 720;

        DisplayArea? area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null)
        {
            AppWindow.Resize(new SizeInt32(width, height));
            return;
        }

        RectInt32 work = area.WorkArea;
        int windowWidth = Math.Min(width, work.Width);
        int windowHeight = Math.Min(height, work.Height);
        int x = work.X + Math.Max(0, (work.Width - windowWidth) / 2);
        int y = work.Y + Math.Max(0, (work.Height - windowHeight) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, windowWidth, windowHeight));
    }

    private async void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing)
            return;

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary
        };
        picker.FileTypeFilter.Add("*");

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        FolderPathBox.Text = folder.Path;
        ProcessButton.IsEnabled = true;
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || string.IsNullOrWhiteSpace(FolderPathBox.Text))
            return;

        string folder = FolderPathBox.Text;
        _isProcessing = true;
        SelectFolderButton.IsEnabled = false;
        ProcessButton.IsEnabled = false;
        _sections.Clear();
        _notes = null;
        _folder = null;

        var progress = new Progress<LogEvent>(AppendLog);

        try
        {
            await Task.Run(() => new CoverProcessor().Process(folder, progress));
        }
        catch (Exception ex)
        {
            AppendLog(new LogEvent(LogEventKind.Summary, $"Сбой обработки: {ex.Message}"));
        }
        finally
        {
            _isProcessing = false;
            SelectFolderButton.IsEnabled = true;
            ProcessButton.IsEnabled = true;
        }
    }

    private void AppendLog(LogEvent entry)
    {
        if (DispatcherQueue.HasThreadAccess)
            AddLog(entry);
        else
            DispatcherQueue.TryEnqueue(() => AddLog(entry));
    }

    private void AddLog(LogEvent entry)
    {
        switch (entry.Kind)
        {
            case LogEventKind.Folder:
                _folder = AddSection(entry.Text, LogSectionKind.Folder);
                break;
            case LogEventKind.Summary:
                LogSection summary = AddSection("Итог", LogSectionKind.Summary);
                summary.Lines.Add(entry.Text);
                _folder = null;
                break;
            case LogEventKind.Info:
                _notes ??= AddSection("Обход", LogSectionKind.Notes);
                _notes.Lines.Add(entry.Text);
                Reveal();
                return;
            default:
                LogSection target = _folder ?? (_notes ??= AddSection("Обход", LogSectionKind.Notes));
                target.Lines.Add(entry.Text);
                Reveal();
                return;
        }

        Reveal();
    }

    private LogSection AddSection(string title, LogSectionKind kind)
    {
        var section = new LogSection(title, kind);
        _sections.Add(section);
        return section;
    }

    private void Reveal()
    {
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }
}
