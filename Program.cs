// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;

#if __MonoCS__
using Mono.Unix.Native;
#endif // __MonoCS__

namespace GitBifrost
{
    class StoreData : Dictionary<string, string> { }
    class StoreContainer : Dictionary<string, StoreData> { }
    delegate int CommandDelegate(string[] args);

    class Program
    {
        const int StartingBufferSize = 1024 * 1024;

        const string LocalStoreLocation = ".git/bifrost/data";

        static readonly string[] StandardAttributes = new string[]
        {
            "*.bmp filter=bifrost",
            "*.dae filter=bifrost",
            "*.fbx filter=bifrost",
            "*.jpg filter=bifrost",
            "*.max filter=bifrost",
            "*.obj filter=bifrost",
            "*.png filter=bifrost",
            "*.psd filter=bifrost",
            "*.tga filter=bifrost",
            "*.ttf filter=bifrost",
            "*.ztl filter=bifrost",
        };

        static StreamWriter LogWriter = null;
        static int GitExitCode = 0;

        public static void LogLine(string format, params object[] arg)
        {
            LogWriter.WriteLine(format, arg);
            Console.Error.WriteLine(format, arg);
        }

        static Dictionary<string, IStoreInterface> GetStoreInterfaces()
        {
            var store_interfaces = new Dictionary<string, IStoreInterface>();

            store_interfaces[Uri.UriSchemeFile] = new StoreFileSystem();

            return store_interfaces;
        }

        static int Main(string[] args)
        {
            //Debugger.Break();

//            Uri ftp = new Uri("ftp://ftp.bacon.com/some_path/to/thisfile.bin");
//            Uri cdn = new Uri("ftp://cdn.bacon.com/some_path/to/thisfile.bin");
//            Uri localfile = new Uri("/local_directory/some_path/to/thisfile.bin");
//            Uri localfile2 = new Uri("file:///local_directory/some_path/to/thisfile.bin");
//            Uri ip = new Uri("ftp://192.168.50.1:2432/some_path/to/thisfile.bin");
//            Uri sftp = new Uri("sftp://192.168.50.1:24/some_path/to/thisfile.bin");

            int result = 0;

            using (LogWriter = new StreamWriter(File.Open("bifrostlog.txt", FileMode.Append, FileAccess.Write)))
            {
                LogLine("------ Bifrost: {0} ------", string.Join(" ", args));
                LogLine("Current Dir: {0}", Directory.GetCurrentDirectory());
//                LogLine("PATH: {0}", Environment.GetEnvironmentVariable("PATH"));

                Dictionary<string, CommandDelegate> Commands = new Dictionary<string, CommandDelegate>(10);

                Commands["hook-sync"] = HookSync;
                Commands["hook-push"] = HookPush;
                Commands["filter-clean"] = FilterClean;
                Commands["filter-smudge"] = FilterSmudge;
                Commands["help"] = Help;
                Commands["clone"] = Clone;
                Commands["init"] = Init;
                Commands["activate"] = Activate;


                string arg_command = args.Length > 0 ? args[0].ToLower() : null;

                if (arg_command != null)
                {
                    result = Commands[arg_command](args);
                }

                LogLine("");
            }

            return result;
        }

        static int HookSync(string[] args)
        {
            return 0;
        }

