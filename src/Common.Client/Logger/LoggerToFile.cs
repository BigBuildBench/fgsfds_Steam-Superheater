using CommunityToolkit.Diagnostics;
using System.Diagnostics;

namespace Common.Client.Logger;

public sealed class LoggerToFile : ILogger
{
    private readonly object _lock = new();
    private readonly List<string> _buffer = [];

    public string LogFile { get; init; }

    public LoggerToFile()
    {
        LogFile = Path.Combine(ClientProperties.WorkingFolder, "superheater.log");

        try
        {
            File.Delete(LogFile);

            Info(Environment.OSVersion.ToString());
            Info(ClientProperties.CurrentVersion.ToString());
        }
        catch
        {
            ThrowHelper.ThrowInvalidOperationException("Error while creating log file");
        }
    }

    public void Info(string message)
    {
        Debug.WriteLine(message);
        Log(message, "Info");
    }

    public void Error(string message)
    {
        Debug.WriteLine(message);
        Log(message, "Error");
    }

    /// <summary>
    /// Add message to the log file
    /// </summary>
    /// <param name="message"></param>
    /// <param name="type">Type of log message</param>
    private void Log(string message, string type)
    {
        lock (_lock)
        {
            message = $"[{DateTime.Now:dd.MM.yyyy HH.mm.ss}] [{type}] {message}";
            _buffer.Add(message);

            try
            {
                File.AppendAllLines(LogFile, _buffer);
                _buffer.Clear();
            }
            catch
            {
            }
        }
    }
}
