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
        bool IsStoreAvailable(Uri store_location);
        bool FileExists(string url, string filename);

        /// <summary>
        /// Pushs the file to a datastore
        /// </summary>
        /// <returns>Result of the update operation</returns>
        /// <param name="localfilepath">Where to find the local file</param>
        /// <param name="store_location">The location of the store</param>
        /// <param name="filename">The file name to write to in the store</param>
        SyncResult PushFile(string localfilepath, Uri store_location, string filename);
        byte[] PullFile(string url, string filename);
    }
}

