using System;

namespace GitBifrost
{
    /* SkippedLate is the same as skipped but for when a store push was attempted,
     * but another client updated the file before this one could complete 
    */
    enum SyncResult
    {
        Failed = 0,
        Success = 1,
        Skipped = 3,
        SkippedLate = 4
    }

    interface IStore
    {
        bool HasValidEndpoint(string url);
        bool FileExists(string url, string filename);
        SyncResult PushFile(string localfilepath, string url, string filename);
        byte[] PullFile(string url, string filename);
    }
}

