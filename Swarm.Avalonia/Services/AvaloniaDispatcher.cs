using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Swarm.Core.Abstractions;

namespace Swarm.Avalonia.Services;

/// <summary>
/// Avalonia implementation of IDispatcher.
/// </summary>
public class AvaloniaDispatcher : Swarm.Core.Abstractions.IDispatcher
{
    public void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Invoke(action);
        }
    }

    public Task InvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        else
        {
            return Dispatcher.UIThread.InvokeAsync(action).GetTask();
        }
    }

    public bool CheckAccess()
    {
        return Dispatcher.UIThread.CheckAccess();
    }
}
