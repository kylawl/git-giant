using System;
using System.IO;

namespace GitBifrost
{
    class StoreFileSystem : IStore
    {
        public StoreFileSystem()
        {

        }

        public bool HasValidEndpoint(string url)
        {
            return Directory.Exists(url);
        }

        public bool FileExists(string url, string filename)
        {
            string filepath = Path.Combine(url, filename);
            return File.Exists(filepath);
        }

        public SyncResult PushFile(string localfilepath, string url, string filename)
        {
            string store_filepath = Path.Combine(url, filename);

            try
            {
                if (!File.Exists(store_filepath))
                {
                    Guid guid = Guid.NewGuid();

                    string store_filename_temp = string.Format("{0}.tmp", guid.ToString().Replace("-", ""));
                    string store_filepath_temp = Path.Combine(url, store_filename_temp);

                    File.Copy(localfilepath, store_filepath_temp);

                    if (!File.Exists(store_filepath))
                    {
                        File.Move(store_filepath_temp, store_filepath);
                        return SyncResult.Success;
                    }
                    else
                    {
                        File.Delete(store_filepath);
                        return SyncResult.Skipped;
                    }
                }
            }
            catch
            {
                return SyncResult.Failed;
            }

            // TODO: Safety check for comparing size of local file to hash file (incase of collision)

            return SyncResult.SkippedLate;
        }

        public byte[] PullFile(string url, string filename)
        {
            string input_filepath = Path.Combine(url, filename);

            if (File.Exists(input_filepath))
            {
                return File.ReadAllBytes(input_filepath);
            }

            return null;
        }
    }
}

                