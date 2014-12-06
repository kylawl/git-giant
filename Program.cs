// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;
using System.Text;

#if __MonoCS__
using Mono.Unix.Native;
#endif // __MonoCS__

namespace GitBifrost
{
    class StoreData : Dictionary<string, string> { }
    class StoreContainer : Dictionary<string, StoreData> { }
    delegate int CommandDelegate(string[] args);

    enum LogNoiseLevel
    {
        Normal,
        Loud
    }

    class Program
    {
        const int Version = 1;
        const int StartingBufferSize = 1024 * 1024;
        const string LocalStoreLocation = "./.git/bifrost/data";
        const string GitEmptySha = "0000000000000000000000000000000000000000";
        const int Succeeded = 1;
        const int Failed = 1;

        static StreamWriter LogWriter = null;
        static int GitExitCode = 0;
        static LogNoiseLevel NoiseLevel = LogNoiseLevel.Loud;

        static int Main(string[] args)
        {
            //Debugger.Break();

//            Uri ftp = new Uri("ftp://ftp.bacon.com/some_path/to/thisfile.bin");
//            Uri cdn = new Uri("ftp://cdn.bacon.com/some_path/to/thisfile.bin");
//            Uri localfile = new Uri("/local_directory/some_path/to/thisfile.bin");
//            Uri localfile2 = new Uri("file:///local_directory/some_path/to/thisfile.bin");
//            Uri ip = new Uri("ftp://192.168.50.1:2432/some_path/to/thisfile.bin");
//            Uri sftp = new Uri("sftp://192.168.50.1:24/some_path/to/thisfile.bin");
//
            int result = Succeeded;

            using (LogWriter = new StreamWriter(File.Open("bifrostlog.txt", FileMode.Append, FileAccess.Write)))
            {
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
                    LogLine("Bifrost: {0}", string.Join(" ", args));
                    // LogLine("Current Dir: {0}", Directory.GetCurrentDirectory());
                    // LogLine("PATH: {0}", Environment.GetEnvironmentVariable("PATH"));

                    result = Commands[arg_command](args);
                }
                else
                {
                    Help(args);
                }
            }

            return result;
        }

        static int HookSync(string[] args)
        {
            return Succeeded;
        }

        static int HookPreCommit(string[] args)
        {


            return Succeeded;
        }

