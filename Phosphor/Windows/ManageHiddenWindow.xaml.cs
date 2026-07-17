using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Phosphor.Plugin.Abstractions;
using ListBox = System.Windows.Controls.ListBox;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using Button = System.Windows.Controls.Button;

namespace Phosphor.Windows;

/// <summary>
/// Themed modal for managing a source's hidden items (<see cref="IHideable"/>). Two Extended-select
/// lists (Visible ⇄ Hidden) for block moves, plus a Group → SubGroup tree so a whole super-group
/// (e.g. "Sports") or category (e.g. "Country") can be hidden/shown at once. Transient — closes on
/// OK/Cancel; no navigation. Inherits the app's dark theme via implicit styles + BgBrush chrome.
/// </summary>
public partial class ManageHiddenWindow : Window
{
    private readonly ObservableCollection<HideableItem> _visible = new();
    private readonly ObservableCollection<HideableItem> _hidden = new();

    /// <summary>True when the user clicked OK.</summary>
    public bool Applied { get; private set; }

    /// <summary>The resulting hidden id set (valid when <see cref="Applied"/>).</summary>
    public HashSet<string> HiddenIds { get; private set; } = new(StringComparer.Ordinal);

    public ManageHiddenWindow(IReadOnlyList<HideableItem> all, HashSet<string> hidden)
    {
        InitializeComponent();

        foreach (var it in all)
            (hidden.Contains(it.Id) ? _hidden : _visible).Add(it);

        LbVisible.ItemsSource = _visible;
        LbHidden.ItemsSource = _hidden;
        BuildGroupTree(all);
    }

    // Group → SubGroup hierarchy for the tree (each carries the ids under it).
    private sealed class GroupNode
    {
        public string Label { get; init; } = "";
        public List<string> Ids { get; } = new();
    }

    // Every tree item (group + sub-group), so headers/dimming can refresh after moves.
    private readonly List<TreeViewItem> _treeItems = new();

    private void BuildGroupTree(IReadOnlyList<HideableItem> all)
    {
        // Top level = Group (Music/Talk/Sports/Other); children = SubGroup (category).
        var byGroup = all
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Group) ? "Other" : i.Group!)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var g in byGroup)
        {
            var groupNode = new GroupNode { Label = g.Key };
            groupNode.Ids.AddRange(g.Select(i => i.Id));
            var groupItem = new TreeViewItem { Tag = groupNode, IsExpanded = false };
            _treeItems.Add(groupItem);

            var bySub = g
                .GroupBy(i => string.IsNullOrWhiteSpace(i.SubGroup) ? "(uncategorized)" : i.SubGroup!)
                .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var s in bySub)
            {
                var subNode = new GroupNode { Label = s.Key };
                subNode.Ids.AddRange(s.Select(i => i.Id));
                var subItem = new TreeViewItem { Tag = subNode };
                _treeItems.Add(subItem);
                groupItem.Items.Add(subItem);
            }
            TvGroups.Items.Add(groupItem);
        }
        RefreshTree();
    }

    /// <summary>Updates each tree node's header ("x/y hidden") and dims fully-hidden nodes, so the
    /// user can tell whether a whole group/category is hidden.</summary>
    private void RefreshTree()
    {
        var hiddenIds = new HashSet<string>(_hidden.Select(i => i.Id), StringComparer.Ordinal);
        foreach (var item in _treeItems)
        {
            if (item.Tag is not GroupNode n) continue;
            int hidden = n.Ids.Count(hiddenIds.Contains);
            int total = n.Ids.Count;
            bool allHidden = total > 0 && hidden == total;
            item.Header = allHidden ? $"{n.Label} — hidden" : $"{n.Label} ({hidden}/{total} hidden)";
            item.Opacity = allHidden ? 0.45 : 1.0;
        }
    }

    private static void MoveSelected(System.Windows.Controls.ListBox from, ObservableCollection<HideableItem> fromCol,
        ObservableCollection<HideableItem> toCol)
    {
        foreach (var it in from.SelectedItems.Cast<HideableItem>().ToList())
        {
            fromCol.Remove(it);
            toCol.Add(it);
        }
    }

    private void MoveIds(IReadOnlyCollection<string> ids, ObservableCollection<HideableItem> fromCol,
        ObservableCollection<HideableItem> toCol)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (var it in fromCol.Where(i => set.Contains(i.Id)).ToList())
        {
            fromCol.Remove(it);
            toCol.Add(it);
        }
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e) { MoveSelected(LbVisible, _visible, _hidden); RefreshTree(); }
    private void BtnShow_Click(object sender, RoutedEventArgs e) { MoveSelected(LbHidden, _hidden, _visible); RefreshTree(); }

    private void BtnHideGroup_Click(object sender, RoutedEventArgs e)
    {
        if (TvGroups.SelectedItem is TreeViewItem { Tag: GroupNode n })
        {
            MoveIds(n.Ids, _visible, _hidden);
            RefreshTree();
        }
    }

    private void BtnShowGroup_Click(object sender, RoutedEventArgs e)
    {
        if (TvGroups.SelectedItem is TreeViewItem { Tag: GroupNode n })
        {
            MoveIds(n.Ids, _hidden, _visible);
            RefreshTree();
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Applied = true;
        HiddenIds = new HashSet<string>(_hidden.Select(i => i.Id), StringComparer.Ordinal);
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
