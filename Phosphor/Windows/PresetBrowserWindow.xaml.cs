using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace Phosphor;

/// <summary>
/// Preset browser for ProjectM visualizations. Lets the user preview presets
/// and move them between the active presets folder and a Deactivated mirror folder.
/// </summary>
public partial class PresetBrowserWindow : Window
{
    private readonly string _presetPath;
    private readonly string _deactivatedPath;
    private readonly string _favoritesPath;
    private readonly string _deletedPath;
    private readonly PlayfieldProxy? _playfieldProxy;
    private string? _selectedActiveFolder;
    private string? _selectedDeactivatedFolder;
    private string? _selectedFavoritesFolder;

    public PresetBrowserWindow(string presetPath, PlayfieldProxy? playfieldProxy)
    {
        InitializeComponent();
        _presetPath = presetPath;
        _deactivatedPath = Path.Combine(presetPath, "Deactivated");
        _favoritesPath = Path.Combine(presetPath, "Favorites");
        _deletedPath = Path.Combine(presetPath, "Deleted");
        _playfieldProxy = playfieldProxy;

        // Lock the current preset so preview stays visible
        _playfieldProxy?.WithProjectMRenderer(r => r?.LockPreset(true));

        PopulateActiveTree();
        PopulateDeactivatedTree();
        PopulateFavoritesTree();
        PopulateHistory();
    }

    // ── Window chrome ──────────────────────────────────────────────

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ── History ────────────────────────────────────────────────────

    private List<HistoryItem> _allHistoryItems = new();

    private void PopulateHistory()
    {
        var entries = ProjectMPresetLog.GetEntries();
        _allHistoryItems = new List<HistoryItem>();

        // Build a lookup of the most recent monitor entry per preset path
        var monitorEntries = ProjectMPresetMonitorLog.GetEntries();
        var monitorLookup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var me in monitorEntries)
        {
            monitorLookup[me.PresetPath] = me.TopAvgLuminance;
        }

