using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace BatteryMonitor;

/// <summary>
/// Helper extensions for the <see cref="DispatcherQueue"/> in WinUI3.
/// </summary>
public static class UIThreadHelper
{
    /// <summary>
    /// Executes the given action on the UI thread synchronously.
    /// </summary>
    public static void InvokeOnUI(this DispatcherQueue dispatcherQueue, Action action)
    {
        if (dispatcherQueue is null)
            throw new ArgumentNullException(nameof(dispatcherQueue));
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        if (dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            var taskCompletionSource = new TaskCompletionSource();
            dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });
            taskCompletionSource.Task.Wait();
        }
    }

    /// <summary>
    /// Executes the given action on the UI thread asynchronously.
    /// </summary>
    public static async Task InvokeOnUIAsync(this DispatcherQueue dispatcherQueue, Action action)
    {
        if (dispatcherQueue is null)
            throw new ArgumentNullException(nameof(dispatcherQueue));
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        if (dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            var taskCompletionSource = new TaskCompletionSource();
            dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });
            await taskCompletionSource.Task;
        }
    }

    /// <summary>
    /// Executes the given function on the UI thread synchronously and returns the result.
    /// </summary>
    public static T InvokeOnUI<T>(this DispatcherQueue dispatcherQueue, Func<T> func)
    {
        if (dispatcherQueue is null)
            throw new ArgumentNullException(nameof(dispatcherQueue));
        if (func is null)
            throw new ArgumentNullException(nameof(func));

        if (dispatcherQueue.HasThreadAccess)
        {
            return func();
        }
        else
        {
            var taskCompletionSource = new TaskCompletionSource<T>();
            dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    T result = func();
                    taskCompletionSource.SetResult(result);
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });
            return taskCompletionSource.Task.Result;
        }
    }

    /// <summary>
    /// Executes the given function on the UI thread asynchronously and returns the result.
    /// </summary>
    public static async Task<T> InvokeOnUIAsync<T>(this DispatcherQueue dispatcherQueue, Func<T> func)
    {
        if (dispatcherQueue is null)
            throw new ArgumentNullException(nameof(dispatcherQueue));
        if (func is null)
            throw new ArgumentNullException(nameof(func));

        if (dispatcherQueue.HasThreadAccess)
        {
            return func();
        }
        else
        {
            var taskCompletionSource = new TaskCompletionSource<T>();
            dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    T result = func();
                    taskCompletionSource.SetResult(result);
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });
            return await taskCompletionSource.Task;
        }
    }

    /// <summary>
    /// Executes an asynchronous function on the UI thread and returns the result as a Task.
    /// </summary>
    /// <typeparam name="T">The type of the result returned by the asynchronous function.</typeparam>
    /// <param name="dispatcherQueue">The DispatcherQueue associated with the UI thread.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <returns>A Task representing the asynchronous operation with the result of type T.</returns>
    public static async Task<T> InvokeOnUIAsync<T>(this DispatcherQueue dispatcherQueue, Func<Task<T>> func)
    {
        if (dispatcherQueue is null)
            throw new ArgumentNullException(nameof(dispatcherQueue));
        if (func is null)
            throw new ArgumentNullException(nameof(func));

        if (dispatcherQueue.HasThreadAccess)
        {   // Already on the UI thread, execute the function directly
            return await func();
        }
        else
        {   // Not on the UI thread, enqueue the function to be executed
            var taskCompletionSource = new TaskCompletionSource<T>();
            dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    T result = await func();
                    taskCompletionSource.SetResult(result);
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });
            return await taskCompletionSource.Task;
        }
    }

    /// <summary>
    /// Executes an asynchronous function on the UI thread without returning a result.
    /// </summary>
    /// <param name="dispatcherQueue">The DispatcherQueue associated with the UI thread.</param>
    /// <param name="func">The asynchronous function to execute.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public static async Task InvokeOnUIAsync(this DispatcherQueue dispatcherQueue, Func<Task> func)
    {
        if (dispatcherQueue is null)
            throw new ArgumentNullException(nameof(dispatcherQueue));
        if (func is null)
            throw new ArgumentNullException(nameof(func));

        if (dispatcherQueue.HasThreadAccess)
        {   // Already on the UI thread, execute the function directly
            await func();
        }
        else
        {   // Not on the UI thread, enqueue the function to be executed
            var taskCompletionSource = new TaskCompletionSource();
            dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await func();
                    taskCompletionSource.SetResult();
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });
            await taskCompletionSource.Task;
        }
    }
}