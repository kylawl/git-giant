using System;
using Renci.SshNet;
using System.IO;

namespace GitGiant
{
    class StoreSftp : IStoreInterface
    {
        SftpClient Client;

        public StoreSftp()
        {
        }

        #region IStoreInterface implementation

        public bool OpenStore(Uri uri, System.Collections.Generic.Dictionary<string, string> store)
        {
            if (Client != null)
            {
                Program.LogLine(LogNoiseLevel.Normal, "Store is already open, did you forget to close the other one first?");
                return false;
            }

            bool store_available = false;

            if (uri.Scheme == "sftp")
            {
                SftpClient sftp_client = new SftpClient(uri.Host, 22, 
                    store.GetValue("username", "anonymous"), 
                    store.GetValue("password", ""));

                try
                {
                    sftp_client.Connect();

                    if (sftp_client.IsConnected && sftp_client.Exists(uri.AbsolutePath))
                    {
                        sftp_client.ChangeDirectory(uri.AbsolutePath);

                        Client = sftp_client;
                        store_available = true;
                    }
                }
                catch
                {
                    Program.LogLine(LogNoiseLevel.Loud, "Can't connect to {0}", uri.AbsoluteUri);
                }
            }

            return store_available;
        }

        public void CloseStore()
        {
            Client.Disconnect();
            Client.Dispose();
            Client = null;
        }

        public SyncResult PushFile(string source_file, Uri store_location, string filename)
        {
            string dir = Path.GetDirectoryName(filename);

            try
            {
                if (!Client.Exists(filename))
                {
                    if (!Client.Exists(dir))
                    {
                        try
                        {
                            string[] dirs = dir.Split(new char[] {Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar}, StringSplitOptions.RemoveEmptyEntries); 

                            foreach(string sub_dir in dirs)
                            {
                                Client.CreateDirectory(sub_dir);
                                Client.ChangeDirectory(sub_dir);
                            }

                            Client.ChangeDirectory(store_location.AbsolutePath);
                        }
                        catch
                        {
                            Program.LogLine("git-giant: Unable to create directory '{0}' in '{1}'. Do you have the correct permissions?", 
                                dir, Client.WorkingDirectory);

                            return SyncResult.Failed;
                        }
                    }


                    Guid guid = Guid.NewGuid();

                    string store_filename_temp = string.Format("{0}.tmp", guid.ToString().Replace("-", ""));
                    string store_filepath_temp = Path.Combine(store_location.AbsolutePath, dir, store_filename_temp);

                    using (Stream output_stream = Client.Create(store_filepath_temp))
                    {
                        using (Stream file_stream = File.OpenRead(source_file))
                        {
                            file_stream.CopyTo(output_stream);
                        }
                    }

                    if (!Client.Exists(filename))
                    {
                        Client.RenameFile(store_filepath_temp, filename);
                        return SyncResult.Success;
                    }
                    else
                    {
                        Client.DeleteFile(store_filepath_temp);
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
            string input_filepath = Path.Combine(uri.LocalPath, filename);

            if (Client.Exists(input_filepath))
            {
                long size = Client.GetAttributes(filename).Size;

                byte[] buffer = new byte[size];

                MemoryStream mem_stream = new MemoryStream(buffer);

                using (Stream input_stream = Client.OpenRead(input_filepath))
                {
                    input_stream.CopyTo(mem_stream);
                }

                return buffer;
            }

            return null;
        }

        #endregion
    }
}

