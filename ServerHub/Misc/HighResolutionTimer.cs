﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;


namespace ServerHub.Misc
{
    /// <summary>
    /// Hight precision non overlapping timer
    /// Came from 
    /// https://stackoverflow.com/a/41697139/548894
    /// </summary>
    /// <remarks>
    /// This implementation guaranteed that Elapsed events 
    /// are not overlapped with different threads. 
    /// Which is important, because a state of the event handler attached to  Elapsed,
    /// may be left unprotected of multi threaded access
    /// </remarks>
    public class HighResolutionTimer
    {
        public static HighResolutionTimer LoopTimer { get; private set; } = new HighResolutionTimer(1000 / 30f);
        public static HighResolutionTimer VoIPTimer { get; private set; } = new HighResolutionTimer(1000 / 30f);

        /// <summary>
        /// Tick time length in [ms]
        /// </summary>
        public static readonly double TickLength = 1000f / Stopwatch.Frequency;

        /// <summary>
        /// Tick frequency
        /// </summary>
        public static readonly double Frequency = Stopwatch.Frequency;

        /// <summary>
        /// True if the system/operating system supports HighResolution timer
        /// </summary>
        public static bool IsHighResolution = Stopwatch.IsHighResolution;

        /// <summary>
        /// Invoked when the timer is elapsed
        /// </summary>
        public event EventHandler<HighResolutionTimerElapsedEventArgs> Elapsed;

        /// <summary>
        /// Invoked after Elapsed event
        /// </summary>
        public event EventHandler<HighResolutionTimerElapsedEventArgs> AfterElapsed;

        /// <summary>
        ///  Execution thread
        /// </summary>
        public Thread thread;

        /// <summary>
        /// The interval of timer ticks [ms]
        /// </summary>
        private volatile float _interval;

        /// <summary>
        /// The timer is running
        /// </summary>
        private volatile bool _isRunning;
        
        /// <summary>
        /// Creates a timer with 1 [ms] interval
        /// </summary>
        public HighResolutionTimer() : this(1f)
        {
        }

        /// <summary>
        /// Creates timer with interval in [ms]
        /// </summary>
        /// <param name="interval">Interval time in [ms]</param>
        public HighResolutionTimer(float interval)
        {
            Interval = interval;
        }

        /// <summary>
        /// The interval of a timer in [ms]
        /// </summary>
        public float Interval
        {
            get => _interval;
            set
            {
                if (value < 0f || Single.IsNaN(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _interval = value;
            }
        }

        /// <summary>
        /// True when timer is running
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// If true, sets the execution thread to ThreadPriority.Highest
        /// (works after the next Start())
        /// </summary>
        /// <remarks>
        /// It might help in some cases and get things worse in others. 
        /// It suggested that you do some studies if you apply
        /// </remarks>
        public bool UseHighPriorityThread { get; set; } = false;

        /// <summary>
        /// Starts the timer
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _isRunning = true;
            thread = new Thread(ExecuteTimer)
            {
                IsBackground = true,
            };

            if (UseHighPriorityThread)
            {
                thread.Priority = ThreadPriority.Highest;
            }
            thread.Start();
        }

        /// <summary>
        /// Stops the timer
        /// </summary>
        /// <remarks>
        /// This function is waiting an executing thread (which do  to stop and join.
        /// </remarks>
        public void Stop(bool joinThread = true)
        {
            _isRunning = false;

            // Even if _thread.Join may take time it is guaranteed that 
            // Elapsed event is never called overlapped with different threads
            if (joinThread && Thread.CurrentThread != thread)
            {
                thread.Join();
            }
        }

        private void ExecuteTimer()
        {
            float nextTrigger = 0f;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            while (_isRunning)
            {
                nextTrigger += _interval;
                double elapsed;

                while (true)
                {
                    elapsed = ElapsedHiRes(stopwatch);
                    double diff = nextTrigger - elapsed;
                    if (diff <= 0f)
                        break;

                    if (diff < 1f)
                        Thread.SpinWait(10);
                    else
                        Thread.Sleep(Math.Floor(diff));

                    if (!_isRunning)
                        return;
                }


                double delay = elapsed - nextTrigger;

                if (Elapsed != null)
                {
                    foreach (EventHandler<HighResolutionTimerElapsedEventArgs> nextDel in Elapsed.GetInvocationList())
                    {
                        try
                        {
                            nextDel.Invoke(this, new HighResolutionTimerElapsedEventArgs(delay));
                        }
                        catch (Exception e)
                        {
                            if (Settings.Instance.Server.ShowTickEventExceptions)
                                Logger.Instance.Exception($"Exception in {nextDel.Method.Name} on tick event: {e}");
                        }
                    }
                }

                if (AfterElapsed != null)
                {
                    foreach (EventHandler<HighResolutionTimerElapsedEventArgs> nextDel in AfterElapsed.GetInvocationList())
                    {
                        try
                        {
                            nextDel.Invoke(this, new HighResolutionTimerElapsedEventArgs(delay));
                        }
                        catch (Exception e)
                        {
                            if (Settings.Instance.Server.ShowTickEventExceptions)
                                Logger.Instance.Exception($"Exception in {nextDel.Method.Name} on tick event: {e}");
                        }
                    }
                }

                if (!_isRunning)
                    return;

                // restarting the timer in every hour to prevent precision problems
                if (stopwatch.Elapsed.TotalHours >= 1d)
                {
                    stopwatch.Restart();
                    nextTrigger = 0f;
                }
            }

            stopwatch.Stop();
        }

        private static double ElapsedHiRes(Stopwatch stopwatch)
        {
            return stopwatch.ElapsedTicks * TickLength;
        }
    }


    public class HighResolutionTimerElapsedEventArgs : EventArgs
    {
        /// <summary>/// Real timer delay in [ms]/// </summary>
        public double Delay { get; }

        internal HighResolutionTimerElapsedEventArgs(double delay)
        {
            Delay = delay;
        }
    }
}