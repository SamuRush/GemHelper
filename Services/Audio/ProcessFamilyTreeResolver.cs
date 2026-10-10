using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Gem.Win32;

namespace Gem.Services.Audio;

/// <summary>
/// Traverses Windows process trees via Toolhelp32 snapshots to collect parent and child PIDs
/// belonging to multi-process applications (e.g. Electron apps like Yandex Music).
/// </summary>
public static class ProcessFamilyTreeResolver
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    public static HashSet<uint> GetTargetFamilyPids(string target, Func<string, string, bool> isProcessMatching)
    {
        var familyPids = new HashSet<uint>();
        try
        {
            var parentToChildren = new Dictionary<uint, List<uint>>();
            var rootPids = new List<uint>();

            var snapshot = NativeMethods.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot != IntPtr.Zero && snapshot != (IntPtr)(-1))
            {
                try
                {
                    var entry = new PROCESSENTRY32();
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();

                    if (NativeMethods.Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            uint pid = entry.th32ProcessID;
                            uint parentPid = entry.th32ParentProcessID;
                            string exe = entry.szExeFile;

                            if (!parentToChildren.TryGetValue(parentPid, out var children))
                            {
                                children = new List<uint>();
                                parentToChildren[parentPid] = children;
                            }
                            children.Add(pid);

                            if (isProcessMatching(exe, target))
                            {
                                rootPids.Add(pid);
                            }
                        } while (NativeMethods.Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(snapshot);
                }
            }
            else
            {
                // Fallback: standard Process enumeration
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (isProcessMatching(proc.ProcessName, target))
                        {
                            rootPids.Add((uint)proc.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        JarvisLog.Debug("ProcessFamilyTreeResolver", $"Error examining process for target '{target}': {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }

            // Breadth-First Search to gather all child and grandchild PIDs
            var queue = new Queue<uint>(rootPids);
            while (queue.Count > 0)
            {
                uint currentPid = queue.Dequeue();
                if (familyPids.Add(currentPid))
                {
                    if (parentToChildren.TryGetValue(currentPid, out var childList))
                    {
                        foreach (var childPid in childList)
                        {
                            queue.Enqueue(childPid);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessFamilyTreeResolver] Error collecting family PIDs: {ex.Message}");
        }

        return familyPids;
    }
}
