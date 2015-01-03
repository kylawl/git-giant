// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.IO;
using System.Collections.Generic;

namespace GitBifrost
{
    class StoreFileSystem : IStoreInterface
    {
        public bool OpenStore(Uri uri, Dictionary<string, string> store)
        {
            bool directory_exists = Directory.Exists(uri.LocalPath);

            if (!directory_exists)
            {
                Program.LogLine(LogNoiseLevel.Loud, "Bifrost: Unable to find store '{0}'", uri.LocalPath);
            }

            return directory_exists && uri.Scheme == Uri.UriSchemeFile;
        }

        public void CloseStore()
        {
            /* do nothing */
        }

        public SyncResult PushFile(string source_file, Uri store_location, string filename)
        {
            string store_filepath = Path.Combine(store_location.LocalPath, filename);
            string dir = Path.GetDirectoryName(store_filepath);

            try
            {
                if (!File.Exists(store_filepath))
                {
                    Program.LogLine(LogNoiseLevel.Debug, "File doesn't exist in store, will update");

                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                        Program.LogLine(LogNoiseLevel.Debug, "Created directory '{0}'", dir);
                    }

                    Guid guid = Guid.NewGuid();

                    string store_filename_temp = string.Format("{0}.tmp", guid.ToString().Replace("-", ""));
                    string store_filepath_temp = Path.Combine(dir, store_filename_temp);

                    File.Copy(source_file, store_filepath_temp);

                    if (!File.Exists(store_filepath))
                    {
                        File.Move(store_filepath_temp, store_filepath);
                        return SyncResult.Success;
                    }
                    else
                    {
                        File.Delete(store_filepath_temp);
                        return SyncResult.SkippedLate;
                    }
                }
            }
            catch
            {
                return SyncResult.Failed;
            }

            return SyncResult.Skipped;
        }

        public byte[] PullFile(Uri uri, string filename)
        {
            string input_filepath = Path.Combine(uri.AbsolutePath, filename);

            if (File.Exists(input_filepath))
            {
                return File.ReadAllBytes(input_filepath);
            }

            return null;
        }
    }
}

                