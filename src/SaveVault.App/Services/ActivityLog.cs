using System.IO;

namespace SaveVault.App.Services;

public sealed class ActivityLog(string directory)
{
    public string DirectoryPath => directory;

    public void Write(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.UtcNow:O} {message}{Environment.NewLine}";
            if (exception is not null) line += exception + Environment.NewLine;
            File.AppendAllText(Path.Combine(directory, $"savevault-{DateTime.UtcNow:yyyy-MM-dd}.log"), line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unavailable log folder must not prevent a backup or obscure its original error.
            System.Diagnostics.Debug.WriteLine(error);
        }
    }
}
