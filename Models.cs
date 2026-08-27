using System;

public record LogFileName(string Id, long Stamp, string Kind);

public record IisEntry(DateTime Timestamp, string Method, string UriStem, string UriQuery, string ClientIp, int Status, long Bytes, int TimeTaken);

public record InboundEntry(DateTime Timestamp, string Level, string Message);
