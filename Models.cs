using System;

public record LogFileName(string Id, long Stamp, string Kind);

public record IisEntry(DateTime Timestamp, string Method, string UriStem, string UriQuery, string ClientIp, int Status, long Bytes, int TimeTaken);

public record InboundEntry(DateTime Timestamp, string Level, string Message);

public sealed record SystemLockDto(string InstallationId, string LockId, string TypeOfLock, string ErrorMessage, DateTime? CreatedDateTime);

public sealed record UnlockLockRequest(string InstallationId, string LockId, string TypeOfLock);

public sealed class LocksApiException(string message, int statusCode) : Exception(message)
{
	public int StatusCode { get; } = statusCode;
}
