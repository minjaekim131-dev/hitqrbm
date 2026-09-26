using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PupilDesktop.Windows;

public partial class MainWindow
{
    private CancellationTokenSource? _includeSuggestCts;
    private CancellationTokenSource? _excludeSuggestCts;
    private bool _syncingStoredTags;

    private sealed record SuggestionOption(string Query, string Label)
    {
        public override string ToString() => Label;
    }

    private async void IncludeTagInput_TextChanged(object sender, TextChangedEventArgs e) =>
        await UpdateSuggestionsAsync(true);

    private async void ExcludeTagInput_TextChanged(object sender, TextChangedEventArgs e) =>
        await UpdateSuggestionsAsync(false);

    private async Task UpdateSuggestionsAsync(bool include)
    {
        var input = include ? IncludeTagInput : ExcludeTagInput;
        var list = include ? IncludeSuggestionList : ExcludeSuggestionList;
        var text = input.Text.Trim();

        var oldCts = include ? _includeSuggestCts : _excludeSuggestCts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        var cts = new CancellationTokenSource();
        if (include) _includeSuggestCts = cts;
        else _excludeSuggestCts = cts;

        if (text.Length < 1)
        {
            list.Visibility = Visibility.Collapsed;
            list.ItemsSource = null;
            return;
        }

        try
        {
            await Task.Delay(220, cts.Token);
            var suggestions = await _client.GetTagSuggestionsAsync(text, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            var items = suggestions
                .Select(q => new SuggestionOption(q, TagQueryToLabel(q)))
                .ToList();
            list.ItemsSource = items;
            list.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch
        {
            list.Visibility = Visibility.Collapsed;
            list.ItemsSource = null;
        }
    }

    private async void IncludeSuggestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IncludeSuggestionList.SelectedItem is not SuggestionOption option) return;
        IncludeSuggestionList.SelectedItem = null;
        await AddFilterTagAsync(true, option.Query);
    }

    private async void ExcludeSuggestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExcludeSuggestionList.SelectedItem is not SuggestionOption option) return;
        ExcludeSuggestionList.SelectedItem = null;
        await AddFilterTagAsync(false, option.Query);
    }

    private async void IncludeTagInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && IncludeSuggestionList.Items.Count > 0)
        {
            IncludeSuggestionList.SelectedIndex = 0;
            IncludeSuggestionList.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var value = IncludeSuggestionList.SelectedItem is SuggestionOption option ? option.Query : IncludeTagInput.Text;
        await AddFilterTagAsync(true, value);
    }

    private async void ExcludeTagInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ExcludeSuggestionList.Items.Count > 0)
        {
            ExcludeSuggestionList.SelectedIndex = 0;
            ExcludeSuggestionList.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var value = ExcludeSuggestionList.SelectedItem is SuggestionOption option ? option.Query : ExcludeTagInput.Text;
        await AddFilterTagAsync(false, value);
    }

    private async Task AddFilterTagAsync(bool include, string raw)
    {
        var value = raw.Trim();
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!value.Contains(':')) value = "tag:" + value;
        value = NormalizeStructuredSearch(value);
        if (value.StartsWith('-')) value = value[1..];
        if (string.IsNullOrWhiteSpace(value) || !value.Contains(':')) return;

        var storage = include ? IncludeTagsBox : ExcludeTagsBox;
        var current = ReadStoredTags(storage.Text);
        if (!current.Contains(value, StringComparer.OrdinalIgnoreCase))
            current.Add(value);

        _syncingStoredTags = true;
        storage.Text = string.Join(",", current);
        _syncingStoredTags = false;

        var input = include ? IncludeTagInput : ExcludeTagInput;
        var list = include ? IncludeSuggestionList : ExcludeSuggestionList;
        input.Text = "";
        list.ItemsSource = null;
        list.Visibility = Visibility.Collapsed;
        RenderFilterTagChips();
        await RefreshCurrentViewAsync();
    }

    private static List<string> ReadStoredTags(string text) => text
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void StoredTags_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingStoredTags) return;
        RenderFilterTagChips();
    }

    private void RenderFilterTagChips()
    {
        RenderFilterTagChipPanel(IncludeTagChips, ReadStoredTags(IncludeTagsBox.Text), true);
        RenderFilterTagChipPanel(ExcludeTagChips, ReadStoredTags(ExcludeTagsBox.Text), false);
    }

    private void RenderFilterTagChipPanel(WrapPanel panel, IEnumerable<string> tags, bool include)
    {
        panel.Children.Clear();
        foreach (var tag in tags)
        {
            var button = new Button
            {
                Content = TagQueryToLabel(tag) + "  ×",
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 5, 4),
                ToolTip = include ? "포함 태그에서 제거" : "제외 태그에서 제거"
            };
            button.Click += async (_, _) => await RemoveFilterTagAsync(include, tag);
            panel.Children.Add(button);
        }
    }

    private async Task RemoveFilterTagAsync(bool include, string tag)
    {
        var storage = include ? IncludeTagsBox : ExcludeTagsBox;
        var current = ReadStoredTags(storage.Text);
        current.RemoveAll(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));

        _syncingStoredTags = true;
        storage.Text = string.Join(",", current);
        _syncingStoredTags = false;
        RenderFilterTagChips();
        await RefreshCurrentViewAsync();
    }

    private static string TagQueryToLabel(string query)
    {
        var idx = query.IndexOf(':');
        if (idx <= 0) return query.Replace('_', ' ');
        var prefix = query[..idx].ToLowerInvariant();
        var name = query[(idx + 1)..].Replace('_', ' ');
        return prefix switch
        {
            "female" => "♀ " + name,
            "male" => "♂ " + name,
            "tag" => name,
            _ => $"{prefix}: {name}"
        };
    }
}