        static int HookPush(string[] args)
        {
            string arg_remote_name = args[1];
            Uri arg_remote_uri = new Uri(args[2]);

            // TODO: If there directory doesn't exist BUT the stuff we're about to push includes files that should be in there,
            // something has gone very wrong. You should probably warn the user.
            if (Directory.Exists(LocalStoreLocation))
            {
                LogLine("Bifrost: Updating datastore(s) for remote '{0}' ({1})", arg_remote_name, arg_remote_uri.AbsoluteUri);

                var store_interfaces = GetStoreInterfaces();
                StoreContainer stores = GetStores();

                foreach (KeyValuePair<string, StoreData> store_kvp in stores)
                {
                    StoreData store_data = store_kvp.Value;

                    Uri store_remote_uri = new Uri(store_data["remote"]);
                    Uri store_uri = new Uri(store_data["url"]);

                    if (store_remote_uri != arg_remote_uri)
                    {
                        continue;
                    }

                    IStoreInterface store_interface = store_interfaces[store_uri.Scheme];
                    if (store_interface == null || !store_interface.IsStoreAvailable(store_uri))
                    {
                        continue;
                    }

                    LogLine("Bifrost: Updating store: '{0}'", store_uri.AbsoluteUri);

                    int files_pushed = 0;
                    int files_skipped = 0;
                    int files_skipped_late = 0;

                    string[] local_files = Directory.GetFiles(LocalStoreLocation);

                    foreach (string file in local_files)
                    {
                        string filename = Path.GetFileName(file);

                        SyncResult result = store_interface.PushFile(file, store_uri, filename);

                        if (result == SyncResult.Failed)
                        {
                            LogLine("Bifrost: Failed to push file {0} to {1}", file, store_remote_uri.AbsolutePath);
                            return 1;
                        }
                        else if (result == SyncResult.Success)
                        {
                            ++files_pushed;
                        }
                        else if (result == SyncResult.Skipped)
                        {
                            ++files_skipped;
                        }
                        else if (result == SyncResult.SkippedLate)
                        {
                            ++files_skipped_late;
                        }
                    }

                    LogLine("Bifrost: {0} Copied, {1} Skipped, {2} Skipped late", files_pushed, files_skipped, files_skipped_late);
                }
            }

            return 0;
        }

        /// <summary>
        /// Updates the local data store with files from a git commit
        /// </summary>       
        static int FilterClean(string[] args)
        {            
            string arg_filepath = null;

            if (args.Length > 1)
            {
                arg_filepath = args[1];                
            }
            else
            {
                return 1;
            }

            MemoryStream file_stream = new MemoryStream(StartingBufferSize);

            using (Stream stdin = Console.OpenStandardInput())
            {
                stdin.CopyTo(file_stream);
                file_stream.Position = 0;
            }

            byte[] file_hash_bytes = SHA1.Create().ComputeHash(file_stream);
            string file_hash = BitConverter.ToString(file_hash_bytes).Replace("-", "");

            file_stream.Position = 0;

            using (StreamWriter output_writer = new StreamWriter(Console.OpenStandardOutput()))
            {
                output_writer.WriteLine(file_hash);
                output_writer.WriteLine(file_stream.Length);
            }

            LogLine("Name: {0}", arg_filepath);
            LogLine("Bytes: {0}", file_stream.Length);
            LogLine("Hash: {0}", file_hash);

            string output_filename = String.Format("{0}-{1}.bin", file_hash, Path.GetFileName(arg_filepath));           

            WriteToLocalStore(file_stream, output_filename);

            return 0;
        }

