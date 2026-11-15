// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Runtime.ExceptionServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GraphReader.App.Tests;

internal static class StaTestHost
{
    static StaTestHost()
    {
        // Keep WPF rendering deterministic and independent of hosted-runner GPU availability.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Run(
            () =>
            {
                action();
                return true;
            });
    }

    public static T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        T? result = default;
        ExceptionDispatchInfo? failure = null;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(dispatcher));
                    _ = dispatcher.BeginInvoke(
                        () =>
                        {
                            try
                            {
                                result = action();
                            }
                            catch (Exception exception)
                            {
                                failure = ExceptionDispatchInfo.Capture(exception);
                            }
                            finally
                            {
                                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                            }
                        },
                        DispatcherPriority.Send);
                    Dispatcher.Run();
                    completed.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        try
        {
            _ = completed.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Assert.Fail("The STA test operation timed out.");
        }
        failure?.Throw();
        return result!;
    }
}
