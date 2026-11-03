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

    public ProcessingReport Process(string folderPath, IProgress<LogEvent>? progress = null)
    {
        void Report(LogEvent entry) => progress?.Report(entry);
        void Info(string message) => Report(new LogEvent(LogEventKind.Info, message));
        void Summary(string message) => Report(new LogEvent(LogEventKind.Summary, message));

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            Info("Папка не найдена.");
            return EmptyReport();
        }

        string root = Path.GetFullPath(folderPath);
        Info($"Корень: {root}");

        var folders = new List<string>();
        CollectFolders(root, folders, Info, new HashSet<string>(StringComparer.OrdinalIgnoreCase), isRoot: true);

        var reports = new List<ProcessingReport>();
        foreach (string folder in folders)
        {
            ProcessingReport? report = ProcessFolder(root, folder, Report);
            if (report is not null)
                reports.Add(report);
        }

        if (reports.Count == 0)
        {
            Info("В выбранной папке и подпапках нет файлов MP3, FLAC или M4A.");
            return EmptyReport();
        }

        if (reports.Count == 1)
            return reports[0];

        int processed = reports.Sum(report => report.ProcessedCount);
        int failed = reports.Sum(report => report.FailedCount);
        Summary($"Папок: {reports.Count}, файлов записано: {processed}, ошибок: {failed}.");
        return new ProcessingReport
        {
            Source = CoverSource.None,
            AudioFileCount = reports.Sum(report => report.AudioFileCount),
            ProcessedCount = processed,
            FailedCount = failed,
            Errors = reports.SelectMany(report => report.Errors).ToList()
        };
    }

    private static void CollectFolders(
        string folderPath,
        List<string> folders,
        Action<string> log,
        HashSet<string> visited,
        bool isRoot)
    {
        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(folderPath);
            if (!isRoot && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                log($"Пропущена ссылка: {info.FullName}");
                return;
            }
        }
        catch (Exception ex)
        {
            log($"Не удалось открыть папку {folderPath}: {ex.Message}");
            return;
        }

        if (!visited.Add(info.FullName))
            return;

        folders.Add(info.FullName);

        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(info.FullName)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log($"Не удалось просмотреть {info.FullName}: {ex.Message}");
            return;
        }

        foreach (string child in children)
            CollectFolders(child, folders, log, visited, isRoot: false);
    }

    private static ProcessingReport? ProcessFolder(string root, string folderPath, Action<LogEvent> log)
    {
        void Line(string message) => log(new LogEvent(LogEventKind.Line, message));

        string display = DisplayPath(root, folderPath);
        IReadOnlyList<string> audioFiles;
        try
        {
            audioFiles = FindAudioFiles(folderPath);
        }
        catch (Exception ex)
        {
            log(new LogEvent(LogEventKind.Folder, display));
            Line($"Не удалось прочитать папку: {ex.Message}");
            return Report(CoverSource.None, null, 0, 0, 1, [$"{display}: {ex.Message}"]);
        }

        if (audioFiles.Count == 0)
            return null;

        log(new LogEvent(LogEventKind.Folder, display));
        Line($"Аудиофайлов (MP3, FLAC, M4A): {audioFiles.Count}.");

        var errors = new List<string>();
        string? coverFilePath = FindImageFile(folderPath, CoverFileName);
        if (coverFilePath is null && FindImageFile(folderPath, "folder.jpg") is string folderFilePath)
        {
            string renamedPath = Path.Combine(folderPath, CoverFileName);
            try
            {
                File.Move(folderFilePath, renamedPath);
                coverFilePath = renamedPath;
                Line($"Файл {Path.GetFileName(folderFilePath)} переименован в cover.jpg.");
            }
            catch (Exception ex)
            {
                errors.Add($"{display}: {Path.GetFileName(folderFilePath)}: {ex.Message}");
                Line($"Не удалось переименовать {Path.GetFileName(folderFilePath)} в cover.jpg: {ex.Message}");
                coverFilePath = folderFilePath;
            }
        }

        PreparedCover cover;
        CoverSource source;
        string? sourceFileName;

        if (coverFilePath is not null)
        {
            string imagePath = coverFilePath;
            string imageName = Path.GetFileName(imagePath);
            source = CoverSource.CoverFile;
            sourceFileName = imageName;
            Line($"Источник обложки: файл {imageName}.");
            try
            {
                cover = ImageResizer.Prepare(File.ReadAllBytes(imagePath), "image/jpeg");
            }
            catch (Exception ex)
            {
                Line($"Не удалось прочитать {imageName}: {ex.Message}");
                Line("Аудиофайлы не изменены.");
                return Report(source, sourceFileName, audioFiles.Count, 0, 1, [$"{display}: {imageName}: {ex.Message}"]);
            }

            Line(Describe(cover));
            if (ExceedsTarget(cover))
            {
                Line($"Файл {imageName} оставлен без изменений. В треки вшивается копия {cover.Width}×{cover.Height}.");
            }
            else if (IsSmallerThanTarget(cover))
            {
                Line("Файл меньше 500×500. Ищу в треках более крупную обложку.");
                if (TryFindLargerCover(audioFiles, cover, Line) is SizedPicture larger)
                {
                    if (TryAdoptLargerCover(folderPath, larger, Line, errors, display) is PreparedCover adopted)
                    {
                        cover = adopted;
                        source = CoverSource.EmbeddedTrack;
                        sourceFileName = larger.TrackName;
                    }
                }
                else
                {
                    Line($"В треках нет обложки крупнее. Вшивается {imageName}.");
                }
            }
            else
            {
                Line($"Файл {imageName} уже 500×500 и оставлен без изменений.");
            }
        }
        else
        {
            Line("Файлы cover.jpg и Folder.jpg не найдены. Ищу встроенную обложку.");
            EmbeddedPicture? embedded = TryExtractFirstCover(audioFiles, Line, out string? trackName, out string? extractError);
            if (embedded is null)
            {
                Line("Встроенная обложка не найдена. Аудиофайлы не изменены.");
                return Report(
                    CoverSource.None,
                    null,
                    audioFiles.Count,
                    0,
                    extractError is null ? 0 : 1,
                    extractError is null ? [] : [$"{display}: {extractError}"]);
            }

            source = CoverSource.EmbeddedTrack;
            sourceFileName = trackName;
            Line($"Источник обложки: трек {trackName}.");
            try
            {
                cover = ImageResizer.Prepare(embedded.Value.Data, embedded.Value.MimeType);
            }
            catch (Exception ex)
            {
                Line($"Не удалось обработать обложку из {trackName}: {ex.Message}");
                Line("Аудиофайлы не изменены.");
                return Report(source, sourceFileName, audioFiles.Count, 0, 1, [$"{display}: {trackName}: {ex.Message}"]);
            }

            Line(Describe(cover));
            try
            {
                SaveCoverFile(Path.Combine(folderPath, CoverFileName), CoverFileBytes(embedded.Value.Data, embedded.Value.MimeType));
                Line(ExceedsTarget(cover)
                    ? $"Файл cover.jpg сохранён в исходном размере {cover.SourceWidth}×{cover.SourceHeight}. В треки вшивается копия {cover.Width}×{cover.Height}."
                    : $"Файл cover.jpg сохранён: {cover.Width}×{cover.Height}.");
            }
            catch (Exception ex)
            {
                errors.Add($"{display}: cover.jpg: {ex.Message}");
                Line($"Не удалось записать cover.jpg: {ex.Message}");
            }
        }

        int processed = 0;
        foreach (string audioPath in audioFiles)
        {
            string name = Path.GetFileName(audioPath);
            try
            {
                EmbedCover(audioPath, cover.Data, cover.MimeType);
                processed++;
                Line($"Записано: {name}");
            }
            catch (Exception ex)
            {
                errors.Add($"{display}: {name}: {ex.Message}");
                Line($"Ошибка: {name}: {ex.Message}");
            }
        }

        Line($"Готово. Обработано: {processed}, ошибок: {errors.Count}.");
        return Report(source, sourceFileName, audioFiles.Count, processed, errors.Count, errors);
    }

    private static string Describe(PreparedCover cover) =>
        ExceedsTarget(cover)
            ? $"Обложка {cover.SourceWidth}×{cover.SourceHeight} больше 500×500. Пропорции сохранены: {cover.Width}×{cover.Height}."
            : $"Обложка {cover.SourceWidth}×{cover.SourceHeight} не больше 500×500, размер не меняется.";

    private static bool ExceedsTarget(PreparedCover cover) =>
        cover.SourceWidth > ImageResizer.TargetSize || cover.SourceHeight > ImageResizer.TargetSize;

    private static bool IsSmallerThanTarget(PreparedCover cover) =>
        !ExceedsTarget(cover)
        && (cover.SourceWidth < ImageResizer.TargetSize || cover.SourceHeight < ImageResizer.TargetSize);

    private readonly record struct SizedPicture(byte[] Data, string MimeType, int Width, int Height, string TrackName)
    {
        public long Area => (long)Width * Height;
    }

    private static SizedPicture? TryFindLargerCover(
        IReadOnlyList<string> audioFiles,
        PreparedCover current,
        Action<string> log)
    {
        long currentArea = (long)current.SourceWidth * current.SourceHeight;
        SizedPicture? best = null;

        foreach (string audioPath in audioFiles)
        {
            string name = Path.GetFileName(audioPath);
            try
            {
                EmbeddedPicture? picture = ReadFrontCover(audioPath);
                if (picture is null)
                    continue;

                (int width, int height) = ImageResizer.Measure(picture.Value.Data);
                long area = (long)width * height;
                if (area <= currentArea)
                    continue;

                if (best is null || area > best.Value.Area)
                    best = new SizedPicture(picture.Value.Data, picture.Value.MimeType, width, height, name);
            }
            catch (Exception ex)
            {
                log($"Не удалось прочитать {name}: {ex.Message}");
            }
        }

        return best;
    }

    private static PreparedCover? TryAdoptLargerCover(
        string folderPath,
        SizedPicture picture,
        Action<string> log,
        List<string> errors,
        string display)
    {
        log($"Найдена более крупная обложка в {picture.TrackName}: {picture.Width}×{picture.Height}.");
        PreparedCover prepared;
        try
        {
            prepared = ImageResizer.Prepare(picture.Data, picture.MimeType);
        }
        catch (Exception ex)
        {
            log($"Не удалось обработать обложку из {picture.TrackName}: {ex.Message}");
            log("Остаётся обложка из файла папки.");
            return null;
        }

        try
        {
            SaveCoverFile(Path.Combine(folderPath, CoverFileName), CoverFileBytes(picture.Data, picture.MimeType));
            log(ExceedsTarget(prepared)
                ? $"Файл cover.jpg сохранён в исходном размере {prepared.SourceWidth}×{prepared.SourceHeight}. В треки вшивается копия {prepared.Width}×{prepared.Height}."
                : $"Файл cover.jpg сохранён: {prepared.Width}×{prepared.Height}.");
        }
        catch (Exception ex)
        {
            errors.Add($"{display}: cover.jpg: {ex.Message}");
            log($"Не удалось записать cover.jpg: {ex.Message}");
        }

        return prepared;
    }

    private static byte[] CoverFileBytes(byte[] data, string mimeType)
    {
        if (IsJpeg(data, mimeType))
            return data;

        return ImageResizer.ToJpeg(data);
    }

    private static bool IsJpeg(byte[] data, string mimeType)
    {
        if (mimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
            || mimeType.Contains("jpg", StringComparison.OrdinalIgnoreCase))
            return true;

        return data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
    }

    private readonly record struct EmbeddedPicture(byte[] Data, string MimeType);

    private static EmbeddedPicture? TryExtractFirstCover(
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
                EmbeddedPicture? picture = ReadFrontCover(audioPath);
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

    private static EmbeddedPicture? ReadFrontCover(string audioPath)
    {
        using TagLib.File file = TagLib.File.Create(audioPath);
        TagLib.IPicture? picture = file.Tag.Pictures
            .FirstOrDefault(item => item.Type == TagLib.PictureType.FrontCover)
            ?? file.Tag.Pictures.FirstOrDefault();

        if (picture?.Data is not { Count: > 0 } data)
            return null;

        string mime = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType;
        return new EmbeddedPicture(data.Data, mime);
    }

    private static void EmbedCover(string audioPath, byte[] data, string mimeType)
    {
        using TagLib.File file = TagLib.File.Create(audioPath);
        file.Tag.Pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(data))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = mimeType,
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

    private static string DisplayPath(string root, string folderPath)
    {
        string relative = Path.GetRelativePath(root, folderPath);
        return relative == "." ? folderPath : relative;
    }

    private static IReadOnlyList<string> FindAudioFiles(string folderPath)
    {
        return Directory.EnumerateFiles(folderPath)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindImageFile(string folderPath, string fileName)
    {
        return Directory.EnumerateFiles(folderPath)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                fileName,
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