        static int FilterSmudge(string[] args)
        {
//            Debugger.Break();

            string arg_filepath = null;

            if (args.Length > 1)
            {
                arg_filepath = args[1];
            }
            else
            {
                return 1;
            }

            string expected_file_hash = null;
            int expected_file_size = -1;

            using (StreamReader input_reader = new StreamReader(Console.OpenStandardInput()))
            {
                expected_file_hash = input_reader.ReadLine(); 
                expected_file_size = int.Parse(input_reader.ReadLine());
            }

            string input_filename = String.Format("{0}-{1}.bin", expected_file_hash, Path.GetFileName(arg_filepath));

            //
            // Collect up all the store locations in the config file
            //

			string[] loaded_stores = GitConfigGetRegex(@".*\.url$", ".gitbifrost");

            string[] data_stores = new string[loaded_stores.Length + 1];
            data_stores[0] = LocalStoreLocation;

            int index = 1;
            foreach(string store_url in loaded_stores)
            {
                string[] store_tokens = store_url.Split(new char[] {' '}, 2);
                data_stores[index++] = store_tokens[1];
            }

            bool succeeded = false;

            var store_interfaces = GetStoreInterfaces();

            foreach (string datastore_location in data_stores)
            {
                Uri store_uri = new Uri(datastore_location);

                IStoreInterface store_interface = store_interfaces[store_uri.Scheme];

                if (store_interface != null)
                {
                    byte[] file_contents = store_interface.PullFile(datastore_location, input_filename);

                    if (file_contents != null)
                    {
                        int loaded_file_size = file_contents.Length;

                        byte[] loaded_file_hash_bytes = SHA1.Create().ComputeHash(file_contents, 0, loaded_file_size);
                        string loaded_file_hash = BitConverter.ToString(loaded_file_hash_bytes).Replace("-", "");

                        LogLine("Name: {0}", arg_filepath);
                        LogLine("Expect Hash: {0}", expected_file_hash);
                        LogLine("Loaded Hash: {0}", loaded_file_hash);
                        LogLine("Expected Size: {0}", expected_file_size);
                        LogLine("Loaded Size: {0}", loaded_file_size);

                        //
                        // Safety checking size and hash
                        //

                        if (expected_file_size != loaded_file_size)
                        {
                            LogLine("!!!ERROR!!!");
                            LogLine("File size missmatch with '{0}'", arg_filepath);
                            LogLine("Store '{0}'", datastore_location);
                            LogLine("Expected {0}, got {1}", expected_file_size, loaded_file_size);
                            continue;
                        }

                        if (loaded_file_hash != expected_file_hash)
                        {
                            LogLine("!!!ERROR!!!");
                            LogLine("File hash missmatch with '{0}'", arg_filepath);
                            LogLine("Store '{0}'", datastore_location);
                            LogLine("Expected {0}, got {1}", expected_file_hash, loaded_file_hash);
                            continue;
                        }

                        //
                        // Finally write file
                        //

                        WriteToLocalStore(new MemoryStream(file_contents), input_filename);

                        using (Stream stdout = Console.OpenStandardOutput())
                        {
                            stdout.Write(file_contents, 0, loaded_file_size);
                        }

                        succeeded = true;

                        break;
                    }
                }
                else
                {
                    LogLine("Unrecognized store type: '{0}'", store_uri.Scheme);
                }
            }

            if (!succeeded)
            {
                LogLine("Bifrost: Failed to get file '{0}'", arg_filepath);
            }

            return succeeded ? 0 : 1;
        }

        static int Help(string[] args)
        {
            return 0;
        }

        static int Clone(string[] args)
        {
            string clone_args = string.Join(" ", args, 1, args.Length - 1);

            Git("clone", "--no-checkout", clone_args);

            if (GitExitCode == 0)
            {
                string arg_directory = args.Length > 2 ? args[args.Length - 1] : "./";

                Directory.SetCurrentDirectory(arg_directory);

                InstallBifrost();

                Git("checkout");
            }

            return GitExitCode;
        }

        static int Init(string[] args)
        {
            string init_args = string.Join(" ", args);

            Git(init_args);           

            if (GitExitCode == 0)
            {
                string arg_directory = args[args.Length - 1];

                if (Directory.Exists(arg_directory))
                {
                    Directory.SetCurrentDirectory(arg_directory);
                }

                InstallBifrost();
            }

            return GitExitCode;
        }

        static int Activate(string[] args)
        {
            if (!Directory.Exists(".git"))
            {
                Console.WriteLine("No git repository at '{0}'", Directory.GetCurrentDirectory());
                return 1;
            }

            InstallBifrost();

            // Generate sample config

            GitConfigSet("localstore.depth", "0", ".gitbifrost");

            GitConfigSet("store.luminawesome.mac.remote", "/Users/kylerocha/dev/BifrostTest.git", ".gitbifrost");
            GitConfigSet("store.luminawesome.mac.url", "/Users/kylerocha/dev/BifrostTestCDN", ".gitbifrost");    

            GitConfigSet("store.luminawesome.mac-backup.remote", "/Users/kylerocha/dev/BifrostTest.git", ".gitbifrost");
            GitConfigSet("store.luminawesome.mac-backup.url", "/Users/kylerocha/dev/BifrostTestCDN2", ".gitbifrost");    

            GitConfigSet("store.luminawesome.onsite.remote", "file:///D:/Work/BifrostTest.git", ".gitbifrost");
            GitConfigSet("store.luminawesome.onsite.url", "file:///D:/Work/BifrostTestCDN", ".gitbifrost");           

            GitConfigSet("store.luminawesome.offsite.remote", "https://github.com/kylawl/BifrostTest.git", ".gitbifrost");
            GitConfigSet("store.luminawesome.offsite.url", "file:///D:/Work/BifrostTestCDN", ".gitbifrost");
            GitConfigSet("store.luminawesome.offsite.user", "kyle", ".gitbifrost");
            GitConfigSet("store.luminawesome.offsite.password", "some_password", ".gitbifrost");

            if (args.Contains("-ica", StringComparer.CurrentCultureIgnoreCase) ||
                args.Contains("--include-common-attributes", StringComparer.CurrentCultureIgnoreCase))
            {
                File.WriteAllLines(".gitattributes", StandardAttributes);
            }

            Console.WriteLine("Bifrost is now active");

            return 0;
        }

