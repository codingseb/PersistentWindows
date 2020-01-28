﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedWinapi;
using ManagedWinapi.Hooks;
using ManagedWinapi.Windows;
using Microsoft.Win32;
using Ninjacrab.PersistentWindows.Common.Diagnostics;
using Ninjacrab.PersistentWindows.Common.Models;
using Ninjacrab.PersistentWindows.Common.WinApiBridge;

namespace Ninjacrab.PersistentWindows.Common
{
    public class PersistentWindowProcessor : IDisposable
    {
        // read and update this from a config file eventually
        private const int MaxAppsMoveUpdate = 4;
        private const int MaxAppsMoveDelay = 60; // accept massive app move in 60 seconds
        private int pendingAppsMoveTimer = 0;
        private int pendingAppMoveSum = 0;
        private Hook windowProcHook = null;
        private Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>> monitorApplications = null;
        private object displayChangeLock = null;

        public void Start()
        {
            monitorApplications = new Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>>();
            displayChangeLock = new object();
            CaptureApplicationsOnCurrentDisplays(initialCapture: true);

            var thread = new Thread(InternalRun);
            thread.IsBackground = true;
            thread.Name = "PersistentWindowProcessor.InternalRun()";
            thread.Start();

            SystemEvents.DisplaySettingsChanged += 
                (s, e) =>
                {
                    Log.Info("Display settings changed");
                    BeginRestoreApplicationsOnCurrentDisplays();
                };

            SystemEvents.PowerModeChanged += 
                (s, e) =>
                {
                    switch (e.Mode)
                    {
                        case PowerModes.Suspend:
                            Log.Info("System Suspending");
                            BeginCaptureApplicationsOnCurrentDisplays();
                            break;

                        case PowerModes.Resume:
                            Log.Info("System Resuming");
                            BeginRestoreApplicationsOnCurrentDisplays();
                            break;
                    }
                };

        }


        private void InternalRun()
        {
            while(true)
            {
                Thread.Sleep(1000);
                CaptureApplicationsOnCurrentDisplays();
            }
        }

        private void BeginCaptureApplicationsOnCurrentDisplays()
        {
            var thread = new Thread(() => CaptureApplicationsOnCurrentDisplays());
            thread.IsBackground = true;
            thread.Name = "PersistentWindowProcessor.BeginCaptureApplicationsOnCurrentDisplays()";
            thread.Start();
        }

