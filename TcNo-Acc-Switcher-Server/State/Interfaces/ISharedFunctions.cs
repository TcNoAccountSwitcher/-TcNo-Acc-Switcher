﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace TcNo_Acc_Switcher_Server.State.Interfaces;

public interface ISharedFunctions
{
    /// <summary>
    /// Runs initAccListSortable - Final init needed for account switcher to work.
    /// </summary>
    Task FinaliseAccountList(IJSRuntime jsRuntime);

    void RunShortcut(string s, string shortcutFolder, string platform = "", bool admin = false);
    bool CanKillProcess(List<string> procNames, string closingMethod = "Combined", bool showModal = true);
    bool CanKillProcess(string processName, string closingMethod = "Combined", bool showModal = true);
    Task<bool> CloseProcesses(IJSRuntime jsRuntime, string procName, string closingMethod);
    Task<bool> CloseProcesses(IJSRuntime jsRuntime, List<string> procNames, string closingMethod);

    /// <summary>
    /// Waits for a program to close, and returns true if not running anymore.
    /// </summary>
    /// <param name="procName">Name of process to lookup</param>
    /// <returns>Whether it was closed before this function returns or not.</returns>
    Task<bool> WaitForClose(IJSRuntime jsRuntime, string procName);

    Task<bool> WaitForClose(IJSRuntime jsRuntime, List<string> procNames, string closingMethod);
}