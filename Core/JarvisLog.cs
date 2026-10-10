using System;

namespace Gem.Core;

/// <summary>
/// Unified thread-safe logger for JARVIS providing audited timestamped diagnostic output:
/// Format: [{DateTime.Now:HH:mm:ss.fff}] [{serviceName}] {message}
/// </summary>
public static class JarvisLog
{
    private static readonly object _consoleLock = new();

    public static void Info(string serviceName, string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{serviceName}] {message}");
            Console.ResetColor();
        }
    }

    public static void Debug(string serviceName, string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{serviceName}] {message}");
            Console.ResetColor();
        }
    }

    public static void Warn(string serviceName, string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{serviceName} Warning] {message}");
            Console.ResetColor();
        }
    }

    public static void Error(string serviceName, string message, Exception? ex = null)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            if (ex != null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{serviceName} Error] {message}: {ex.Message}");
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{serviceName} Error] {message}");
            }
            Console.ResetColor();
        }
    }

    public static void Route(string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Router] {message}");
            Console.ResetColor();
        }
    }

    public static void Route(string subTag, string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Router: {subTag}] {message}");
            Console.ResetColor();
        }
    }

    public static void Fsm(string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [FSM] {message}");
            Console.ResetColor();
        }
    }
}