        static int InstallBifrost()
        {
            GitConfigSet("filter.bifrost.clean", "git-bifrost filter-clean %f");
            if (GitExitCode != 0) { return GitExitCode; }

            GitConfigSet("filter.bifrost.smudge", "git-bifrost filter-smudge %f");
            if (GitExitCode != 0) { return GitExitCode; }

            GitConfigSet("filter.bifrost.required", "true");
            if (GitExitCode != 0) { return GitExitCode; }

            try
            {
                File.WriteAllText(".git/hooks/pre-push", "#!/bin/bash\ngit-bifrost hook-push \"$@\"");
                File.WriteAllText(".git/hooks/post-checkout", "#!/bin/bash\ngit-bifrost hook-sync \"$@\"");

#if __MonoCS__
                Syscall.chmod(".git/hooks/pre-push", FilePermissions.ACCESSPERMS);
                Syscall.chmod(".git/hooks/post-checkout", FilePermissions.ACCESSPERMS);
#endif // __MonoCS__
            }
            catch
            {
                return 1;
            }

            return 0;
        }

        static void WriteToLocalStore(Stream file_stream, string filename)
        {
            string filepath = Path.Combine(LocalStoreLocation, filename);

            if (!File.Exists(filepath))
            {
                if (!Directory.Exists(LocalStoreLocation))
                {
                    Directory.CreateDirectory(LocalStoreLocation);
                }

                using (FileStream output_stream = new FileStream(filepath, FileMode.Create, FileAccess.Write))
                {
                    file_stream.CopyTo(output_stream);

                    LogLine("Bifrost: Local store - updated {0}", filepath);
                }
            }
            else
            {
                LogLine("Bifost: Local store - skipped");
            }
        }

        static StoreContainer GetStores()
        {
            StoreContainer stores = new StoreContainer();

            string[] data_stores_text = GitConfigGetRegex(@"^store\..*", ".gitbifrost");

            foreach (string store_text in data_stores_text)
            {
                string[] store_kv = store_text.Split(new char[]{ ' ' }, 2);

                string store_name = Path.GetFileNameWithoutExtension(store_kv[0]);
                string key = Path.GetExtension(store_kv[0]).Substring(1);

                StoreData store_data = null;

                if (!stores.TryGetValue(store_name, out store_data))
                {
                    store_data = new StoreData();
                    stores[store_name] = store_data;
                }

                store_data[key] = store_kv[1];
            }

            return stores;
        }

        static void GitConfigSet(string key, string value, string file = null)
        {       
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            string command = string.Format("config {0} {1} \"{2}\"", fileArg, key, value);
            
            Git(command);
        }

        static string[] GitConfigGetRegex(string key, string file)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            string command = string.Format("config --get-regexp {0} {1}", fileArg, key);

            return Git(command).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static string Git(params string[] arguments)
        {
            Process process = new Process();
            ProcessStartInfo psi = process.StartInfo;
            psi.FileName = "git";
            psi.Arguments = string.Join(" ", arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.EnvironmentVariables["PATH"] = psi.EnvironmentVariables["PATH"].Replace(@"\", @"\\");                   
            
            string output = string.Empty;
            if (process.Start())
            {
                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
            }

            GitExitCode = process.ExitCode;

            return output;
        }
    }
}