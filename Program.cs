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
        const int Kilobytes = 1024;
        const int Megabytes = 1024 * 1024;
        const int Version = 1;
        const int StartingBufferSize = 1024 * 1024;
        const string LocalStoreLocation = "./.git/bifrost/data";
        const string GitEmptySha = "0000000000000000000000000000000000000000";
        const int Succeeded = 0;
        const int Failed = 1;
        const int FirstFewBytes = 8000;

        const string BifrostProxyId = "git-bifrost-proxy-file";

        const string TextSizeThresholdKey = "repo.text-size-threshold";
        const int DefaultTextSizeThreshold = 5 * Megabytes;
        const string BinSizeThresholdKey = "repo.bin-size-threshold"; 
        const int DefaultBinSizeThreshold = 100 * Kilobytes;

        static LogNoiseLevel NoiseLevel = LogNoiseLevel.Loud;

        static int Main(string[] args)
        {
            int result = Succeeded;

            Dictionary<string, CommandDelegate> Commands = new Dictionary<string, CommandDelegate>(10);

            Commands["hook-sync"] = CmdHookSync;
            Commands["hook-push"] = CmdHookPush;
            Commands["hook-pre-commit"] = CmdHookPreCommit;
            Commands["filter-clean"] = CmdFilterClean;
            Commands["filter-smudge"] = CmdFilterSmudge;
            Commands["help"] = CmdHelp;
            Commands["clone"] = CmdClone;
            Commands["init"] = CmdInit;
            Commands["activate"] = CmdActivate;                


            string arg_command = args.Length > 0 ? args[0].ToLower() : null;

            if (arg_command != null)
            {
                if (NoiseLevel == LogNoiseLevel.Loud)
                {
                    LogLine("Bifrost: {0}", string.Join(" ", args));
                }
                // LogLine("Current Dir: {0}", Directory.GetCurrentDirectory());
                // LogLine("PATH: {0}", Environment.GetEnvironmentVariable("PATH"));

                CommandDelegate command;
                if (!Commands.TryGetValue(arg_command, out command))
                {
                    command = CmdHelp;
                }
                result = command(args);
            }
            else
            {
                result = CmdHelp(args);
            }

            return result;
        }

        static int CmdHookSync(string[] args)
        {
            return Succeeded;
        }
            
        static int CmdHookPush(string[] args)
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
            // Collect up all the file revisions we need to push to the stores
            //

            var file_revs = new List<Tuple<string, string>>();

            using (StreamReader stdin = new StreamReader(Console.OpenStandardInput()))
            {
                string push_info = null;
                while ((push_info = stdin.ReadLine()) != null)
                {
                    // LogLine("Bifrost: push info ({0})", push_info);

                    string[] push_tokens = push_info.Split(' ');

                    //string local_ref = push_tokens[0];
                    string local_sha = push_tokens[1];
                    //string remote_ref = push_tokens[2];
                    string remote_sha = push_tokens[3];

                    if (local_sha != GitEmptySha)
                    {
                        var rev_ids = new List<string>();

                        // Get the individual revision ids between local and the remote commit id's
                        {
                            string rev_list_range = remote_sha != GitEmptySha ? string.Format("{0}..{1}", remote_sha, local_sha) : local_sha;

                            Process git_proc = StartGit("rev-list", rev_list_range);

                            if (git_proc != null)
                            { 
                                string rev_line = null;

                                while ((rev_line = git_proc.StandardOutput.ReadLine()) != null)
                                {
                                    rev_ids.Add(rev_line);
                                }

                                if (git_proc.WaitForExitFail())
                                {
                                    return git_proc.ExitCode;
                                }
                            }
                            else
                            { 
                                return Failed; 
                            }
                        }

                        foreach (string revision in rev_ids)
                        {
                            // Get files modified in this revision
                            // format: file_status<null>file_name<null>file_status<null>file_name<null>
                            Process git_proc = StartGit("diff-tree --no-commit-id --name-status -r -z", revision); 
                            if (git_proc != null)
                            {
                                string status = null;
                                while ((status = ReadToNull(git_proc.StandardOutput)) != null)
                                {
                                    if (status == "X")
                                    {
                                        LogLine("Bifrost: According to git something has gone wrong.");
                                        return Failed;
                                    }

                                    string file = ReadToNull(git_proc.StandardOutput);
                                    
                                    // Skip files that have been deleted
                                    if (status != "D")
                                    {
                                        try
                                        {
                                            string filter_tag = GetFilterAttribute(file);

                                            if (filter_tag == "bifrost")
                                            {
                                                file_revs.Add(new Tuple<string, string>(revision, file));
                                            }
                                        }
                                        catch
                                        {
                                            LogLine("Failed to get file from diff-tree.");
                                            return Failed;
                                        }
                                    }
                                }

                                if (git_proc.WaitForExitCode() != 0)
                                {
                                    return git_proc.ExitCode;
                                }
                            }
                            else
                            { 
                                return Failed; 
                            }
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
                LogLine("Bifrost: Local store directory is missing but should have files.");
                foreach (var file_rev in file_revs)
                {
                    LogLine("    {0}:{1}", file_rev.Item1.Substring(0, 7), file_rev.Item2);
                }
                return Failed;
            }

            if (file_revs.Count == 0)
            {
                LogLine("Bifrost: no files to push.");
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
                    // Read in the proxy for this revision of the file
                    string rile_rev_string = string.Format("{0}:{1}", file_rev.Item1, file_rev.Item2);

                    Process git_proc = StartGit(string.Format("show \"{0}\"", rile_rev_string));
                    if (git_proc != null)
                    {
                        string bifrost_ver = git_proc.StandardOutput.ReadLine();

                        if (!bifrost_ver.StartsWith(BifrostProxyId))
                        {
                            LogLine("Bifrost: Expected '{0}' to be a bifrost proxy file but got something else.", file_rev.Item2);
                            return Failed;
                        }

                        string file_sha = git_proc.StandardOutput.ReadLine();
                        int.Parse(git_proc.StandardOutput.ReadLine()); // File size (unused here)

                        if (git_proc.WaitForExitFail())
                        {
                            return git_proc.ExitCode;
                        }

                        // Build the mangled name
                        string mangled_filename = string.Format("{0}.bin", file_sha);
                        string mangled_filepath = Path.Combine(LocalStoreLocation, mangled_filename);

                        SyncResult result = SyncResult.Failed;

                        if (File.Exists(mangled_filepath))
                        {
                            result = store_interface.PushFile(mangled_filepath, store_uri, mangled_filename);
                        }
                        else
                        {
                            LogLine("Bifrost: Failed to find revision '{0}' of '{1}' in local store.", file_rev.Item1, file_rev.Item2);                       
                        }

                        if (result == SyncResult.Failed)
                        {
                            LogLine("Bifrost: Failed to push file {0} to {1}.", mangled_filepath, store_remote_uri.AbsolutePath);
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
                    else 
                    { 
                        return Failed; 
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

        // Verify that the staged files are reasonable to fit in a git repo filtered by bifrost
        static int CmdHookPreCommit(string[] args)
        {
            Process git_proc = StartGit("diff --name-only --cached");
            if (git_proc == null) { return Failed; }

            bool succeeded = true;

            string file = null;
            while ((file = git_proc.StandardOutput.ReadLine()) != null)
            {
                bool bifrost_filtered = GetFilterAttribute(file) == "bifrost";

                Process git_show_proc = StartGit(string.Format("show :\"{0}\"", file));
                if (git_show_proc != null)
                {
                    if (bifrost_filtered)
                    {
                        // If the file is already filtered by bifrost, make sure that the staged file is in fact 
                        // a bifrost proxy file if it isn't the user needs to restage the file in order to allow the clean filter to run.
                        // This is likely to happen when you modify your git attributes to include a file in bifrost.

                        string proxyfile_tag = git_show_proc.StandardOutput.ReadLine();

                        if (!proxyfile_tag.StartsWith(BifrostProxyId))
                        {
                            LogLine("Bifrost: You need to restage '{0}'.", file);

                            succeeded = false;
                        }
                    }
                    else
                    {
                        bool is_binary = GetAttributeSet(file, "binary");
                        int size = 0;

                        // Count the bytes & see if this is a binary file
                        int file_byte = 0;
                        while ((file_byte = git_show_proc.StandardOutput.Read()) != -1)
                        {
                            if (!is_binary && file_byte == 0)
                            {
                                is_binary = true;
                            }
                            ++size;
                        }

                        if (git_show_proc.WaitForExitFail())
                        {
                            return git_show_proc.ExitCode;
                        }

                        int threshold;
                        if (is_binary)
                        {
                            threshold = GitConfigGetInt(BinSizeThresholdKey, DefaultBinSizeThreshold, ".gitbifrost");
                        }
                        else
                        {
                            threshold = GitConfigGetInt(TextSizeThresholdKey, DefaultTextSizeThreshold, ".gitbifrost");
                        }

                        if (threshold > -1 && size > threshold)
                        {
                            succeeded = false;
                            string type = is_binary ? "binary" : "text";

                            LogLine("Bifrost: The {0} file '{1}' is too big ({2} bytes)\t. Add a filter for the file or extention with bifrost and restage.", 
                                type, file, size);
                        }
                    }
                }
                else
                {
                    return Failed;
                }

            }

            if (git_proc.WaitForExitFail())
            {
                LogLine("Git failed");
                return git_proc.ExitCode;
            }

            if (!succeeded)
            {
                LogLine("Bifrost: Aborting commit.");
            }

            return succeeded ? Succeeded : Failed;
        }

        /*
        * A filter driver consists of a clean command and a smudge command, either of which can be left unspecified. 
        * Upon checkout, when the smudge command is specified, the command is fed the blob object from its standard input, 
        * and its standard output is used to update the worktree file. 
        * Similarly, the clean command is used to convert the contents of worktree file upon checkin.
        */   
        static int CmdFilterClean(string[] args)
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

            bool is_bifrost_proxy = false;

            {
                StreamReader reader = new StreamReader(file_stream);
                is_bifrost_proxy = reader.ReadLine().StartsWith(BifrostProxyId);

                file_stream.Position = 0;
            }

            if (is_bifrost_proxy)
            {
                // Acording to the git docs, it's possible for a filter to run on the same file multiple times so we need to handle
                // the case where a proxy is passed in for a clean at some point. So far I've not seen this scenario occur, 
                // so until it's clear when and why this could happen in our setup, I'll leave this fail condition here to catch it.
                LogLine("Bifrost: File '{0}' is already bifrost proxy, why are you cleaning again?", arg_filepath);

                return Failed;
            }

            byte[] file_hash_bytes = SHA1.Create().ComputeHash(file_stream);
            string file_hash = BitConverter.ToString(file_hash_bytes).Replace("-", "");

            file_stream.Position = 0;

            // Give git the proxy instead of the actual file
            using (StreamWriter output_writer = new StreamWriter(Console.OpenStandardOutput()))
            {
                output_writer.WriteLine("{0} {1}", BifrostProxyId, Version);
                output_writer.WriteLine(file_hash);
                output_writer.WriteLine(file_stream.Length);
            }

            if (NoiseLevel == LogNoiseLevel.Loud)
            {
                LogLine(" Name: {0}", arg_filepath);
                LogLine(" Hash: {0}", file_hash);
                LogLine("Bytes: {0}", file_stream.Length);
            }

            // Dump the real file into the local store
            string output_filename = String.Format("{0}.bin", file_hash);
            WriteToLocalStore(file_stream, output_filename);

            return Succeeded;
        }

       /*
        * A filter driver consists of a clean command and a smudge command, either of which can be left unspecified. 
        * Upon checkout, when the smudge command is specified, the command is fed the blob object from its standard input, 
        * and its standard output is used to update the worktree file. 
        * Similarly, the clean command is used to convert the contents of worktree file upon checkin.
        */
        static int CmdFilterSmudge(string[] args)
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

            string expected_file_hash = null;
            int expected_file_size = -1;

            using (StreamReader input_reader = new StreamReader(Console.OpenStandardInput()))
            {
                string bifrost_ver = input_reader.ReadLine(); // "git-bifrost-proxy-file <version number>"

                if (!bifrost_ver.StartsWith(BifrostProxyId))
                {
                    LogLine("Bifrost: '{0}' is not a bifrost proxy file but is being smudged.", arg_filepath);
                    return Failed;
                }

                expected_file_hash = input_reader.ReadLine();
                expected_file_size = int.Parse(input_reader.ReadLine());
            }

            string input_filename = String.Format("{0}.bin", expected_file_hash);

            //
            // Collect up all the store locations in the config file
            //

			string[] loaded_stores = GitConfigGetRegex(@".*\.url$", ".gitbifrost");

            Uri[] data_stores = new Uri[loaded_stores.Length + 1];

            // Include the local store first
            data_stores[0] = new Uri(Path.Combine(Directory.GetCurrentDirectory(), LocalStoreLocation)); 

            // Load the other stores
            {
                int index = 1;
                foreach (string store_url in loaded_stores)
                {
                    string[] store_tokens = store_url.Split(new char[] { ' ' }, 2);
                    data_stores[index++] = new Uri(store_tokens[1]);
                }
            }


            bool succeeded = false;

            var store_interfaces = GetStoreInterfaces();

            // Walk through all the stores/interfaces and attempt to retrevie a matching file from any of them
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
                            LogLine("    Repo File: {0}", arg_filepath);
                            LogLine("   Store Name: {0}", input_filename);
                            LogLine("  Expect Hash: {0}", expected_file_hash);
                            LogLine("  Loaded Hash: {0}", loaded_file_hash);
                            LogLine("Expected Size: {0}", expected_file_size);
                            LogLine("  Loaded Size: {0}", loaded_file_size);
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
                            LogLine("Will try another store, but this one should be tested for integrity");
                            continue;
                        }

                        if (loaded_file_hash != expected_file_hash)
                        {
                            LogLine("!!!ERROR!!!");
                            LogLine("File hash missmatch with '{0}'", arg_filepath);
                            LogLine("Store '{0}'", store_uri.AbsoluteUri);
                            LogLine("Expected {0}, got {1}", expected_file_hash, loaded_file_hash);
                            LogLine("Will try another store, but this one should be tested for integrity");
                            continue;
                        }

                        //
                        // Finally write file
                        //

                        // Put a copy in the local store
                        WriteToLocalStore(new MemoryStream(file_contents), input_filename);

                        // Give it to git to update the working directory
                        using (Stream stdout = Console.OpenStandardOutput())
                        {
                            stdout.Write(file_contents, 0, loaded_file_size);
                        }

                        succeeded = true;

                        break;
                    }
                    else
                    {
                        LogLine("Bifrost: Store {0} does not contain file.", store_uri.AbsoluteUri);
                        LogLine("    Repo File: {0}", arg_filepath);
                        LogLine("   Store Name: {0}", input_filename);
                    }
                }
                else
                {
                    LogLine("Unrecognized store type in '{0}'.", store_uri.ToString());
                }
            }

            if (!succeeded)
            {
                LogLine("Bifrost: Failed to get file '{0}'.", arg_filepath);
            }

            return succeeded ? Succeeded : Failed;
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

                    LogLine("Bifrost: Local store updated with {0}.", filepath);
                }
            }
            else
            {
                if (NoiseLevel == LogNoiseLevel.Loud)
                {
                    LogLine("Bifrost: Local store skipped");
                }
            }
        }

        static int CmdHelp(string[] args)
        {
            LogLine("usage: git-bifrost <command> [options]");
            LogLine("");
            LogLine("Commands:");
            LogLine("   activate    installs git-bifrost into the current git repo");
            LogLine("   clone       like a normal git-clone but installs git-bifrost prior to checkout");
            LogLine("   init        like a normal git-init but installs git-bifrost as well");

            return Succeeded;
        }

        static int CmdClone(string[] args)
        {
            string clone_args = string.Join(" ", args, 1, args.Length - 1);

            Process git_clone_proc = StartGit("clone", "--no-checkout", clone_args);

            if (git_clone_proc.WaitForExitSucceed())
            {
                string arg_directory = args.Length > 2 ? args[args.Length - 1] : Path.GetFileNameWithoutExtension(args[1]);

                Directory.SetCurrentDirectory(arg_directory);

                InstallBifrost();


                Process git_checkout_proc = StartGit("checkout");
                if (git_checkout_proc.WaitForExitSucceed())
                {
                    return git_checkout_proc.ExitCode;
                }
            }

            return Failed;
        }

        static int CmdInit(string[] args)
        {
            return InstallBifrost();
        }

        static int CmdActivate(string[] args)
        {
            InstallBifrost();

            // Generate sample config

            if (args.Contains("--test-config", StringComparer.CurrentCultureIgnoreCase))
            {
                GitConfigSet(TextSizeThresholdKey, DefaultTextSizeThreshold.ToString(), ".gitbifrost");
                GitConfigSet(BinSizeThresholdKey, DefaultBinSizeThreshold.ToString(), ".gitbifrost");

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
            }

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
            if (!Directory.Exists(".git"))
            {
                Console.WriteLine("No git repository at '{0}'", Directory.GetCurrentDirectory());
                return Failed;
            }
                
            if (!GitConfigSet("filter.bifrost.clean", "git-bifrost filter-clean %f"))
            { 
                return Failed; 
            }

            if (!GitConfigSet("filter.bifrost.smudge", "git-bifrost filter-smudge %f"))
            { 
                return Failed; 
            }

            if (!GitConfigSet("filter.bifrost.required", "true"))
            { 
                return Failed; 
            }

            try
            {
                File.WriteAllText(".git/hooks/pre-commit", "#!/bin/bash\ngit-bifrost hook-pre-commit");
                File.WriteAllText(".git/hooks/pre-push", "#!/bin/bash\ngit-bifrost hook-push \"$@\"");
                //File.WriteAllText(".git/hooks/post-checkout", "#!/bin/bash\ngit-bifrost hook-sync \"$@\"");

#if __MonoCS__
                Syscall.chmod(".git/hooks/pre-commit", FilePermissions.ACCESSPERMS);
                Syscall.chmod(".git/hooks/pre-push", FilePermissions.ACCESSPERMS);
                //Syscall.chmod(".git/hooks/post-checkout", FilePermissions.ACCESSPERMS);
#endif // __MonoCS__
            }
            catch
            {
                return Failed;
            }

            return Succeeded;
        }

        public static void LogLine(string format, params object[] arg)
        {
            try
            {
//                LogWriter.WriteLine(format, arg);
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
            
        // Returns true if set succeeded
        static bool GitConfigSet(string key, string value, string file = null)
        {       
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            string command = string.Format("config {0} {1} \"{2}\"", fileArg, key, value);
            
            return StartGit(command).WaitForExitSucceed();
        }

        static string[] GitConfigGetRegex(string key, string file)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            Process git_proc = StartGit("config --get-regexp", fileArg, key);

            if(git_proc == null)
            {
                return new string[0];
            }

            List<string> lines = new List<string>();

            string line = git_proc.StandardOutput.ReadLine();
            while(line != null)
            {
                lines.Add(line);

                line = git_proc.StandardOutput.ReadLine();
            }

            return lines.ToArray();
        }

        static int GitConfigGetInt(string key, int defaultValue, string file)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            Process git_proc = StartGit("config --null --get", fileArg, key);

            if (git_proc == null)
            {
                return defaultValue;
            }

            string value_str = ReadToNull(git_proc.StandardOutput);

            if (git_proc.WaitForExitCode() != 0)
            {
                return defaultValue;
            }

            int value;
            if (int.TryParse(value_str, out value))
            {
                return value;
            }

            return defaultValue;
        }

        static string GetFilterAttribute(string file)
        {
            Process git_check_attr = StartGit(string.Format("check-attr -z filter \"{0}\"", file));
            if (git_check_attr == null) { return null; }

            ReadToNull(git_check_attr.StandardOutput); // File name
            ReadToNull(git_check_attr.StandardOutput); // Attribute (filter)
            string filter_tag = ReadToNull(git_check_attr.StandardOutput);

            if (git_check_attr.WaitForExitCode() != 0) { return null; }

            return filter_tag;
        }

        static bool GetAttributeSet(string file, string attribute)
        {
            Process git_check_attr = StartGit(string.Format("check-attr -z {0} \"{1}\"", attribute, file));
            if (git_check_attr == null) { return false; }

            ReadToNull(git_check_attr.StandardOutput); // File name
            ReadToNull(git_check_attr.StandardOutput); // attribute
            string value = ReadToNull(git_check_attr.StandardOutput);

            if (git_check_attr.WaitForExitCode() != 0) { return false; }

            return value == "set";
        }

        static string ReadToNull(StreamReader reader)
        {
            MemoryStream buffer = new MemoryStream(Kilobytes);

            int data = reader.Read();

            if (data == -1)
            {
                return null;
            }

            while (data != -1)
            {
                if (data != 0)
                {
                    buffer.WriteByte((byte)data);

                    data = reader.Read();
                }
                else
                {
                    break;
                }
            }

            return Encoding.UTF7.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        static Process StartGit(params string[] arguments)
        {
            Process process = new Process();
            ProcessStartInfo psi = process.StartInfo;
            psi.FileName = "git";
            psi.Arguments = string.Join(" ", arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.EnvironmentVariables["PATH"] = psi.EnvironmentVariables["PATH"].Replace(@"\", @"\\"); // Somehow windows needs this?

            if (process.Start())
            {
                return process;
            }

            LogLine("Failed to start git.");
            return null;
        }
    }

    static class ProcessEx
    {
        public static int WaitForExitCode(this Process self)
        {
            self.WaitForExit();
            return self.ExitCode;
        }

        public static bool WaitForExitFail(this Process self)
        {
            return WaitForExitCode(self) != 0;
        }

        public static bool WaitForExitSucceed(this Process self)
        {
            return WaitForExitCode(self) == 0;
        }
    }
    
}