        /// <summary>
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        static int HookPush(string[] args)
        {
            // This hook is called with the following parameters:
            // 
            // $1 -- Name of the remote to which the push is being done
            // $2 -- URL to which the push is being done
            // 
            // If pushing without using a named remote those arguments will be equal.
            // 
            // Information about the commits which are being pushed is supplied as lines to
            // the standard input in the form:
            // 
            //   <local ref> <local sha1> <remote ref> <remote sha1>

            string arg_remote_name = args[1];
            Uri arg_remote_uri = new Uri(args[2]);


            //
            // Collect up all the file revisions we need to push
            //

            var file_revs = new List<Tuple<string, string>>();

            using (StreamReader stdin = new StreamReader(Console.OpenStandardInput()))
            {
                string push_info = null;
                while ((push_info = stdin.ReadLine()) != null)
                {
                    LogLine("Bifrost: push info ({0})", push_info);
                    string[] push_tokens = push_info.Split(' ');

//                     string local_ref = push_tokens[0];
                    string local_sha = push_tokens[1];
//                     string remote_ref = push_tokens[2];
                    string remote_sha = push_tokens[3];

                    if (local_sha != GitEmptySha)
                    {
                        var rev_ids = new List<string>();

                        // Get the individual revision ids between you and the remote
                        {
                            string rev_list_range = remote_sha != GitEmptySha ? string.Format("{0}..{1}", remote_sha, local_sha) : local_sha;

                            Process git_proc = StartGit("rev-list", rev_list_range);

                            if (git_proc == null) { return Failed; }

                            string rev_line = null;

                            while ((rev_line = git_proc.StandardOutput.ReadLine()) != null)
                            {
                                rev_ids.Add(rev_line);
                            }

                            if (git_proc.WaitForExitCode() != 0) { return git_proc.ExitCode; }
                        }

                        foreach (string revision in rev_ids)
                        {
                            // Get files modified in this revision
                            Process git_proc = StartGit("diff-tree --no-commit-id --name-status -r -z", revision); 

                            if (git_proc == null) { return Failed; }

                            string status = null;
                            while ((status = ReadToEscape(git_proc.StandardOutput)) != null)
                            {
                                if (status == "X")
                                {
                                    LogLine("According to git something has gone wrong.");
                                    return Failed;
                                }
                                    
                                // Skip files that have been deleted or require merging
                                if (status != "D" && status != "U")
                                {
                                    try
                                    {
                                        string file = ReadToEscape(git_proc.StandardOutput);

                                        Process git_check_attr = StartGit("check-attr -z filter", file);
                                        if (git_check_attr == null) { return Failed; }

                                        git_check_attr.StandardOutput.ReadLine(); // File name
                                        git_check_attr.StandardOutput.ReadLine(); // Attribute (filter)
                                        string filter_tag = git_check_attr.StandardOutput.ReadLine();

                                        if (filter_tag == "bifrost")
                                        {
                                            file_revs.Add(new Tuple<string, string>(revision, file));
                                        }
                                    }
                                    catch
                                    {
                                        LogLine("Failed to get file from diff-tree");
                                        return Failed;
                                    }
                                }
                            }

                            if (git_proc.WaitForExitCode() != 0) { return git_proc.ExitCode; }
                        }
                    }
                    else
                    {
                        // Empty
                        // Commit deleted?
                    }
                }
            }            

            if (file_revs.Count > 0 && !Directory.Exists(LocalStoreLocation))
            {
                LogLine("Bifrost: Local store missing");
                return Failed;
            }

            if (file_revs.Count == 0)
            {
                LogLine("Bifrost: no files to push");
                return Succeeded;
            }

            //
            // Update the stores with the files
            //

            LogLine("Bifrost: Updating datastore(s) for remote '{0}' ({1})", arg_remote_name, arg_remote_uri.AbsoluteUri);

            var store_interfaces = GetStoreInterfaces();
            StoreContainer store_infos = GetStores();

            foreach (KeyValuePair<string, StoreData> store_kvp in store_infos)
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

                foreach (var file_rev in file_revs)
                {
                    string rile_rev_string = string.Format("{0}:{1}", file_rev.Item1, file_rev.Item2);

                    Process git_proc = StartGit("show ", rile_rev_string);

                    if (git_proc == null) { return Failed; }

                    string bifrost_ver = git_proc.StandardOutput.ReadLine();
                    string file_sha = git_proc.StandardOutput.ReadLine();
//                    int file_size = int.Parse(git_proc.StandardOutput.ReadLine());

                    if (git_proc.WaitForExitCode() != 0) { return git_proc.ExitCode; }

                    string mangled_filename = string.Format("{0}-{1}.bin", file_sha, Path.GetFileName(bifrost_ver));
                    string mangled_filepath = Path.Combine(LocalStoreLocation, mangled_filename);

                    SyncResult result = store_interface.PushFile(mangled_filepath, store_uri, mangled_filename);

                    if (result == SyncResult.Failed)
                    {
                        LogLine("Bifrost: Failed to push file {0} to {1}", mangled_filepath, store_remote_uri.AbsolutePath);
                        return Failed;
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

                if (NoiseLevel == LogNoiseLevel.Normal)
                {
                    LogLine("Bifrost: {0} Copied, {1} Skipped", files_pushed, files_skipped + files_skipped_late);
                }
                else if (NoiseLevel == LogNoiseLevel.Loud)
                {
                    LogLine("Bifrost: {0} Copied, {1} Skipped, {2} Skipped late", files_pushed, files_skipped, files_skipped_late);
                }
            }

            
            return Succeeded;
        }

        /// <summary>
        /// Updates the local data store with files from a git commit/add
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
                return Failed;
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
                output_writer.WriteLine("git-bifrost {0}", Version);
                output_writer.WriteLine(file_hash);
                output_writer.WriteLine(file_stream.Length);
            }

