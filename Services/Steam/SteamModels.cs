namespace Gem.Services;

/// <summary>
/// Model representing a Steam game indexed from local libraries, manifests, or cache.
/// </summary>
public sealed record SteamGameInfo(
    string AppId,
    string Title,
    string NormalizedTitle,
    string CyrillicTitle,
    bool IsInstalled,
    string? InstallPath = null,
    int StateFlags = 4,
    bool IsFromManifest = false
)
{
    public string Name => Title;
}

/// <summary>
/// Result of a Steam game search with confidence grading.
/// <para>Confidence &gt;= 0.82 — exact match (Found = true, RequiresConfirmation = false).</para>
/// <para>0.60 &lt;= Confidence &lt; 0.82 — probable candidate (Found = true, RequiresConfirmation = true).</para>
/// <para>Confidence &lt; 0.60 — not found (Found = false).</para>
/// </summary>
public sealed class GameSearchResult
{
    /// <summary>True if a matching game was found (confidence >= 0.60).</summary>
    public bool Found { get; set; }

    /// <summary>The matched game, or null when Found is false.</summary>
    public SteamGameInfo? Game { get; set; }

    /// <summary>Fuzzy similarity score from 0.0 to 1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>True when the match is probable but not certain (0.60 &lt;= Confidence &lt; 0.82) — requires user confirmation.</summary>
    public bool RequiresConfirmation => Confidence >= 0.60 && Confidence < 0.82;

    public GameSearchResult() { }

    public GameSearchResult(SteamGameInfo? game, double confidence, bool? found = null)
    {
        Game = game;
        Confidence = confidence;
        Found = found ?? (confidence >= 0.60);
    }

    // Backward-compatibility forwarders for callers expecting SteamGameInfo
    public string AppId => Game?.AppId ?? string.Empty;
    public string Title => Game?.Title ?? string.Empty;
    public string Name => Game?.Name ?? string.Empty;
    public bool IsInstalled => Game?.IsInstalled ?? false;
    public string? InstallPath => Game?.InstallPath;

    public static implicit operator SteamGameInfo?(GameSearchResult? result) => result?.Game;
}
