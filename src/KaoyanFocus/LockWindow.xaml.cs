using System.Windows;

namespace KaoyanFocus;

public partial class LockWindow : Window
{
    public event Action? Unlocked;

    public LockWindow(AppState state, StateStore store) => InitializeComponent();

    internal void RaiseUnlocked() => Unlocked?.Invoke();
}
