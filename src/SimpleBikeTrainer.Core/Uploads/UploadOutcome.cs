using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Uploads;

public enum UploadState
{
    Uploaded,
    Duplicate,
    Failed,
}

/// <summary>What happened to one upload. <see cref="Url"/> links to the activity when the service returned one.</summary>
public sealed record UploadOutcome(UploadState State, string? Url, string? Error)
{
    public static UploadOutcome Uploaded(string? url) => new(UploadState.Uploaded, url, null);

    public static UploadOutcome Duplicate(string? url) => new(UploadState.Duplicate, url, null);

    public static UploadOutcome Failed(string error) => new(UploadState.Failed, null, error);

    public bool Succeeded => State != UploadState.Failed;

    public override string ToString() => State switch
    {
        UploadState.Uploaded => Url is null ? CoreStrings.Upload_Done : Loc.Format(CoreStrings.Upload_DoneAt, Url),
        UploadState.Duplicate => Url is null ? CoreStrings.Upload_Duplicate : Loc.Format(CoreStrings.Upload_DuplicateAt, Url),
        _ => Loc.Format(CoreStrings.Upload_Failed, Error),
    };
}
