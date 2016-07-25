﻿namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using Xunit;
    using Xunit.Abstractions;

    public abstract class TestBase
    {
        protected const int AsyncDelay = 500;

        protected const int TestTimeout = 1000;

        private const int GCAllocationAttempts = 5;

        protected TestBase(ITestOutputHelper logger)
        {
            this.Logger = logger;
        }

        /// <summary>
        /// Gets or sets the source of <see cref="TimeoutToken"/> that influences
        /// when tests consider themselves to be timed out.
        /// </summary>
        protected CancellationTokenSource TimeoutTokenSource { get; set; } = new CancellationTokenSource(TestTimeout);

        /// <summary>
        /// Gets a token that is canceled when the test times out,
        /// per the policy set by <see cref="TimeoutTokenSource"/>.
        /// </summary>
        protected CancellationToken TimeoutToken => this.TimeoutTokenSource.Token;

        /// <summary>
        /// Gets or sets the logger to use for writing text to be captured in the test results.
        /// </summary>
        protected ITestOutputHelper Logger { get; set; }

        /// <summary>
        /// Verifies that continuations scheduled on a task will not be executed inline with the specified completing action.
        /// </summary>
        /// <param name="antecedent">The task to test.</param>
        /// <param name="completingAction">The action that results in the synchronous completion of the task.</param>
        protected static void VerifyDoesNotInlineContinuations(Task antecedent, Action completingAction)
        {
            Requires.NotNull(antecedent, nameof(antecedent));
            Requires.NotNull(completingAction, nameof(completingAction));

            var completingActionFinished = new ManualResetEventSlim();
            var continuation = antecedent.ContinueWith(
                _ => Assert.True(completingActionFinished.Wait(AsyncDelay)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            completingAction();
            completingActionFinished.Set();

            // Rethrow the exception if it turned out it deadlocked.
            continuation.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Verifies that continuations scheduled on a task can be executed inline with the specified completing action.
        /// </summary>
        /// <param name="antecedent">The task to test.</param>
        /// <param name="completingAction">The action that results in the synchronous completion of the task.</param>
        protected static void VerifyCanInlineContinuations(Task antecedent, Action completingAction)
        {
            Requires.NotNull(antecedent, nameof(antecedent));
            Requires.NotNull(completingAction, nameof(completingAction));

            Thread callingThread = Thread.CurrentThread;
            var continuation = antecedent.ContinueWith(
                _ => Assert.Equal(callingThread, Thread.CurrentThread),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            completingAction();
            Assert.True(continuation.IsCompleted);

            // Rethrow any exceptions.
            continuation.GetAwaiter().GetResult();
        }

        protected void CheckGCPressure(Action scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
        {
            // prime the pump
            for (int i = 0; i < iterations; i++)
            {
                scenario();
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool passingAttemptObserved = false;
            for (int attempt = 1; attempt <= allowedAttempts; attempt++)
            {
                this.Logger?.WriteLine("Iteration {0}", attempt);
                long initialMemory = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    scenario();
                }

                long allocated = (GC.GetTotalMemory(false) - initialMemory) / iterations;

                // If there is a dispatcher sync context, let it run for a bit.
                // This allows any posted messages that are now obsolete to be released.
                if (SynchronizationContext.Current is DispatcherSynchronizationContext)
                {
                    var frame = new DispatcherFrame();
                    SynchronizationContext.Current.Post(state => frame.Continue = false, null);
                    Dispatcher.PushFrame(frame);
                }

                long leaked = (GC.GetTotalMemory(true) - initialMemory) / iterations;

                this.Logger?.WriteLine("{0} bytes leaked per iteration.", leaked);
                this.Logger?.WriteLine("{0} bytes allocated per iteration ({1} allowed).", allocated, maxBytesAllocated);

                if (leaked <= 0 && allocated <= maxBytesAllocated)
                {
                    passingAttemptObserved = true;
                }

                if (!passingAttemptObserved)
                {
                    // give the system a bit of cool down time to increase the odds we'll pass next time.
                    GC.Collect();
                    Thread.Sleep(250);
                }
            }

            Assert.True(passingAttemptObserved);
        }

        protected async Task CheckGCPressureAsync(Func<Task> scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
        {
            // prime the pump
            for (int i = 0; i < iterations; i++)
            {
                await scenario();
            }

            // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
            bool passingAttemptObserved = false;
            for (int attempt = 1; attempt <= allowedAttempts; attempt++)
            {
                this.Logger?.WriteLine("Iteration {0}", attempt);
                long initialMemory = GC.GetTotalMemory(true);
                for (int i = 0; i < iterations; i++)
                {
                    await scenario();
                }

                long allocated = (GC.GetTotalMemory(false) - initialMemory) / iterations;

                // Allow the message queue to drain.
                await Task.Yield();

                long leaked = (GC.GetTotalMemory(true) - initialMemory) / iterations;

                this.Logger?.WriteLine("{0} bytes leaked per iteration.", leaked);
                this.Logger?.WriteLine("{0} bytes allocated per iteration ({1} allowed).", allocated, maxBytesAllocated);

                if (leaked < iterations && allocated <= maxBytesAllocated)
                {
                    passingAttemptObserved = true;
                }

                if (!passingAttemptObserved)
                {
                    // give the system a bit of cool down time to increase the odds we'll pass next time.
                    GC.Collect();
                    Thread.Sleep(250);
                }
            }

            Assert.True(passingAttemptObserved);
        }

        protected void CheckGCPressure(Func<Task> scenario, int maxBytesAllocated, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
        {
            this.ExecuteOnDispatcher(() => this.CheckGCPressureAsync(scenario, maxBytesAllocated));
        }

        /// <summary>
        /// Executes the delegate on a thread with <see cref="ApartmentState.STA"/>
        /// and without a current <see cref="SynchronizationContext"/>.
        /// </summary>
        /// <param name="action">The delegate to execute.</param>
        protected void ExecuteOnSTA(Action action)
        {
            Requires.NotNull(action, nameof(action));

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
                && SynchronizationContext.Current == null)
            {
                action();
                return;
            }

            Exception staFailure = null;
            var staThread = new Thread(state =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    staFailure = ex;
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            if (staFailure != null)
            {
                ExceptionDispatchInfo.Capture(staFailure).Throw(); // rethrow preserving callstack.
            }
        }

        protected void ExecuteOnDispatcher(Action action)
        {
            this.ExecuteOnDispatcher(delegate
            {
                action();
                return TplExtensions.CompletedTask;
            });
        }

        protected void ExecuteOnDispatcher(Func<Task> action)
        {
            this.ExecuteOnSTA(() =>
            {
                if (!(SynchronizationContext.Current is DispatcherSynchronizationContext))
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                }

                var frame = new DispatcherFrame();
                Exception failure = null;
                SynchronizationContext.Current.Post(
                    async _ =>
                    {
                        try
                        {
                            await action();
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                        finally
                        {
                            frame.Continue = false;
                        }
                    },
                    null);

                Dispatcher.PushFrame(frame);
                if (failure != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }
            });
        }
    }
}