        private void CaptureApplicationsOnCurrentDisplays(string displayKey = null, bool initialCapture = false)
        {            
            lock(displayChangeLock)
            {
                if (displayKey == null)
                {
                    DesktopDisplayMetrics metrics = DesktopDisplayMetrics.AcquireMetrics();
                    displayKey = metrics.Key;
                }

                if (!monitorApplications.ContainsKey(displayKey))
                {
                    monitorApplications.Add(displayKey, new SortedDictionary<string, ApplicationDisplayMetrics>());
                }

                List<string> updateLogs = new List<string>();
                List<ApplicationDisplayMetrics> updateApps = new List<ApplicationDisplayMetrics>();
                var appWindows = CaptureWindowsOfInterest();
                foreach (var window in appWindows)
                {
                    ApplicationDisplayMetrics curDisplayMetrics = null;
                    bool updateScreenCoord;
                    if (NeedUpdateWindow(displayKey, window, out curDisplayMetrics, out updateScreenCoord))
                    {
                        updateApps.Add(curDisplayMetrics);
                        updateLogs.Add(string.Format("Captured {0,-8} at [{1,4}x{2,4}] size [{3,4}x{4,4}] V:{5} {6} ",
                            curDisplayMetrics,
                            curDisplayMetrics.WindowPlacement.NormalPosition.Left,
                            curDisplayMetrics.WindowPlacement.NormalPosition.Top,
                            curDisplayMetrics.WindowPlacement.NormalPosition.Width,
                            curDisplayMetrics.WindowPlacement.NormalPosition.Height,
                            window.Visible,
                            window.Title
                            ));
                    }
                }

                if (!initialCapture)
                {
                    if (updateLogs.Count == 0 && pendingAppMoveSum == 0)
                    {
                        return;
                    }

                    ++pendingAppsMoveTimer;
                    pendingAppMoveSum += updateLogs.Count;

                    if (pendingAppsMoveTimer < 3)
                    {
                        // wait up to 3 seconds before commit update
                        return;
                    }
                }

                if (!initialCapture && pendingAppMoveSum > MaxAppsMoveUpdate * pendingAppsMoveTimer)
                {
                    // this is an undesirable status which could be caused by either of the following,
                    // 1. a new rdp session attemp to move/resize ALL windows even for the same display settings.
                    // 2. window pos/size recovery is incomplete due to lack of admin permission of this program.
                    // 3. moving many windows using mouse too fast.

                    // The remedy for issue 1
                    // wait up to 60 seconds to give DisplaySettingsChanged event handler a chance to recover.
                    if (pendingAppsMoveTimer < MaxAppsMoveDelay)
                    {
                        Log.Trace("Waiting for display setting recovery");
                        return;
                    }

                    // the remedy for issue 2 and 3
                    // acknowledge the status quo and proceed to recapture all windows
                    initialCapture = true;
                    Log.Trace("Full capture timer triggered");
                }

                if (pendingAppsMoveTimer != 0)
                {
                    Log.Trace("pending update timer value is {0}", pendingAppsMoveTimer);
                }

                int maxUpdateCnt = updateLogs.Count;
                if (!initialCapture && maxUpdateCnt > MaxAppsMoveUpdate)
                {
                    maxUpdateCnt = MaxAppsMoveUpdate;
                }

                if (maxUpdateCnt > 0)
                {
                    Log.Trace("{0}Capturing windows for display setting {1}", initialCapture ? "Initial " : "", displayKey);

                    List<string> commitUpdateLog = new List<string>();
                    for (int i = 0; i < maxUpdateCnt; i++)
                    {
                        ApplicationDisplayMetrics curDisplayMetrics = updateApps[i];
                        commitUpdateLog.Add(updateLogs[i]);
                        if (!monitorApplications[displayKey].ContainsKey(curDisplayMetrics.Key))
                        {
                            monitorApplications[displayKey].Add(curDisplayMetrics.Key, curDisplayMetrics);
                        }
                        else
                        {
                            monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement = curDisplayMetrics.WindowPlacement;
                            monitorApplications[displayKey][curDisplayMetrics.Key].ScreenPosition = curDisplayMetrics.ScreenPosition;
                        }
                    }

                    commitUpdateLog.Sort();
                    Log.Trace("{0}{1}{2} windows captured", string.Join(Environment.NewLine, commitUpdateLog), Environment.NewLine, commitUpdateLog.Count);
                }
                pendingAppsMoveTimer = 0;
                pendingAppMoveSum = 0;
            }
        }

        private IEnumerable<SystemWindow> CaptureWindowsOfInterest()
        {
            return SystemWindow.AllToplevelWindows
                                .Where(row => row.Parent.HWnd.ToInt64() == 0
                                    && !string.IsNullOrEmpty(row.Title)
                                    //&& !row.Title.Equals("Program Manager")
                                    //&& !row.Title.Contains("Task Manager")
                                    && row.Visible
                                    );
        }

        private bool NeedUpdateWindow(string displayKey, SystemWindow window, out ApplicationDisplayMetrics curDisplayMetric, out bool updateScreenCoord)
        {
            WindowPlacement windowPlacement = new WindowPlacement();
            User32.GetWindowPlacement(window.HWnd, ref windowPlacement);

            // compensate for GetWindowPlacement() failure to get real coordinate of snapped window
            RECT screenPosition = new RECT();
            User32.GetWindowRect(window.HWnd, ref screenPosition);

            uint processId = 0;
            uint threadId = User32.GetWindowThreadProcessId(window.HWnd, out processId);

            curDisplayMetric = new ApplicationDisplayMetrics
            {
                HWnd = window.HWnd,
#if DEBUG
                // these function calls are very cpu-intensive
                ApplicationName = window.Process.ProcessName,
#else
                ApplicationName = "",
#endif
                ProcessId = processId,

                WindowPlacement = windowPlacement,
                ScreenPosition = screenPosition
            };

            bool needUpdate = false;
            updateScreenCoord = false;
            if (!monitorApplications[displayKey].ContainsKey(curDisplayMetric.Key))
            {
                needUpdate = true;
            }
            else
            {
                ApplicationDisplayMetrics prevDisplayMetrics = monitorApplications[displayKey][curDisplayMetric.Key];
                if (prevDisplayMetrics.ProcessId != curDisplayMetric.ProcessId)
                {
                    // key collision between dead window and new window with the same hwnd
                    monitorApplications[displayKey].Remove(curDisplayMetric.Key);
                    needUpdate = true;
                }
                else
                {
                    if (!prevDisplayMetrics.ScreenPosition.Equals(curDisplayMetric.ScreenPosition))
                    {
                        updateScreenCoord = true;
                        needUpdate = true;
                    }
                    if (!prevDisplayMetrics.EqualPlacement(curDisplayMetric))
                    {
                        needUpdate = true;
                    }
                }
            }

            return needUpdate;
        }

