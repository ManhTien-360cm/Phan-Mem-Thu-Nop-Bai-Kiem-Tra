using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Storage;

internal static class SubmissionDownloadPathResolver
{
    public static string ResolveExistingFile(
        IStoragePaths paths,
        Guid sessionId,
        string studentCode,
        Guid submissionId,
        string storedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(storedRelativePath)
            || Path.IsPathRooted(storedRelativePath)
            || Path.IsPathFullyQualified(storedRelativePath))
        {
            throw NotFound();
        }

        string storageRoot;
        string submissionRoot;
        string candidate;
        try
        {
            storageRoot = Path.GetFullPath(paths.RootPath);
            submissionRoot = Path.GetFullPath(
                paths.SubmissionRoot(sessionId, studentCode, submissionId));
            candidate = Path.GetFullPath(Path.Combine(storageRoot, storedRelativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            throw NotFound();
        }

        if (!IsStrictDescendant(storageRoot, submissionRoot)
            || !IsStrictDescendant(submissionRoot, candidate)
            || !File.Exists(candidate))
        {
            throw NotFound();
        }

        RejectReparsePoints(storageRoot, candidate);
        return candidate;
    }

    public static void RejectReparsePoints(string storageRoot, string candidate)
    {
        string root;
        string fullCandidate;
        try
        {
            root = Path.GetFullPath(storageRoot);
            fullCandidate = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            throw NotFound();
        }
        if (!IsStrictDescendant(root, fullCandidate))
            throw NotFound();

        RejectIfReparsePoint(root);
        var relative = Path.GetRelativePath(root, fullCandidate);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectIfReparsePoint(current);
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            return false;

        var rootWithBoundary = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootWithBoundary, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectIfReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            throw NotFound();
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw NotFound();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw NotFound();
        }
    }

    private static ApiException NotFound() =>
        new(
            ErrorCodes.NotFound,
            "Không tìm thấy file bài nộp hợp lệ.",
            404);
}
