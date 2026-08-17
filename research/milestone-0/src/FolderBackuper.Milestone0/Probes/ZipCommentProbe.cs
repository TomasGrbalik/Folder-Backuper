using System.IO.Compression;

namespace FolderBackuper.Milestone0.Probes;

public static class ZipCommentProbe
{
    public static ProbeResult Run(string workingDirectory, Guid installationId, Guid runId)
    {
        var archivePath = Path.Combine(workingDirectory, $"zip-comment-{runId:N}.zip");
        var expectedComment = $"FolderBackuper:v1;installation={installationId:D};run={runId:D}";

        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.Comment = expectedComment;
                using var writer = new StreamWriter(archive.CreateEntry("sample/readme.txt").Open());
                writer.Write("Milestone 0 ZIP compatibility probe");
            }

            using var reopened = ZipFile.OpenRead(archivePath);
            return reopened.Comment == expectedComment && reopened.GetEntry("sample/readme.txt") is not null
                ? new ProbeResult("ZIP archive comment", ProbeStatus.Passed, "Installation and run identifiers round-tripped exactly.")
                : new ProbeResult("ZIP archive comment", ProbeStatus.Failed, "The archive comment or representative entry did not round-trip.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new ProbeResult("ZIP archive comment", ProbeStatus.Failed, exception.Message);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }
}
