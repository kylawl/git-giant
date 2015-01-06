// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.Collections.Generic;

namespace GitGiant
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

    interface IStoreInterface
    {
        bool OpenStore(Uri uri, Dictionary<string, string> store);
        void CloseStore();

        /// <summary>
        /// Pushs the file to a datastore
        /// </summary>
        /// <returns>Result of the update operation</returns>
        /// <param name="localfilepath">Where to find the local file</param>
        /// <param name="store_location">The location of the store</param>
        /// <param name="filename">The file name to write to in the store</param>
        SyncResult PushFile(string localfilepath, Uri store_location, string filename);
        byte[] PullFile(Uri uri, string filename);
    }
}