            if (NoiseLevel == LogNoiseLevel.Loud)
            {
                LogLine("Name: {0}", arg_filepath);
                LogLine("Bytes: {0}", file_stream.Length);
                LogLine("Hash: {0}", file_hash);
            }

            string output_filename = String.Format("{0}-{1}.bin", file_hash, Path.GetFileName(arg_filepath));           

            WriteToLocalStore(file_stream, output_filename);

            return Succeeded;
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
                return Failed;
            }

            string expected_file_hash = null;
            int expected_file_size = -1;

            using (StreamReader input_reader = new StreamReader(Console.OpenStandardInput()))
            {
                input_reader.ReadLine(); // First line is currently unused "git-bifrost <version number>"   
                expected_file_hash = input_reader.ReadLine(); 
                expected_file_size = int.Parse(input_reader.ReadLine());
            }

            string input_filename = String.Format("{0}-{1}.bin", expected_file_hash, Path.GetFileName(arg_filepath));

            //
            // Collect up all the store locations in the config file
            //

			string[] loaded_stores = GitConfigGetRegex(@".*\.url$", ".gitbifrost");

            Uri[] data_stores = new Uri[1];//loaded_stores.Length + 1];
            data_stores[0] = new Uri(Path.Combine(Directory.GetCurrentDirectory(), LocalStoreLocation));

            int index = 1;
            foreach(string store_url in loaded_stores)
            {
                string[] store_tokens = store_url.Split(new char[] {' '}, 2);
                data_stores[index++] = new Uri(store_tokens[1]);
            }

            bool succeeded = false;

            var store_interfaces = GetStoreInterfaces();

            foreach (Uri store_uri in data_stores)
            {
                IStoreInterface store_interface = store_interfaces[store_uri.Scheme];

                if (store_interface != null)
                {
                    byte[] file_contents = store_interface.PullFile(store_uri, input_filename);

                    if (file_contents != null)
                    {
                        int loaded_file_size = file_contents.Length;

                        byte[] loaded_file_hash_bytes = SHA1.Create().ComputeHash(file_contents, 0, loaded_file_size);
                        string loaded_file_hash = BitConverter.ToString(loaded_file_hash_bytes).Replace("-", "");

                        if (NoiseLevel == LogNoiseLevel.Loud)
                        {
                            LogLine("Repo File: {0}", arg_filepath);
                            LogLine("Store Name: {0}", input_filename);
                            LogLine("Expect Hash: {0}", expected_file_hash);
                            LogLine("Loaded Hash: {0}", loaded_file_hash);
                            LogLine("Expected Size: {0}", expected_file_size);
                            LogLine("Loaded Size: {0}", loaded_file_size);
                        }

                        //
                        // Safety checking size and hash
                        //

                        if (expected_file_size != loaded_file_size)
                        {
                            LogLine("!!!ERROR!!!");
                            LogLine("File size missmatch with '{0}'", arg_filepath);
                            LogLine("Store '{0}'", store_uri.AbsoluteUri);
                            LogLine("Expected {0}, got {1}", expected_file_size, loaded_file_size);
                            continue;
                        }

                        if (loaded_file_hash != expected_file_hash)
                        {
                            LogLine("!!!ERROR!!!");
                            LogLine("File hash missmatch with '{0}'", arg_filepath);
                            LogLine("Store '{0}'", store_uri.AbsoluteUri);
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

            return succeeded ? Succeeded : Failed;
        }

        static int Help(string[] args)
        {
            LogLine("usage: git-bifrost <command> [options]");
            LogLine("");
            LogLine("Commands:");
            LogLine("   activate    installs git-bifrost into the current git repo");
            LogLine("   clone       like a normal git-clone but installs git-bifrost prior to checkout");
            LogLine("   init        like a normal git-init but installs git-bifrost as well");

            return Succeeded;
        }

        static int Clone(string[] args)
        {
            string clone_args = string.Join(" ", args, 1, args.Length - 1);

            Git("clone", "--no-checkout", clone_args);

            if (GitExitCode == 0)
            {
                string arg_directory = args.Length > 2 ? args[args.Length - 1] : Path.GetFileNameWithoutExtension(args[1]);

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
                return Failed;
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
                File.WriteAllLines(".gitattributes", new string[]
                {
                    "*.bmp filter=bifrost",
                    "*.dae filter=bifrost",
                    "*.fbx filter=bifrost",
                    "*.jpg filter=bifrost",
                    "*.ma filter=bifrost",
                    "*.max filter=bifrost",
                    "*.mb filter=bifrost",
                    "*.mp3 filter=bifrost",
                    "*.obj filter=bifrost",
                    "*.ogg filter=bifrost",
                    "*.png filter=bifrost",
                    "*.psd filter=bifrost",
                    "*.tga filter=bifrost",
                    "*.ttf filter=bifrost",
                    "*.ztl filter=bifrost",
                    "*.wav filter=bifrost",
                });
            }

            Console.WriteLine("Bifrost is now active");

            return Succeeded;
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
                File.WriteAllText(".git/hooks/pre-commit", "#!/bin/bash\ngit-bifrost hook-pre-commit");
                File.WriteAllText(".git/hooks/pre-push", "#!/bin/bash\ngit-bifrost hook-push \"$@\"");
                File.WriteAllText(".git/hooks/post-checkout", "#!/bin/bash\ngit-bifrost hook-sync \"$@\"");

#if __MonoCS__
                Syscall.chmod(".git/hooks/pre-commit", FilePermissions.ACCESSPERMS);
                Syscall.chmod(".git/hooks/pre-push", FilePermissions.ACCESSPERMS);
                Syscall.chmod(".git/hooks/post-checkout", FilePermissions.ACCESSPERMS);
#endif // __MonoCS__
            }
            catch
            {
                return Failed;
            }

            return Succeeded;
        }

        static string ReadToEscape(StreamReader reader)
        {
            var bytes = new List<byte>();

            int data = reader.Read();

            if (data == -1)
            {
                return null;
            }

            while (data != -1)
            {
                if (data != 0)
                {
                    bytes.Add((byte)data);

                    data = reader.Read();
                }
                else
                {
                    break;
                }
            }

            return Encoding.UTF7.GetString(bytes.ToArray());
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


        public static void LogLine(string format, params object[] arg)
        {
            try
            {
                LogWriter.WriteLine(format, arg);
                Console.Error.WriteLine(format, arg);
            }
            catch
            {

            }
        }

        static Dictionary<string, IStoreInterface> GetStoreInterfaces()
        {
            var store_interfaces = new Dictionary<string, IStoreInterface>();

            store_interfaces[Uri.UriSchemeFile] = new StoreFileSystem();

            return store_interfaces;
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

        static Process NewGit(string arguments)
        {
            Process process = new Process();
            ProcessStartInfo psi = process.StartInfo;
            psi.FileName = "git";
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.EnvironmentVariables["PATH"] = psi.EnvironmentVariables["PATH"].Replace(@"\", @"\\");
            
            return process;
        }

        static Process StartGit(params string[] arguments)
        {
            Process process = NewGit(string.Join(" ", arguments));

            if (process.Start())
            {
                return process;
            }

            LogLine("Failed to start git");
            return null;
        }


        static string Git(params string[] arguments)
        {
            Process process = NewGit(string.Join(" ", arguments));
            
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

    static class ProcessEx
    {
        public static int WaitForExitCode(this Process self)
        {
            self.WaitForExit();
            return self.ExitCode;
        }
    }
    
}