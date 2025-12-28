using Swarm.Core.Abstractions;
using WpfApp = System.Windows.Application;

namespace Swarm.Services;

/// <summary>
/// WPF implementation of IDispatcher using Application.Current.Dispatcher.
/// </summary>
public class WpfDispatcher : IDispatcher
{
    public void Invoke(Action action)
    {
        if (WpfApp.Current?.Dispatcher.CheckAccess() == true)
        {
            action();
        }
        else
        {
            WpfApp.Current?.Dispatcher.Invoke(action);
        }
    }

    public Task InvokeAsync(Action action)
    {
        if (WpfApp.Current?.Dispatcher.CheckAccess() == true)
        {
            action();
            return Task.CompletedTask;
        }
        else
        {
            return WpfApp.Current?.Dispatcher.InvokeAsync(action).Task ?? Task.CompletedTask;
        }
    }

    public bool CheckAccess()
    {
        return WpfApp.Current?.Dispatcher.CheckAccess() ?? false;
    }
}
