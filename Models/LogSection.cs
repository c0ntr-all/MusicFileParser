using System.Collections.ObjectModel;

namespace MusicFileParser.Models;

public enum LogSectionKind
{
    Notes,
    Folder,
    Summary
}

public sealed class LogSection
{
    public LogSection(string title, LogSectionKind kind)
    {
        Title = title;
        Kind = kind;
    }

    public string Title { get; }

    public LogSectionKind Kind { get; }

    public string Glyph => Kind switch
    {
        LogSectionKind.Folder => "\uE8B7",
        LogSectionKind.Summary => "\uE73E",
        _ => "\uE946"
    };

    public ObservableCollection<string> Lines { get; } = [];
}
