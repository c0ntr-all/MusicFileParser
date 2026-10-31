namespace MusicFileParser.Models;

public enum LogEventKind
{
    Info,
    Folder,
    Line,
    Summary
}

public readonly record struct LogEvent(LogEventKind Kind, string Text);
