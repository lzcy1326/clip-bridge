namespace ClipBridge.App;

public static class RemoteClipboardWritePriority
{
    public static bool IsNewerThanLocalCopy(long remoteServerMilliseconds, long localCopyMilliseconds,
        long serverClockOffsetMilliseconds, long uncertaintyMilliseconds)
    {
        if (localCopyMilliseconds <= 0) return true;
        // Remote timestamps come from the server; local copy times come from Windows.
        // Treat close calls as local-first, including HTTP Date rounding and transit time.
        var localOnServerClock = localCopyMilliseconds + serverClockOffsetMilliseconds;
        return remoteServerMilliseconds > localOnServerClock + uncertaintyMilliseconds;
    }
}
