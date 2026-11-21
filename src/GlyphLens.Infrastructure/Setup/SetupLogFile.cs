using System.Globalization;
using System.Text;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.Infrastructure.Setup;

/// <summary>
/// The setup's technical log (<c>&lt;runtime&gt;\setup.log</c>): the commands that were run and the installers' own output, with
/// timestamps. It never receives screen content, recognised text or translations (the setup has none). Lines are cleaned of control
/// characters and shortened; the file is capped, and a log from earlier runs larger than 1 MB is moved to <c>setup.previous.log</c>.
/// Writing never throws: a log that cannot be written is simply missing.
/// </summary>
public sealed class SetupLogFile : ISetupLog
{
    private const int MaxLineLength = 600;
    private const long RotateAboveBytes = 1024 * 1024;
    private const long MaxBytesPerSession = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private long _written;
    private bool _started;

    public SetupLogFile(string filePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string? FilePath { get; }

    public void Write(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            try
            {
                if (!_started)
                {
                    Start();
                }

                if (_written > MaxBytesPerSession)
                {
                    return;
                }

                string clean = Clean(line);
                string entry = string.Create(CultureInfo.InvariantCulture, $"{_time.GetUtcNow():yyyy-MM-dd HH:mm:ss}Z {clean}{Environment.NewLine}");
                if (_written + entry.Length > MaxBytesPerSession)
                {
                    entry = "(log capped)" + Environment.NewLine;
                }

                File.AppendAllText(FilePath!, entry, Encoding.UTF8);
                _written += entry.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is a diagnostic convenience; the setup carries on without it.
            }
        }
    }

    private void Start()
    {
        _started = true;
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var info = new FileInfo(FilePath!);
        if (info.Exists && info.Length > RotateAboveBytes)
        {
            File.Move(FilePath!, Path.Combine(info.DirectoryName!, "setup.previous.log"), overwrite: true);
        }
    }

    private static string Clean(string line)
    {
        var builder = new StringBuilder(Math.Min(line.Length, MaxLineLength));
        foreach (char c in line)
        {
            if (builder.Length >= MaxLineLength)
            {
                builder.Append('…');
                break;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }
}
