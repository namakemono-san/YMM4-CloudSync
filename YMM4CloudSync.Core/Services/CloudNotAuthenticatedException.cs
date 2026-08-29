namespace YMM4CloudSync.Core.Services;

public sealed class CloudNotAuthenticatedException(string message) : InvalidOperationException(message);