        // Most recent first
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            var isFlagged = monitorLookup.TryGetValue(entry.PresetPath, out var luminance);
            var display = isFlagged
                ? $"* {entry.Timestamp:MM/dd HH:mm:ss}  {entry.PresetPath} ({luminance:F2})"
                : $"{entry.Timestamp:MM/dd HH:mm:ss}  {entry.PresetPath}";
            _allHistoryItems.Add(new HistoryItem { Display = display, PresetRelativePath = entry.PresetPath, IsFlagged = isFlagged });
        }

        // Show filter checkbox only if there are flagged items
        CbFilterFlagged.Visibility = _allHistoryItems.Any(h => h.IsFlagged)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var filtered = CbFilterFlagged.IsChecked == true
            ? _allHistoryItems.Where(h => h.IsFlagged).ToList()
            : _allHistoryItems;
        CbHistory.ItemsSource = filtered;
    }

    private void CbFilterFlagged_Changed(object sender, RoutedEventArgs e)
    {
        ApplyHistoryFilter();
    }

    private void CbHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbHistory.SelectedItem is not HistoryItem item) return;

        // The log stores relative paths like "Folder/Category/file.milk"
        // Try to find the file in active presets first, then deactivated
        var relativePath = item.PresetRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var activePath = Path.Combine(_presetPath, relativePath);
        var deactivatedPath = Path.Combine(_deactivatedPath, relativePath);

        if (File.Exists(activePath))
        {
            SelectPresetInTree(TvActiveFolders, IcActivePresets, activePath, isActive: true);
            PreviewPresetFile(activePath);
        }
        else if (File.Exists(deactivatedPath))
        {
            SelectPresetInTree(TvDeactivatedFolders, IcDeactivatedPresets, deactivatedPath, isActive: false);
            PreviewPresetFile(deactivatedPath);
        }
        else
        {
            var favoritesPath = Path.Combine(_favoritesPath, relativePath);
            if (File.Exists(favoritesPath))
            {
                SelectPresetInTree(TvFavoritesFolders, IcFavoritesPresets, favoritesPath, isActive: false);
                PreviewPresetFile(favoritesPath);
            }
        }
    }

    private void SelectPresetInTree(System.Windows.Controls.TreeView tree, System.Windows.Controls.ListBox presetList, string fullPath, bool isActive)
    {
        var dir = Path.GetDirectoryName(fullPath) ?? "";

        // Walk the tree to find and select the matching folder node
        foreach (TreeViewItem topItem in tree.Items)
        {
            if (topItem.Tag is string topPath && dir.StartsWith(topPath, StringComparison.OrdinalIgnoreCase))
            {
                topItem.IsExpanded = true;

                // Check if the file is directly in the top folder
                if (dir.Equals(topPath, StringComparison.OrdinalIgnoreCase))
                {
                    topItem.IsSelected = true;
                    topItem.BringIntoView();
                    if (isActive)
                        _selectedActiveFolder = topPath;
                    else
                        _selectedDeactivatedFolder = topPath;
                    PopulatePresetList(presetList, topPath);
                    SelectPresetInListBox(presetList, fullPath);
                    return;
                }

                // Check sub-items
                foreach (TreeViewItem subItem in topItem.Items)
                {
                    if (subItem.Tag is string subPath &&
                        dir.Equals(subPath, StringComparison.OrdinalIgnoreCase))
                    {
                        subItem.IsSelected = true;
                        subItem.BringIntoView();
                        if (isActive)
                            _selectedActiveFolder = subPath;
                        else
                            _selectedDeactivatedFolder = subPath;
                        PopulatePresetList(presetList, subPath);
                        SelectPresetInListBox(presetList, fullPath);
                        return;
                    }
                }
            }
        }
    }

    // ── Tree population ────────────────────────────────────────────

    private void PopulateActiveTree()
    {
        TvActiveFolders.Items.Clear();
        if (!Directory.Exists(_presetPath)) return;

        foreach (var topDir in Directory.GetDirectories(_presetPath).OrderBy(d => d))
        {
            if (IsUnderDeactivated(topDir)) continue;
            if (IsUnderFavorites(topDir)) continue;
            if (IsUnderDeleted(topDir)) continue;
            if (IsUnderTransition(topDir)) continue;
            var topName = Path.GetFileName(topDir);

            var topItem = CreateTreeItem(topName, topDir, true, isActive: true);

            foreach (var subDir in Directory.GetDirectories(topDir).OrderBy(d => d))
            {
                if (IsUnderDeactivated(subDir)) continue;
                if (IsUnderFavorites(subDir)) continue;
                if (IsUnderDeleted(subDir)) continue;
                if (IsUnderTransition(subDir)) continue;
                int count = Directory.GetFiles(subDir, "*.milk").Length;
                count += GetFavoriteMirrorCount(subDir);
                if (count == 0) continue;

                var subItem = CreateTreeItem($"{Path.GetFileName(subDir)} ({count})", subDir, false, isActive: true);
                topItem.Items.Add(subItem);
            }

            int topCount = Directory.GetFiles(topDir, "*.milk").Length + GetFavoriteMirrorCount(topDir);
            if (topItem.Items.Count > 0 || topCount > 0)
            {
                if (topCount > 0)
                    SetTreeItemHeader(topItem, $"{topName} ({topCount} + {topItem.Items.Count} sub)", true, isActive: true);
                TvActiveFolders.Items.Add(topItem);
            }
        }
    }

    private void PopulateDeactivatedTree()
    {
        TvDeactivatedFolders.Items.Clear();
        if (!Directory.Exists(_deactivatedPath)) return;

        foreach (var topDir in Directory.GetDirectories(_deactivatedPath).OrderBy(d => d))
        {
            var topName = Path.GetFileName(topDir);
            var topItem = CreateTreeItem(topName, topDir, true, isActive: false);

            foreach (var subDir in Directory.GetDirectories(topDir).OrderBy(d => d))
            {
                int count = Directory.GetFiles(subDir, "*.milk").Length;
                if (count == 0) continue;

                var subItem = CreateTreeItem($"{Path.GetFileName(subDir)} ({count})", subDir, false, isActive: false);
                topItem.Items.Add(subItem);
            }

            int topCount = Directory.GetFiles(topDir, "*.milk").Length;
            if (topItem.Items.Count > 0 || topCount > 0)
            {
                if (topCount > 0)
                    SetTreeItemHeader(topItem, $"{topName} ({topCount} + {topItem.Items.Count} sub)", true, isActive: false);
                TvDeactivatedFolders.Items.Add(topItem);
            }
        }
    }

    /// <summary>
    /// Creates a TreeViewItem with a text label and a small thumbs button for bulk move.
    /// </summary>
    private TreeViewItem CreateTreeItem(string text, string folderPath, bool isTopLevel, bool isActive)
    {
        var item = new TreeViewItem { Tag = folderPath };
        SetTreeItemHeader(item, text, isTopLevel, isActive);
        return item;
    }

    private void SetTreeItemHeader(TreeViewItem item, string text, bool isTopLevel, bool isActive)
    {
        var foreground = (System.Windows.Media.Brush)FindResource("TextBrush");

        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var btn = new Button
        {
            Content = isActive ? "👎" : "👍",
            ToolTip = isActive ? "Deactivate entire folder" : "Reactivate entire folder",
            Padding = new Thickness(3, 0, 3, 0),
            FontSize = 10,
            Margin = new Thickness(isActive ? 2 : 6, 0, 0, 0),
            Tag = item.Tag,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += isActive ? BtnDeactivateFolder_Click : BtnActivateFolder_Click;

        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(label);
        if (isActive)
        {
            var starBtn = new Button
            {
                Content = "☆",
                ToolTip = "Add entire folder to favorites",
                Padding = new Thickness(3, 0, 3, 0),
                FontSize = 10,
                Margin = new Thickness(11, 0, 0, 0),
                Tag = item.Tag,
                VerticalAlignment = VerticalAlignment.Center,
            };
            starBtn.Click += BtnFavoriteFolder_Click;
            panel.Children.Add(starBtn);
        }
        panel.Children.Add(btn);
        item.Header = panel;
    }

    // ── Tree selection ─────────────────────────────────────────────

    private void TvActiveFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is string folderPath)
        {
            _selectedActiveFolder = folderPath;
            PopulatePresetList(IcActivePresets, folderPath);
        }
    }

    private void TvDeactivatedFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is string folderPath)
        {
            _selectedDeactivatedFolder = folderPath;
            PopulatePresetList(IcDeactivatedPresets, folderPath);
        }
    }

    private void TvFavoritesFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is string folderPath)
        {
            _selectedFavoritesFolder = folderPath;
            PopulatePresetList(IcFavoritesPresets, folderPath);
        }
    }

    private void PopulatePresetList(ItemsControl control, string folderPath)
    {
        var items = new List<PresetItem>();
        if (Directory.Exists(folderPath))
        {
            foreach (var file in Directory.GetFiles(folderPath, "*.milk", SearchOption.AllDirectories).OrderBy(f => f))
            {
                items.Add(new PresetItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    FullPath = file,
                });
            }
        }

        // For active folders, also include favorites from the mirror folder
        if (control == IcActivePresets && !IsUnderDeactivated(folderPath) && !IsUnderFavorites(folderPath))
        {
            var relativePath = Path.GetRelativePath(_presetPath, folderPath);
            var favMirror = Path.Combine(_favoritesPath, relativePath);
            if (Directory.Exists(favMirror))
            {
                foreach (var file in Directory.GetFiles(favMirror, "*.milk", SearchOption.AllDirectories).OrderBy(f => f))
                {
                    items.Add(new PresetItem
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        FullPath = file,
                        IsFavorite = true,
                    });
                }
            }
            items = items.OrderBy(p => p.Name).ToList();
        }

        control.ItemsSource = items;
    }

    // ── Preview ────────────────────────────────────────────────────

    private void ActivePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IcActivePresets.SelectedItem is PresetItem item)
            PreviewPresetFile(item.FullPath);
    }

    private void DeactivatedPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IcDeactivatedPresets.SelectedItem is PresetItem item)
            PreviewPresetFile(item.FullPath);
    }

    private void FavoritesPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IcFavoritesPresets.SelectedItem is PresetItem item)
            PreviewPresetFile(item.FullPath);
    }

    private void BtnPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetFile) return;
        // Select the item in the parent ListBox so it highlights
        SelectPresetInListBox(IcActivePresets, presetFile);
        SelectPresetInListBox(IcDeactivatedPresets, presetFile);
        PreviewPresetFile(presetFile);
    }

    private void PreviewPresetFile(string presetFile)
    {
        if (_playfieldProxy == null) return;
        _playfieldProxy.WithProjectMRenderer(renderer =>
        {
            if (renderer == null) return;
            renderer.PreviewPreset(presetFile);
        });
    }

    private static void SelectPresetInListBox(System.Windows.Controls.ListBox listBox, string fullPath)
    {
        if (listBox.ItemsSource is not List<PresetItem> items) return;
        var match = items.FirstOrDefault(p => p.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            listBox.SelectedItem = match;
            listBox.ScrollIntoView(match);
        }
    }

    // ── Async helper ──────────────────────────────────────────────

    private async Task RunWithWaitCursor(Func<Task> action)
    {
        var previous = Cursor;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await action();
        }
        finally
        {
            Cursor = previous;
        }
    }

    // ── Deactivate single preset ───────────────────────────────────

    private async void BtnDeactivate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetFile) return;
        if (!File.Exists(presetFile)) return;

        await RunWithWaitCursor(async () =>
        {
            // Use the correct base path depending on whether the file is in favorites
            var basePath = IsUnderFavorites(presetFile) ? _favoritesPath : _presetPath;
            var relativePath = Path.GetRelativePath(basePath, presetFile);
            var destPath = Path.Combine(_deactivatedPath, relativePath);
            var destDir = Path.GetDirectoryName(destPath);

            await Task.Run(() =>
            {
                if (destDir != null) Directory.CreateDirectory(destDir);
                File.Move(presetFile, destPath, overwrite: false);
                if (IsUnderFavorites(presetFile)) CleanEmptyDirectories(_favoritesPath);
            });
            RefreshAfterMove();
        });
    }

    // ── Activate single preset ─────────────────────────────────────

    private async void BtnActivate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetFile) return;
        if (!File.Exists(presetFile)) return;

        await RunWithWaitCursor(async () =>
        {
            var relativePath = Path.GetRelativePath(_deactivatedPath, presetFile);
            var destPath = Path.Combine(_presetPath, relativePath);

            await Task.Run(() =>
            {
                MoveOrDeleteSource(presetFile, destPath);
                CleanEmptyDirectories(_deactivatedPath);
            });
            RefreshAfterMove();
        });
    }

    // ── Delete single deactivated preset ──────────────────────────

    private async void BtnDeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetFile) return;
        if (!File.Exists(presetFile)) return;

        await RunWithWaitCursor(async () =>
        {
            var relativePath = Path.GetRelativePath(_deactivatedPath, presetFile);
            var destPath = Path.Combine(_deletedPath, relativePath);

            await Task.Run(() =>
            {
                var destDir = Path.GetDirectoryName(destPath);
                if (destDir != null) Directory.CreateDirectory(destDir);
                MoveOrDeleteSource(presetFile, destPath);
                CleanEmptyDirectories(_deactivatedPath);
            });
            RefreshAfterMove();
        });
    }

    // ── Deactivate entire folder

    private async void BtnDeactivateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string folderPath) return;
        if (!Directory.Exists(folderPath)) return;

        e.Handled = true;

        await RunWithWaitCursor(async () =>
        {
            await Task.Run(() =>
            {
                var files = Directory.GetFiles(folderPath, "*.milk", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(_presetPath, file);
                    var destPath = Path.Combine(_deactivatedPath, relativePath);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null) Directory.CreateDirectory(destDir);
                    try { File.Move(file, destPath, overwrite: false); } catch { }
                }
            });
            RefreshAfterMove();
        });
    }

    // ── Activate entire folder ─────────────────────────────────────

    private async void BtnActivateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string folderPath) return;
        if (!Directory.Exists(folderPath)) return;

        e.Handled = true;

        await RunWithWaitCursor(async () =>
        {
            await Task.Run(() =>
            {
                var files = Directory.GetFiles(folderPath, "*.milk", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(_deactivatedPath, file);
                    var destPath = Path.Combine(_presetPath, relativePath);
                    MoveOrDeleteSource(file, destPath);
                }
                CleanEmptyDirectories(_deactivatedPath);
            });
            RefreshAfterMove();
        });
    }

    // ── Favorite single preset ─────────────────────────────────────

    private async void BtnFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetFile) return;
        if (!File.Exists(presetFile)) return;

        bool isFavorite = IsUnderFavorites(presetFile);

        await RunWithWaitCursor(async () =>
        {
            if (isFavorite)
            {
                // Unfavorite: move back to active
                var relativePath = Path.GetRelativePath(_favoritesPath, presetFile);
                var destPath = Path.Combine(_presetPath, relativePath);

                await Task.Run(() =>
                {
                    MoveOrDeleteSource(presetFile, destPath);
                    CleanEmptyDirectories(_favoritesPath);
                });
            }
            else
            {
                // Favorite: move to favorites
                var relativePath = Path.GetRelativePath(_presetPath, presetFile);
                var destPath = Path.Combine(_favoritesPath, relativePath);
                var destDir = Path.GetDirectoryName(destPath);

                await Task.Run(() =>
                {
                    if (destDir != null) Directory.CreateDirectory(destDir);
                    File.Move(presetFile, destPath, overwrite: false);
                });
            }
            RefreshAfterMove();
        });
    }

    // ── Unfavorite single preset ───────────────────────────────────

    private async void BtnUnfavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string presetFile) return;
        if (!File.Exists(presetFile)) return;

        await RunWithWaitCursor(async () =>
        {
            var relativePath = Path.GetRelativePath(_favoritesPath, presetFile);
            var destPath = Path.Combine(_presetPath, relativePath);

            await Task.Run(() =>
            {
                MoveOrDeleteSource(presetFile, destPath);
                CleanEmptyDirectories(_favoritesPath);
            });
            RefreshAfterMove();
        });
    }

    // ── Favorite entire folder

    private async void BtnFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string folderPath) return;
        if (!Directory.Exists(folderPath)) return;

        e.Handled = true;

        await RunWithWaitCursor(async () =>
        {
            await Task.Run(() =>
            {
                var files = Directory.GetFiles(folderPath, "*.milk", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(_presetPath, file);
                    var destPath = Path.Combine(_favoritesPath, relativePath);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null) Directory.CreateDirectory(destDir);
                    try { File.Move(file, destPath, overwrite: false); } catch { }
                }
            });
            RefreshAfterMove();
        });
    }

    // ── Refresh ────────────────────────────────────────────────────

    private void RefreshAfterMove()
    {
        // Save expanded state
        var expandedActive = GetExpandedPaths(TvActiveFolders);
        var expandedDeactivated = GetExpandedPaths(TvDeactivatedFolders);
        var expandedFavorites = GetExpandedPaths(TvFavoritesFolders);

        PopulateActiveTree();
        PopulateDeactivatedTree();
        PopulateFavoritesTree();

        // Restore expanded state and re-select
        RestoreExpandedPaths(TvActiveFolders, expandedActive, _selectedActiveFolder);
        RestoreExpandedPaths(TvDeactivatedFolders, expandedDeactivated, _selectedDeactivatedFolder);
        RestoreExpandedPaths(TvFavoritesFolders, expandedFavorites, _selectedFavoritesFolder);

        if (_selectedActiveFolder != null && Directory.Exists(_selectedActiveFolder))
            PopulatePresetList(IcActivePresets, _selectedActiveFolder);
        else
            IcActivePresets.ItemsSource = null;

        if (_selectedDeactivatedFolder != null && Directory.Exists(_selectedDeactivatedFolder))
            PopulatePresetList(IcDeactivatedPresets, _selectedDeactivatedFolder);
        else
            IcDeactivatedPresets.ItemsSource = null;

        if (_selectedFavoritesFolder != null && Directory.Exists(_selectedFavoritesFolder))
            PopulatePresetList(IcFavoritesPresets, _selectedFavoritesFolder);
        else
            IcFavoritesPresets.ItemsSource = null;
    }

    private static HashSet<string> GetExpandedPaths(System.Windows.Controls.TreeView tree)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TreeViewItem topItem in tree.Items)
        {
            if (topItem.IsExpanded && topItem.Tag is string p)
                paths.Add(p);
        }
        return paths;
    }

    private static void RestoreExpandedPaths(System.Windows.Controls.TreeView tree, HashSet<string> expandedPaths, string? selectedPath)
    {
        foreach (TreeViewItem topItem in tree.Items)
        {
            if (topItem.Tag is string p && expandedPaths.Contains(p))
                topItem.IsExpanded = true;

            if (selectedPath != null)
            {
                if (topItem.Tag is string tp && tp.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    topItem.IsSelected = true;
                    topItem.BringIntoView();
                }
                foreach (TreeViewItem subItem in topItem.Items)
                {
                    if (subItem.Tag is string sp && sp.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        topItem.IsExpanded = true;
                        subItem.IsSelected = true;
                        subItem.BringIntoView();
                    }
                }
            }
        }
    }

    private bool IsUnderDeactivated(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var deactivated = Path.GetFullPath(_deactivatedPath);
        return fullPath.StartsWith(deactivated, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUnderFavorites(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var favorites = Path.GetFullPath(_favoritesPath);
        return fullPath.StartsWith(favorites, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUnderDeleted(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var deleted = Path.GetFullPath(_deletedPath);
        return fullPath.StartsWith(deleted, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUnderTransition(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("Transition", StringComparison.OrdinalIgnoreCase)
            || name.Equals("! Transition", StringComparison.OrdinalIgnoreCase);
    }

    private int GetFavoriteMirrorCount(string activeFolder)
    {
        var relativePath = Path.GetRelativePath(_presetPath, activeFolder);
        var favMirror = Path.Combine(_favoritesPath, relativePath);
        if (Directory.Exists(favMirror))
            return Directory.GetFiles(favMirror, "*.milk").Length;
        return 0;
    }

    private void PopulateFavoritesTree()
    {
        TvFavoritesFolders.Items.Clear();
        if (!Directory.Exists(_favoritesPath)) return;

        foreach (var topDir in Directory.GetDirectories(_favoritesPath).OrderBy(d => d))
        {
            var topName = Path.GetFileName(topDir);
            var topItem = CreateFavoritesTreeItem(topName, topDir, true);

            foreach (var subDir in Directory.GetDirectories(topDir).OrderBy(d => d))
            {
                int count = Directory.GetFiles(subDir, "*.milk").Length;
                if (count == 0) continue;

                var subItem = CreateFavoritesTreeItem($"{Path.GetFileName(subDir)} ({count})", subDir, false);
                topItem.Items.Add(subItem);
            }

            int topCount = Directory.GetFiles(topDir, "*.milk").Length;
            if (topItem.Items.Count > 0 || topCount > 0)
            {
                if (topCount > 0)
                    SetFavoritesTreeItemHeader(topItem, $"{topName} ({topCount} + {topItem.Items.Count} sub)", true);
                TvFavoritesFolders.Items.Add(topItem);
            }
        }
    }

    private TreeViewItem CreateFavoritesTreeItem(string text, string folderPath, bool isTopLevel)
    {
        var item = new TreeViewItem { Tag = folderPath };
        SetFavoritesTreeItemHeader(item, text, isTopLevel);
        return item;
    }

    private void SetFavoritesTreeItemHeader(TreeViewItem item, string text, bool isTopLevel)
    {
        var foreground = (System.Windows.Media.Brush)FindResource("TextBrush");

        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var btn = new Button
        {
            Content = "★",
            ToolTip = "Remove entire folder from favorites",
            Padding = new Thickness(3, 0, 3, 0),
            FontSize = 10,
            Margin = new Thickness(6, 0, 0, 0),
            Tag = item.Tag,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += BtnUnfavoriteFolder_Click;

        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(label);
        panel.Children.Add(btn);
        item.Header = panel;
    }

    private async void BtnUnfavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        DebugLog.Log("PresetBrowser", $"BtnUnfavoriteFolder_Click fired, sender type={sender?.GetType().Name}");
        if (sender is not Button btn || btn.Tag is not string folderPath)
        {
            DebugLog.Log("PresetBrowser", $"  Early return: sender is not Button or Tag is not string (Tag={((sender as Button)?.Tag)})");
            return;
        }
        DebugLog.Log("PresetBrowser", $"  folderPath={folderPath}");
        if (!Directory.Exists(folderPath))
        {
            DebugLog.Log("PresetBrowser", $"  Early return: folder does not exist");
            return;
        }

        e.Handled = true;

        await RunWithWaitCursor(async () =>
        {
            await Task.Run(() =>
            {
                var files = Directory.GetFiles(folderPath, "*.milk", SearchOption.AllDirectories);
                DebugLog.Log("PresetBrowser", $"  Found {files.Length} .milk files to unfavorite");
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(_favoritesPath, file);
                    var destPath = Path.Combine(_presetPath, relativePath);
                    DebugLog.Log("PresetBrowser", $"  Moving: {file} -> {destPath}");
                    MoveOrDeleteSource(file, destPath);
                }
                CleanEmptyDirectories(_favoritesPath);
            });
            RefreshAfterMove();
        });
    }

    private static void CleanEmptyDirectories(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return;

        foreach (var dir in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (Directory.Exists(dir) &&
                !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try { Directory.Delete(dir); } catch { }
            }
        }
    }

    /// <summary>
    /// Moves a file to the destination. If the destination already exists,
    /// deletes the source instead (the file is already where it needs to be).
    /// </summary>
    private static void MoveOrDeleteSource(string sourceFile, string destPath)
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (destDir != null) Directory.CreateDirectory(destDir);

        if (File.Exists(destPath))
        {
            // Destination already has the file — just remove the source copy
            try { File.Delete(sourceFile); } catch { }
        }
        else
        {
            File.Move(sourceFile, destPath);
        }
    }

    // ── Window closing ─────────────────────────────────────────────

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _playfieldProxy?.WithProjectMRenderer(r =>
        {
            if (r == null) return;
            // Unlock first so the duration is restored before ReloadPlaylist
            // triggers play_next, which starts the internal timer.
            r.LockPreset(false);
            r.ReloadPlaylist();
        });
    }
}

/// <summary>Simple data item for the preset list.</summary>
public class PresetItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsFavorite { get; set; }
    public string StarIcon => IsFavorite ? "★" : "☆";
    public string StarToolTip => IsFavorite ? "Remove from favorites" : "Add to favorites";
    public System.Windows.Media.Brush StarColor => IsFavorite
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 100, 255))
        : System.Windows.Media.Brushes.Gray;
}

/// <summary>Item for the history combobox.</summary>
public class HistoryItem
{
    public string Display { get; set; } = "";
    public string PresetRelativePath { get; set; } = "";
    public bool IsFlagged { get; set; }
    public FontWeight ItemFontWeight => IsFlagged ? FontWeights.Bold : FontWeights.Normal;
    public override string ToString() => Display;
}
