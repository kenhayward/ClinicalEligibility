namespace EligibilityProcessing.Web.Embeddings;

/// <summary>Which embeddings maintenance job to run.</summary>
public enum EmbeddingsJobKind
{
    /// <summary>pg_dump the embeddings table to a downloadable archive.</summary>
    Export,

    /// <summary>Clear the embeddings table, then pg_restore an uploaded/downloaded archive.</summary>
    Import
}

/// <summary>
/// A queued embeddings job. For <see cref="EmbeddingsJobKind.Export"/> both source
/// fields are null. For <see cref="EmbeddingsJobKind.Import"/> exactly one is set:
/// <paramref name="InputFilePath"/> for an uploaded archive already saved to a temp
/// file, or <paramref name="SourceUrl"/> for a release-asset URL the runner downloads.
/// </summary>
public sealed record EmbeddingsJobRequest(
    Guid JobId,
    EmbeddingsJobKind Kind,
    string? InputFilePath,
    string? SourceUrl);
