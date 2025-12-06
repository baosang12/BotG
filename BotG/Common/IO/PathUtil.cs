using System;
using System.IO;

namespace BotG.Common.IO;

/// <summary>
/// Utility for resolving log file paths with environment variable support.
/// </summary>
public static class PathUtil
{
    /// <summary>
    /// Gets the log root directory from BOTG_LOG_PATH environment variable,
    /// or falls back to D:\botg\logs.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string GetLogRoot()
    {
        var root = Environment.GetEnvironmentVariable("BOTG_LOG_PATH")
                   ?? @"D:\botg\logs";

        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }

        return root;
    }

    /// <summary>
    /// Combines log root with filename to get full path.
    /// </summary>
    public static string GetFile(string name)
    {
        return Path.Combine(GetLogRoot(), name);
    }
}
