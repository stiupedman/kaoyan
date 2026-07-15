using System.ComponentModel;
using System.Windows;

namespace KaoyanFocus;

public partial class ShieldWindow : Window
{
    DisplayMonitor monitor;
    bool allowClose;

    public ShieldWindow(DisplayMonitor monitor)
    {
        InitializeComponent();
        this.monitor = monitor;
        SourceInitialized += (_, _) => MoveToMonitor(monitor);
        Loaded += (_, _) => MoveToMonitor(monitor);
    }

    public void MoveToMonitor(DisplayMonitor value)
    {
        monitor = value;
        WindowProtection.FillMonitor(this, monitor, false);
    }

    public void CloseForUnlock()
    {
        allowClose = true;
        Close();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!allowClose) e.Cancel = true;
    }
}
