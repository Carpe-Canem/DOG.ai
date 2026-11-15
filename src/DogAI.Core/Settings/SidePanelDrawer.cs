namespace DogAI.Core.Settings;

/// <summary>
/// Below 1050 px the side panel (chat settings, the Models side panel, the document search model) is not beside the
/// page but a drawer over it, opened with a button in the heading. This says whether that drawer is open.
/// It is never remembered: a narrow window always starts with the page in view, and opening the drawer never changes
/// whether the owner hid the wide side panel (<see cref="DogAI.Core.Chat.ChatPanels.AsideId"/>). It closes when the
/// page changes and when the window grows past 1050 px.
/// </summary>
public sealed class SidePanelDrawer
{
    public bool Open { get; private set; }

    /// <summary>Raised only when <see cref="Open"/> changes.</summary>
    public event Action? Changed;

    public void Show() => Set(true);
    public void Close() => Set(false);
    public void Toggle() => Set(!Open);

    private void Set(bool open)
    {
        if (Open == open) return;
        Open = open;
        Changed?.Invoke();
    }
}