        private void BeginRestoreApplicationsOnCurrentDisplays()
        {
            var thread = new Thread(() => 
            {
                try
                {
                    RestoreApplicationsOnCurrentDisplays();
                    CaptureApplicationsOnCurrentDisplays(initialCapture: true);
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
            });
            thread.IsBackground = true;
            thread.Name = "PersistentWindowProcessor.RestoreApplicationsOnCurrentDisplays()";
            thread.Start();
        }

        private void RestoreApplicationsOnCurrentDisplays(string displayKey = null)
        {
            lock (displayChangeLock)
            {
                if (displayKey == null)
                {
                    DesktopDisplayMetrics metrics = DesktopDisplayMetrics.AcquireMetrics();
                    displayKey = metrics.Key;
                }

                if (!monitorApplications.ContainsKey(displayKey)
                    || monitorApplications[displayKey].Count == 0)
                {
                    // the display setting has not been captured yet
                    Log.Trace("Unknown display setting {0}", displayKey);
                    return;
                }

                Log.Info("Restoring applications for {0}", displayKey);
                foreach (var window in CaptureWindowsOfInterest())
                {
                    var proc_name = window.Process.ProcessName;
                    if (proc_name.Contains("CodeSetup"))
                    {
                        // prevent hang in SetWindowPlacement()
                        continue;
                    }

#if DEBUG
                    string applicationKey = string.Format("{0}-{1}", window.HWnd.ToInt64(), window.Process.ProcessName);
#else
                    string applicationKey = string.Format("{0}-{1}", window.HWnd.ToInt64(), "");
#endif            
                    if (monitorApplications[displayKey].ContainsKey(applicationKey))
                    {
                        ApplicationDisplayMetrics prevDisplayMetrics = monitorApplications[displayKey][applicationKey];
                        // looks like the window is still here for us to restore
                        WindowPlacement windowPlacement = prevDisplayMetrics.WindowPlacement;
                        IntPtr hwnd = prevDisplayMetrics.HWnd;
                        /*
                        if (!User32.IsWindow(hwnd))
                        {
                            monitorApplications[displayKey].Remove(applicationKey);
                            continue;
                        }
                        */

                        ApplicationDisplayMetrics curDisplayMetrics = null;
                        bool updateScreenCoord;
                        if (!NeedUpdateWindow(displayKey, window, out curDisplayMetrics, out updateScreenCoord))
                        {
                            // window position has no change
                            continue;
                        }

                        /*
                        if (windowPlacement.ShowCmd == ShowWindowCommands.Maximize)
                        {
                            // When restoring maximized windows, it occasionally switches res and when the maximized setting is restored
                            // the window thinks it's maxxed, but does not eat all the real estate. So we'll temporarily unmaximize then
                            // re-apply that
                            windowPlacement.ShowCmd = ShowWindowCommands.Normal;
                            User32.SetWindowPlacement(hwnd, ref windowPlacement);
                            windowPlacement.ShowCmd = ShowWindowCommands.Maximize;
                        }
                        */

                        bool success;
                        success = User32.SetWindowPlacement(hwnd, ref windowPlacement);
                        Log.Info("SetWindowPlacement({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                            window.Process.ProcessName,
                            windowPlacement.NormalPosition.Left,
                            windowPlacement.NormalPosition.Top,
                            windowPlacement.NormalPosition.Width,
                            windowPlacement.NormalPosition.Height,
                            success);

                        if (updateScreenCoord)
                        {
                            if (NeedUpdateWindow(displayKey, window, out curDisplayMetrics, out updateScreenCoord))
                            {
                                RECT rect = prevDisplayMetrics.ScreenPosition;
                                success |= User32.MoveWindow(hwnd, rect.Left, rect.Top, rect.Width, rect.Height, true);
                                Log.Info("MoveWindow({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                                    window.Process.ProcessName,
                                    rect.Left,
                                    rect.Top,
                                    rect.Width,
                                    rect.Height,
                                    success);
                            }
                        }

                        if (!success)
                        {
                            string error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                            Log.Error(error);
                        }
                    }
                }
                Log.Trace("Restored windows position for display setting {0}", displayKey);
            }
        }

        public void Dispose()
        {
            if (windowProcHook != null)
            {
                windowProcHook.Dispose();
            }
        }
    }
}
