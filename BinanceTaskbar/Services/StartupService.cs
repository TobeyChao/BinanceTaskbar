using Microsoft.Win32;

namespace BinanceTaskbar.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BinanceTaskbar";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{executable}\" --startup");
                return key.GetValue(ValueName) is string value && value.Length > 0;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return key.GetValue(ValueName) is null;
        }
        catch
        {
            return false;
        }
    }
}

