using CommunityToolkit.Mvvm.ComponentModel;

namespace Phosphor;

/// <summary>One button in the browse letter-jump strip (a bucket from a source's alpha index).</summary>
public sealed partial class AlphaLetterItem : ObservableObject
{
    private bool _isActive;

    /// <summary>True for the letter containing the first visible row.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public AlphaLetterItem(string label, int startOffset, int count)
    {
        Label = label;
        StartOffset = startOffset;
        Count = count;
    }

    public string Label { get; }

    /// <summary>Zero-based offset of the bucket's first item in the title-sorted list.</summary>
    public int StartOffset { get; }

    public int Count { get; }

    /// <summary>False for letters with no items (rendered disabled).</summary>
    public bool HasItems => Count > 0;
}
