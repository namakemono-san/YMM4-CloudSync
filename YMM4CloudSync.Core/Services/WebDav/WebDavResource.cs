namespace YMM4CloudSync.Core.Services.WebDav;

public sealed record WebDavResource(
    string RelativePath,
    string Name,
    bool IsCollection,
    long? ContentLength,
    DateTime? LastModified
);
