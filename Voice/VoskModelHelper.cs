using System.IO.Compression;

namespace Gem.Voice;

public static class VoskModelHelper
{
    public const string DefaultModelUrl = "https://alphacephei.com/vosk/models/vosk-model-ru-0.42.zip";
    public const string DefaultModelFolder = "model";

    /// <summary>
    /// Checks if a valid Vosk model directory exists.
    /// Vosk models typically contain 'am', 'conf', or 'ivector'.
    /// </summary>
    public static bool IsModelAvailable(string modelPath = DefaultModelFolder)
    {
        if (!Directory.Exists(modelPath))
        {
            return false;
        }

        // Check for typical Vosk model files/directories
        return Directory.Exists(Path.Combine(modelPath, "am")) ||
               File.Exists(Path.Combine(modelPath, "am", "final.mdl")) ||
               Directory.Exists(Path.Combine(modelPath, "conf")) ||
               File.Exists(Path.Combine(modelPath, "README"));
    }

    /// <summary>
    /// Downloads and extracts the full-size Russian Vosk model vosk-model-ru-0.42 (~1.5 GB) to the specified folder.
    /// </summary>
    public static async Task DownloadModelAsync(
        string targetDirectory = DefaultModelFolder,
        string downloadUrl = DefaultModelUrl,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"vosk_model_{Guid.NewGuid():N}.zip");

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[65536];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int percent = (int)(totalRead * 100 / totalBytes.Value);
                        progress?.Report(percent);
                    }
                }
            }

            // Extract ZIP
            string tempExtractDir = Path.Combine(Path.GetTempPath(), $"vosk_extract_{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractDir, true);

            // Vosk zip usually has a root folder like "vosk-model-ru-0.42"
            var subDirs = Directory.GetDirectories(tempExtractDir);
            string sourceFolder = subDirs.Length == 1 ? subDirs[0] : tempExtractDir;

            foreach (var file in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceFolder, file);
                string destFile = Path.Combine(targetDirectory, relativePath);
                string? destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                File.Copy(file, destFile, true);
            }

            try 
            { 
                Directory.Delete(tempExtractDir, true); 
            } 
            catch (Exception ex)
            { 
                System.Diagnostics.Debug.WriteLine($"[VoskModelHelper] Не удалось удалить временную папку {tempExtractDir}: {ex.Message}");
            }
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try 
                { 
                    File.Delete(tempZipPath); 
                } 
                catch (Exception ex)
                { 
                    System.Diagnostics.Debug.WriteLine($"[VoskModelHelper] Не удалось удалить временный архив {tempZipPath}: {ex.Message}");
                }
            }
        }
    }
}
