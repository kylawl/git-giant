// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.Net.FtpClient;
using System.IO;
using System.Collections.Generic;
using System.Net;

namespace GitBifrost
{
    class StoreFtp : IStoreInterface
    {
        #region IStoreInterface implementation

        FtpClient Client = null;
//        Dictionary<string, string> Store;

        public bool OpenStore(Uri uri, Dictionary<string, string> store)
        {
            if (Client != null)
            {
                Program.LogLine(LogNoiseLevel.Normal, "Store is already open, did you forget to close the other one first?");
                return false;
            }

            bool store_available = false;

            if (uri.Scheme == Uri.UriSchemeFtp || uri.Scheme == "ftps")
            {
                NetworkCredential credentials = new NetworkCredential();
                credentials.UserName = store.GetValue("username", "anonymous");
                credentials.Password = store.GetValue("password", "");

                FtpClient ftp_client = new FtpClient();
                ftp_client.Credentials = credentials;
                ftp_client.Host = uri.Host;

                try
                {
                    ftp_client.Connect();
                }
                catch
                {
                    // It's ok to do nothing here
                }

                if (ftp_client.IsConnected && ftp_client.DirectoryExists(uri.AbsolutePath))
                {
                    ftp_client.SetWorkingDirectory(uri.AbsolutePath);
                    Client = ftp_client;
                    store_available = true;
                }
                else
                {
                    Program.LogLine(LogNoiseLevel.Loud, "Can't connect to {0}", uri.AbsoluteUri);
                }
            }

            return store_available;
        }

        public void CloseStore()
        {
            Client.Disconnect();
            Client = null;
        }
            
        public SyncResult PushFile(string source_file, Uri store_location, string filename)
        {
            string dir = Path.GetDirectoryName(filename);

            try
            {
                if (!Client.FileExists(filename))
                {
                    if (!Client.DirectoryExists(dir))
                    {
                        try
                        {
                            Client.CreateDirectory(dir);
                        }
                        catch
                        {
                            Program.LogLine(LogNoiseLevel.Normal, "Bifrost: Unable to create directory '{0}' in '{1}'. Do you have the correct permissions?", 
                                dir, Client.GetWorkingDirectory());

                            return SyncResult.Failed;
                        }
                    }


                    Guid guid = Guid.NewGuid();

                    string store_filename_temp = string.Format("{0}.tmp", guid.ToString().Replace("-", ""));
                    string store_filepath_temp = Path.Combine(dir, store_filename_temp);


                    using (Stream output_stream = Client.OpenWrite(store_filepath_temp))
                    {
                        using (Stream file_stream = File.OpenRead(source_file))
                        {
                            file_stream.CopyTo(output_stream);
                        }
                    }

                    if (!Client.FileExists(filename))
                    {
                        Client.Rename(store_filepath_temp, filename);
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

            if (Client.FileExists(input_filepath))
            {
                long size = Client.GetFileSize(input_filepath);

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

