using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SaveVault.Core.Games;

namespace SaveVault.App.Services;

internal static class WindowsGameEnvironment
{
    internal static DiscoveryEnvironment Create()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tokens = new Dictionary<string, string>
        {
            ["home"] = home,
            ["winDocuments"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["winPublicDocuments"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            ["winAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["winLocalAppData"] = local,
            ["winLocalAppDataLow"] = KnownFolder(new("A520A1A4-1780-4FF6-BD18-167343C5AF16"), Path.Combine(home, "AppData", "LocalLow")),
            ["winSavedGames"] = KnownFolder(new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"), Path.Combine(home, "Saved Games")),
            ["winPublic"] = Environment.GetEnvironmentVariable("PUBLIC") ?? Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)) ?? "",
            ["winProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["winDir"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };
        var steam = new List<string>();
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (key?.GetValue("SteamPath") is string root) steam.Add(root);
        steam.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var search = new[] { tokens["winDocuments"], tokens["winSavedGames"], tokens["winAppData"], local,
            tokens["winLocalAppDataLow"], Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), tokens["winProgramData"] };
        return new(tokens.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(),
            search.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            steam.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string KnownFolder(Guid id, string fallback)
    {
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path);
        if (result != 0) return fallback;
        try { return Marshal.PtrToStringUni(path) ?? fallback; }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
