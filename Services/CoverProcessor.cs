using MusicFileParser.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicFileParser.Services;

public sealed class CoverProcessor
{
    private const string CoverFileName = "cover.jpg";

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".m4a"
    };

    public ProcessingReport Process(string folderPath, IProgress<string>? progress = null)
    {
        void Log(string message) => progress?.Report(message);

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            Log("Папка не найдена.");
            return EmptyReport();
        }

        Log($"Папка: {folderPath}");

        IReadOnlyList<string> audioFiles = FindAudioFiles(folderPath);
        Log($"Аудиофайлов (MP3, FLAC, M4A): {audioFiles.Count}.");
        if (audioFiles.Count == 0)
        {
            Log("Обработка остановлена: в папке нет подходящих файлов.");
            return EmptyReport();
        }

        string? existingCoverPath = FindCoverFile(folderPath);
        byte[] jpeg;
        CoverSource source;
        string? sourceFileName;
        var errors = new List<string>();

        if (existingCoverPath is not null)
        {
            source = CoverSource.CoverFile;
            sourceFileName = CoverFileName;
            Log("Источник обложки: файл cover.jpg.");
            try
            {
                jpeg = ImageResizer.ToSquareJpeg(File.ReadAllBytes(existingCoverPath));
            }
            catch (Exception ex)
            {
                Log($"Не удалось прочитать cover.jpg: {ex.Message}");
                Log("Аудиофайлы не изменены.");
                return Report(source, sourceFileName, audioFiles.Count, 0, 1, [$"cover.jpg: {ex.Message}"]);
            }
        }
        else
        {
            Log("Файл cover.jpg не найден. Ищу встроенную обложку.");
            byte[]? embedded = TryExtractFirstCover(audioFiles, Log, out string? trackName, out string? extractError);
            if (embedded is null)
            {
                Log("Встроенная обложка не найдена. Аудиофайлы не изменены.");
                return Report(
                    CoverSource.None,
                    null,
                    audioFiles.Count,
                    0,
                    extractError is null ? 0 : 1,
                    extractError is null ? [] : [extractError]);
            }

            source = CoverSource.EmbeddedTrack;
            sourceFileName = trackName;
            Log($"Источник обложки: трек {trackName}.");
            try
            {
                jpeg = ImageResizer.ToSquareJpeg(embedded);
            }
            catch (Exception ex)
            {
                Log($"Не удалось обработать обложку из {trackName}: {ex.Message}");
                Log("Аудиофайлы не изменены.");
                return Report(source, sourceFileName, audioFiles.Count, 0, 1, [$"{trackName}: {ex.Message}"]);
            }
        }

        Log("Обложка подготовлена: 500×500, JPEG.");

        string coverPath = existingCoverPath ?? Path.Combine(folderPath, CoverFileName);
        try
        {
            SaveCoverFile(coverPath, jpeg);
            Log(source == CoverSource.CoverFile
                ? "Файл cover.jpg обновлён до 500×500, JPEG."
                : "Файл cover.jpg сохранён в папку альбома.");
        }
        catch (Exception ex)
        {
            errors.Add($"cover.jpg: {ex.Message}");
            Log($"Не удалось записать cover.jpg: {ex.Message}");
        }

        int processed = 0;
        foreach (string audioPath in audioFiles)
        {
            string name = Path.GetFileName(audioPath);
            try
            {
                EmbedCover(audioPath, jpeg);
                processed++;
                Log($"Записано: {name}");
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
                Log($"Ошибка: {name}: {ex.Message}");
            }
        }

        Log($"Готово. Обработано: {processed}, ошибок: {errors.Count}.");
        return Report(source, sourceFileName, audioFiles.Count, processed, errors.Count, errors);
    }

    private static byte[]? TryExtractFirstCover(
        IReadOnlyList<string> audioFiles,
        Action<string> log,
        out string? trackName,
        out string? error)
    {
        trackName = null;
        error = null;
        var readErrors = new List<string>();

        foreach (string audioPath in audioFiles)
        {
            string name = Path.GetFileName(audioPath);
            try
            {
                byte[]? picture = ReadFrontCover(audioPath);
                if (picture is null)
                    continue;

                trackName = name;
                return picture;
            }
            catch (Exception ex)
            {
                readErrors.Add($"{name}: {ex.Message}");
                log($"Не удалось прочитать {name}: {ex.Message}");
            }
        }

        if (readErrors.Count > 0)
            error = string.Join("; ", readErrors);

        return null;
    }

    private static byte[]? ReadFrontCover(string audioPath)
    {
        using TagLib.File file = TagLib.File.Create(audioPath);
        TagLib.IPicture? picture = file.Tag.Pictures
            .FirstOrDefault(item => item.Type == TagLib.PictureType.FrontCover)
            ?? file.Tag.Pictures.FirstOrDefault();

        if (picture?.Data is not { Count: > 0 } data)
            return null;

        return data.Data;
    }

    private static void EmbedCover(string audioPath, byte[] jpeg)
    {
        using TagLib.File file = TagLib.File.Create(audioPath);
        file.Tag.Pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(jpeg))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "Cover"
            }
        ];
        file.Save();
    }

    private static void SaveCoverFile(string path, byte[] jpeg)
    {
        string tempPath = path + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, jpeg);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static IReadOnlyList<string> FindAudioFiles(string folderPath)
    {
        return Directory.EnumerateFiles(folderPath)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindCoverFile(string folderPath)
    {
        return Directory.EnumerateFiles(folderPath)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                CoverFileName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static ProcessingReport EmptyReport() =>
        Report(CoverSource.None, null, 0, 0, 0, []);

    private static ProcessingReport Report(
        CoverSource source,
        string? sourceFileName,
        int audioFileCount,
        int processedCount,
        int failedCount,
        IReadOnlyList<string> errors) =>
        new()
        {
            Source = source,
            SourceFileName = sourceFileName,
            AudioFileCount = audioFileCount,
            ProcessedCount = processedCount,
            FailedCount = failedCount,
            Errors = errors
        };
}
