using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.ViewModels;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class BackendClientTransferTests
{
    [Fact]
    public async Task VerifiedDownload_PreservesSupportedPayloadShapesExactly()
    {
        using var fixture = new TemporaryTransferDirectory();
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("đề thi nhỏ.txt", Encoding.UTF8.GetBytes("Nội dung đề thi")),
            ("đề thi.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\n%%EOF")),
            ("đề thi.docx", CreateZip(("word/document.xml", "<document>Đề thi</document>"))),
            ("gói đề.zip", CreateZip(("README.txt", "ExamTransfer"))),
            ("file rỗng.bin", []),
            ("file lớn.bin", Enumerable.Range(0, 5 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray())
        };

        foreach (var testCase in cases)
        {
            var destination = Path.Combine(fixture.Path, testCase.Name);
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(testCase.Bytes));
            var client = new BackendClient(
                "http://localhost:5048",
                new StaticContentHandler(testCase.Bytes));

            await client.DownloadVerifiedFileAsync("api/file", destination, expectedHash);

            Assert.Equal(testCase.Bytes.Length, new FileInfo(destination).Length);
            Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(destination))));
        }
    }

    [Fact]
    public async Task VerifiedDownload_CreatesUnicodeDestinationAndMatchesSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("\u0110\u1ec1 thi c\u00f3 d\u1ea5u v\u00e0 kho\u1ea3ng tr\u1eafng");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var fixture = new TemporaryTransferDirectory();
        var destination = Path.Combine(
            fixture.Path,
            "ch\u01b0a t\u1ed3n t\u1ea1i",
            "\u0110\u1ec1 thi s\u1ed1 01.txt");
        var client = new BackendClient(
            "http://localhost:5048",
            new StaticContentHandler(bytes));

        await client.DownloadVerifiedFileAsync(
            "api/file",
            destination,
            expectedHash,
            ct: CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".partial"));
    }

    [Fact]
    public async Task GenericDownload_TruncatedResponseNeverCreatesFinalFile()
    {
        using var fixture = new TemporaryTransferDirectory();
        var destination = Path.Combine(fixture.Path, "submission.zip");
        var client = new BackendClient(
            "http://localhost:5048",
            new StaticContentHandler([1, 2, 3], announcedLength: 10));

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            client.DownloadFileAsync(
                "api/file",
                destination,
                ct: CancellationToken.None));

        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(destination + ".partial"));
    }

    [Fact]
    public async Task VerifiedDownload_HashMismatchAndServerErrorNeverCreateFinalFile()
    {
        using var fixture = new TemporaryTransferDirectory();
        var mismatchDestination = Path.Combine(fixture.Path, "mismatch.bin");
        var mismatchClient = new BackendClient(
            "http://localhost:5048",
            new StaticContentHandler([1, 2, 3]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            mismatchClient.DownloadVerifiedFileAsync(
                "api/file",
                mismatchDestination,
                Convert.ToHexStringLower(SHA256.HashData([9, 9, 9]))));

        Assert.False(File.Exists(mismatchDestination));
        Assert.False(File.Exists(mismatchDestination + ".partial"));

        var errorDestination = Path.Combine(fixture.Path, "server-error.bin");
        var errorClient = new BackendClient(
            "http://localhost:5048",
            new StaticContentHandler([], statusCode: HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            errorClient.DownloadFileAsync("api/file", errorDestination));
        Assert.False(File.Exists(errorDestination));
    }

    [Fact]
    public void ExamFileNames_AreSanitizedAndMadeUniqueCaseInsensitively()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safe = SubmissionBatchDownloader.MakeSafePathComponent(
            "../Đề thi cuối kỳ?.pdf",
            "exam-file",
            160);
        var first = SubmissionBatchDownloader.MakeUniqueFileName(safe, used);
        var second = SubmissionBatchDownloader.MakeUniqueFileName(safe.ToUpperInvariant(), used);

        Assert.DoesNotContain("..", safe, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, safe);
        Assert.EndsWith(".pdf", first, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(item.Content);
            }
        }
        return stream.ToArray();
    }

    private sealed class StaticContentHandler(
        byte[] bytes,
        long? announcedLength = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(bytes);
            if (announcedLength.HasValue)
                content.Headers.ContentLength = announcedLength.Value;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }

    private sealed class TemporaryTransferDirectory : IDisposable
    {
        public TemporaryTransferDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"examtransfer-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
