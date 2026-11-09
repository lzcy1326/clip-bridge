namespace ClipBridge.Storage.Repositories;

public sealed record HistoryCleanupResult(int DeletedItems, long ReclaimedImageBytes);
