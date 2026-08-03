using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

internal sealed class SubmissionBatchDownloader(IBackendClient api)
{
    private const int MaximumFileNameLength = 120;
    private const int MaximumStudentPartLength = 48;
    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public async Task<SubmissionDownloadResult> DownloadAsync(
        IReadOnlyList<SubmissionSummaryDto> submissions,
        string destinationFolder,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);

        var successfulFiles = 0;
        var fullySuccessfulSubmissions = 0;
        var completedFileCount = 0;
        var failures = new List<SubmissionDownloadFailure>();

        foreach (var submission in submissions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completedFiles = submission.Files
                .Where(file => file.TransferStatus == TransferStatus.Completed)
                .ToArray();
            if (completedFiles.Length == 0)
                continue;

            completedFileCount += completedFiles.Length;
            var submissionFailureCount = 0;
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var studentFolder = $"{MakeSafePathComponent(submission.StudentCode, "Khong_ma", MaximumStudentPartLength)}_" +
                MakeSafePathComponent(submission.DisplayName, "Khong_ten", MaximumStudentPartLength);
            studentFolder = MakeSafePathComponent(studentFolder, "Khong_ro_hoc_sinh", MaximumFileNameLength);
            var attemptFolder = Path.Combine(
                destinationFolder,
                studentFolder,
                $"Lan_{submission.AttemptNumber}");

            for (var index = 0; index < completedFiles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = completedFiles[index];
                var safeFileName = MakeSafePathComponent(
                    file.Name,
                    $"file_{index + 1}",
                    MaximumFileNameLength);
                safeFileName = MakeUniqueFileName(safeFileName, usedFileNames);
                var destinationPath = Path.Combine(attemptFolder, safeFileName);

                try
                {
                    Directory.CreateDirectory(attemptFolder);
                    await api.DownloadVerifiedFileAsync(
                        $"api/v1/submissions/{submission.Id}/files/{file.Id}/content",
                        destinationPath,
                        file.Sha256,
                        null,
                        cancellationToken);
                    successfulFiles++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    submissionFailureCount++;
                    failures.Add(new(
                        submission.Id,
                        file.Id,
                        $"{submission.StudentCode} - {file.Name}",
                        exception.Message));
                }
            }

            if (submissionFailureCount == 0)
                fullySuccessfulSubmissions++;
        }

        return new(
            fullySuccessfulSubmissions,
            successfulFiles,
            failures.Count,
            completedFileCount == 0,
            failures);
    }

    internal static string MakeSafePathComponent(
        string? value,
        string fallback,
        int maximumLength)
    {
        if (maximumLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        candidate = new(candidate
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
        candidate = candidate.Trim().Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = fallback;

        if (IsWindowsReservedName(candidate))
            candidate = "_" + candidate;

        return ShortenPreservingExtension(candidate, maximumLength);
    }

    private static bool IsWindowsReservedName(string value)
    {
        var baseName = value.Split('.', 2)[0].TrimEnd(' ');
        return WindowsReservedNames.Contains(baseName);
    }

    internal static string MakeUniqueFileName(string fileName, HashSet<string> usedFileNames)
    {
        if (usedFileNames.Add(fileName))
            return fileName;

        var extension = Path.GetExtension(fileName);
        var stem = extension.Length == 0 ? fileName : fileName[..^extension.Length];
        for (var duplicateNumber = 2; ; duplicateNumber++)
        {
            var suffix = $" ({duplicateNumber})";
            var maximumStemLength = Math.Max(1, MaximumFileNameLength - extension.Length - suffix.Length);
            var shortenedStem = stem.Length <= maximumStemLength
                ? stem
                : stem[..maximumStemLength];
            var candidate = shortenedStem + suffix + extension;
            if (usedFileNames.Add(candidate))
                return candidate;
        }
    }

    private static string ShortenPreservingExtension(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;

        var extension = Path.GetExtension(value);
        if (extension.Length == 0 || extension.Length >= maximumLength - 1)
            return value[..maximumLength].TrimEnd('.', ' ');

        var stemLength = maximumLength - extension.Length;
        return value[..stemLength].TrimEnd('.', ' ') + extension;
    }
}

internal sealed record SubmissionDownloadFailure(
    Guid SubmissionId,
    Guid FileId,
    string DisplayName,
    string Error);

internal sealed record SubmissionDownloadResult(
    int FullySuccessfulSubmissionCount,
    int SuccessfulFileCount,
    int FailedFileCount,
    bool HasNoCompletedFiles,
    IReadOnlyList<SubmissionDownloadFailure> Failures);
