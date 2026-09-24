using Microsoft.Win32;
using PupilDesktop.Core;
using PupilDesktop.Models;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace PupilDesktop.Windows;

public partial class ReaderWindow : Window
{
    private readonly HitomiClient _client;
    private readonly int _galleryId;
    private readonly GalleryInfo _info;
    private List<string> _urls = [];
    private int _page;
    private byte[]? _currentBytes;
    private CancellationTokenSource? _pageCts;

    public ReaderWindow(HitomiClient client, int galleryId, GalleryInfo info)
    {
        InitializeComponent();
        _client = client; _galleryId = galleryId; _info = info;
        Title = $"Pupil Desktop - {info.Title}";
        TitleText.Text = info.Title;
        MetaText.Text = $"#{galleryId} · {info.Type} · {info.Language} · {info.Files.Count} pages";
        Loaded += async (_, _) =>
        {
            _urls = await _client.GetReaderImageUrlsAsync(_info);
            await LoadPageAsync(0);
        };
        Closed += (_, _) => _pageCts?.Cancel();
    }

    private async Task LoadPageAsync(int page)
    {
        if (_urls.Count == 0 || page < 0 || page >= _urls.Count) return;
        _pageCts?.Cancel(); _pageCts = new CancellationTokenSource();
        var ct = _pageCts.Token;
        try
        {
            LoadingText.Visibility = Visibility.Visible; PageImage.Source = null; _currentBytes = null;
            _page = page; PageText.Text = $"{_page + 1} / {_urls.Count}";
            var bytes = await _client.DownloadBytesAsync(_urls[_page], ct);
            ct.ThrowIfCancellationRequested();
            _currentBytes = bytes;
            PageImage.Source = ImageHelpers.ToBitmap(bytes);
            LoadingText.Visibility = Visibility.Collapsed;
            ImageScroll.ScrollToHome();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LoadingText.Text = "불러오기 실패\n" + ex.Message; }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_page - 1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_page + 1);

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.PageUp) await LoadPageAsync(_page - 1);
        else if (e.Key is Key.Right or Key.PageDown or Key.Space) await LoadPageAsync(_page + 1);
        else if (e.Key == Key.Home) await LoadPageAsync(0);
        else if (e.Key == Key.End) await LoadPageAsync(_urls.Count - 1);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        var newWidth = PageImage.Width;
        if (double.IsNaN(newWidth) || newWidth <= 0) newWidth = PageImage.ActualWidth;
        newWidth = Math.Clamp(newWidth * (e.Delta > 0 ? 1.12 : 0.89), 300, 5000);
        PageImage.Width = newWidth;
        PageImage.Stretch = System.Windows.Media.Stretch.Uniform;
    }

    private void SavePage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBytes is null || _page >= _info.Files.Count) return;
        var ext = Path.GetExtension(_info.Files[_page].Name);
        var dialog = new SaveFileDialog { FileName = $"{_galleryId}_{_page + 1:000}{ext}", Filter = "Image|*" + ext + "|All files|*.*" };
        if (dialog.ShowDialog(this) == true) File.WriteAllBytes(dialog.FileName, _currentBytes);
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "저장할 폴더를 선택하세요", Multiselect = false };
        if (dlg.ShowDialog(this) != true) return;
        var folder = Path.Combine(dlg.FolderName, $"{_galleryId} - {ImageHelpers.SafeFileName(_info.Title)}");
        Directory.CreateDirectory(folder);
        try
        {
            LoadingText.Visibility = Visibility.Visible;
            for (var i = 0; i < _urls.Count; i++)
            {
                LoadingText.Text = $"저장 중… {i + 1}/{_urls.Count}";
                var bytes = await _client.DownloadBytesAsync(_urls[i]);
                var ext = Path.GetExtension(_info.Files[i].Name);
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{i + 1:000}{ext}"), bytes);
            }
            LoadingText.Text = "저장 완료";
            await Task.Delay(800);
            LoadingText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
