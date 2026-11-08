using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using JERCPLD.Desktop.ViewModels;

namespace JERCPLD.Desktop.Views;

public partial class MainWindow : Window
{
    private INotifyCollectionChanged? _observedLogs;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachLogAutoScroll();
    }

    private void AttachLogAutoScroll()
    {
        if (_observedLogs is not null)
        {
            _observedLogs.CollectionChanged -= OnLogsChanged;
            _observedLogs = null;
        }

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _observedLogs = viewModel.Logs;
        _observedLogs.CollectionChanged += OnLogsChanged;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (LogList.ItemCount > 0)
            {
                LogList.ScrollIntoView(LogList.ItemCount - 1);
            }
        }, DispatcherPriority.Background);
    }
}
