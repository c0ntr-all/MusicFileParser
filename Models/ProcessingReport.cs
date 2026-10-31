using System.Collections.Generic;

namespace MusicFileParser.Models;

public enum CoverSource
{
    None,
    CoverFile,
    EmbeddedTrack
}

public sealed class ProcessingReport
{
    public required CoverSource Source { get; init; }

    public string? SourceFileName { get; init; }

    public int AudioFileCount { get; init; }

    public int ProcessedCount { get; init; }

    public int FailedCount { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}
