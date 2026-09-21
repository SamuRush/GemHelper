using System;
using System.IO;
using Gem.Voice;
using Xunit;

namespace Gem.Tests;

public class VoskModelHelperTests : IDisposable
{
    private readonly string _tempTestDir;

    public VoskModelHelperTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), $"vosk_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                Directory.Delete(_tempTestDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void IsModelAvailable_EmptyOrNonExistentDirectory_ReturnsFalse()
    {
        Assert.False(VoskModelHelper.IsModelAvailable("non_existent_folder_xyz_123"));
        Assert.False(VoskModelHelper.IsModelAvailable(_tempTestDir));
    }

    [Fact]
    public void IsModelAvailable_ValidModelDirectory_ReturnsTrue()
    {
        string amDir = Path.Combine(_tempTestDir, "am");
        Directory.CreateDirectory(amDir);
        File.WriteAllText(Path.Combine(amDir, "final.mdl"), "dummy model");

        Assert.True(VoskModelHelper.IsModelAvailable(_tempTestDir));
    }

    [Fact]
    public void FindModelDirectory_FindsPreferredPath_WhenValid()
    {
        string validDir = Path.Combine(_tempTestDir, "custom_model");
        Directory.CreateDirectory(Path.Combine(validDir, "conf"));
        File.WriteAllText(Path.Combine(validDir, "conf", "model.conf"), "dummy config");

        string? found = VoskModelHelper.FindModelDirectory(validDir);

        Assert.NotNull(found);
        Assert.Equal(validDir, found);
    }

    [Fact]
    public void FindModelDirectory_FindsNestedSubfolder_WhenModelIsInsideSubdir()
    {
        string parentDir = Path.Combine(_tempTestDir, "models_vosk_parent");
        string nestedDir = Path.Combine(parentDir, "vosk-model-ru-0.42");
        Directory.CreateDirectory(Path.Combine(nestedDir, "am"));
        File.WriteAllText(Path.Combine(nestedDir, "README"), "model readme");

        string? found = VoskModelHelper.FindModelDirectory(parentDir);

        Assert.NotNull(found);
        Assert.Equal(nestedDir, found);
    }
}
