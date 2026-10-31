using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    private readonly ObservableCollection<string> _log = [];
    private bool _isProcessing;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
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
        _log.Clear();

        var progress = new Progress<string>(AppendLog);

        try
        {
            await Task.Run(() => new CoverProcessor().Process(folder, progress));
        }
        catch (Exception ex)
        {
            AppendLog($"Сбой обработки: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
            SelectFolderButton.IsEnabled = true;
            ProcessButton.IsEnabled = true;
        }
    }

    private void AppendLog(string message)
    {
        if (DispatcherQueue.HasThreadAccess)
            AddLog(message);
        else
            DispatcherQueue.TryEnqueue(() => AddLog(message));
    }

    private void AddLog(string message)
    {
        _log.Add(message);
        LogList.ScrollIntoView(message);
    }
}
