namespace YMM4CloudSync.Core.Services;

public sealed class CloudStorageFullException(string message) : InvalidOperationException(message);
