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
        Normal = 0,
        Loud = 1,
        Debug = 2
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

        static readonly char[] NullChar = new char[] { '\0' };

        static LogNoiseLevel NoiseLevel = LogNoiseLevel.Normal;

        static int Main(string[] args)
        {
            int result = Succeeded;

            Enum.TryParse(Environment.GetEnvironmentVariable("GITBIFROST_VERBOSITY"), true, out NoiseLevel);

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
                LogLine(LogNoiseLevel.Debug, "Bifrost: {0}", string.Join(" ", args));
                LogLine(LogNoiseLevel.Debug, "Bifrost: Current Dir: {0}", Directory.GetCurrentDirectory());
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
            string arg_remote_url = args[2];

            LogLine(LogNoiseLevel.Normal, "Bifrost: Updating stores");


            //
            // Collect up all the file revisions we need to push to the stores
            //

            var file_revs = new List<Tuple<string, string>>();

            using (StreamReader stdin = new StreamReader(Console.OpenStandardInput()))
            {
                LogLine(LogNoiseLevel.Debug, "Bifrost: Building list of file revivions to push to store.");

                string push_info = null;
                while ((push_info = stdin.ReadLine()) != null)
                {
                    LogLine(LogNoiseLevel.Debug, "Bifrost: push info ({0})", push_info);

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

                            string rev_line = null;

                            while ((rev_line = git_proc.StandardOutput.ReadLine()) != null)
                            {
                                rev_ids.Add(rev_line);
                            }

                            if (git_proc.WaitForExitFail(true))
                            {
                                return git_proc.ExitCode;
                            }
                        }

                        LogLine(LogNoiseLevel.Debug, "Bifrost: Iterating revisions");


                        foreach (string revision in rev_ids)
                        {
                            LogLine(LogNoiseLevel.Debug, "Bifrost: Revision {0}", revision);

                            // Get files modified in this revision
                            // format: file_status<null>file_name<null>file_status<null>file_name<null>
                            Process git_proc = StartGit("diff-tree --no-commit-id --name-status -r -z", revision);

                            string proc_data = git_proc.StandardOutput.ReadToEnd();
                            if (git_proc.WaitForExitFail(true))
                            {
                                return git_proc.ExitCode;
                            }

                            string[] revision_files = proc_data.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);
                            LogLine(LogNoiseLevel.Debug, "Bifrost: {0} file(s)", revision_files.Length);

//                            LogLine(LogNoiseLevel.Debug, "Revision Count {0}", revision_files.Length);

                            for (int i = 0; i < revision_files.Length; i += 2)
                            {
                                string status = revision_files[i];
                                string file = revision_files[i + 1];

                                if (status == "X")
                                {
                                    LogLine(LogNoiseLevel.Normal, "Bifrost: According to git something has gone wrong. Aborting push.");
                                    return Failed;
                                }

                                // Skip files that have been deleted
                                if (status != "D")
                                {
                                    try
                                    {
                                        if (GetFilterAttribute(file) == "bifrost")
                                        {
                                            LogLine(LogNoiseLevel.Debug, "Will push '{0}'", file);
                                            file_revs.Add(new Tuple<string, string>(revision, file));
                                        }
                                    }
                                    catch
                                    {
                                        LogLine(LogNoiseLevel.Normal, "Failed to get file from diff-tree.");
                                        return Failed;
                                    }
                                }
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
                LogLine(LogNoiseLevel.Normal, "Bifrost: Local store directory is missing but should have files.");
                foreach (var file_rev in file_revs)
                {
                    LogLine(LogNoiseLevel.Normal, "    {0}:{1}", file_rev.Item1.Substring(0, 7), file_rev.Item2);
                }
                return Failed;
            }

            if (file_revs.Count == 0)
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: No files to push.");
                return Succeeded;
            }

            //
            // Update the stores with the files
            //

            LogLine(LogNoiseLevel.Normal, "Bifrost: Updating store(s) for remote '{0}' ({1})", arg_remote_name, arg_remote_url);

            var store_interfaces = GetStoreInterfaces();
            var store_infos = GetStores();

            LogLine(LogNoiseLevel.Debug, "Bifrost: Available stores: {0}", store_infos.Count);

            int primaries_updated = 0;

            foreach (var store_data in store_infos)
            {
                string remote_url_string;
                if (!store_data.TryGetValue("remote", out remote_url_string))
                {
                    LogLine(LogNoiseLevel.Debug, "Bifrost: Could not find remote url for store mapping '{0}'", store_data["name"]);
                    continue;
                }

                remote_url_string = Path.GetFullPath(remote_url_string);

                if (remote_url_string != arg_remote_url)
                {
                    LogLine(LogNoiseLevel.Debug, "Bifrost: Stores {0} & {1} are not the same", remote_url_string, arg_remote_url);
                    continue;
                }

                Uri store_uri = new Uri(store_data["url"]);

                IStoreInterface store_interface = store_interfaces[store_uri.Scheme];
                if (store_interface == null || !store_interface.OpenStore(store_uri, store_data))
                {
                    LogLine(LogNoiseLevel.Loud, "Couldn't open store '{1}' ({0})", store_uri, store_data["name"]);
                    continue;
                }

                LogLine(LogNoiseLevel.Normal, "Bifrost: Updating store: '{0}'", store_uri.AbsoluteUri);

                int files_pushed = 0;
                int files_skipped = 0;
                int files_skipped_late = 0;

                int file_index = 0;
                foreach (var file_rev in file_revs)
                {
                    Log(LogNoiseLevel.Normal, GetProgressString("Bifrost: Updating store", file_index, file_revs.Count, "\r"));
                    ++file_index;

                    // Read in the proxy for this revision of the file
                    string rile_rev_string = string.Format("{0}:{1}", file_rev.Item1, file_rev.Item2);

                    Process git_proc = StartGit(string.Format("show \"{0}\"", rile_rev_string));

                    string bifrost_ver = git_proc.StandardOutput.ReadLine();
                    string file_sha = git_proc.StandardOutput.ReadLine();
                    string file_size_str = git_proc.StandardOutput.ReadLine(); // File size (unused here)

                    if (git_proc.WaitForExitFail(true))
                    {
                        store_interface.CloseStore();
                        return git_proc.ExitCode;
                    }

                    if (!bifrost_ver.StartsWith(BifrostProxyId))
                    {
                        store_interface.CloseStore();
                        LogLine(LogNoiseLevel.Normal, "Bifrost: Expected '{0}' to be a bifrost proxy file but got something else.", file_rev.Item2);
                        return Failed;
                    }

                    // Build the mangled name
                    string filename = GetFilePath(file_sha);
                    string filepath = Path.Combine(LocalStoreLocation, filename);

                    SyncResult result = SyncResult.Failed;

                    if (File.Exists(filepath))
                    {
                        result = store_interface.PushFile(filepath, store_uri, filename);
                    }
                    else
                    {
                        LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to find revision '{0}' of '{1}' in local store.", file_rev.Item1, file_rev.Item2);
                    }

                    if (result == SyncResult.Failed)
                    {
                        store_interface.CloseStore();
                        LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to push file {0} to {1}.", filepath, store_uri.LocalPath);
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


                store_interface.CloseStore();

                LogLine(LogNoiseLevel.Normal, GetProgressString("Bifrost: Updating store", file_index, file_revs.Count, ", done."));

                if (IsPrimaryStore(store_data))
                {
                    ++primaries_updated;
                }

                LogLine(LogNoiseLevel.Normal, "Bifrost: {0} Copied, {1} Skipped", files_pushed, files_skipped + files_skipped_late);
                LogLine(LogNoiseLevel.Loud, "Bifrost: {0} Copied, {1} Skipped, {2} Skipped late", files_pushed, files_skipped, files_skipped_late);
            }

            if (primaries_updated <= 0)
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to update a primary store for this remote.");
                return Failed;
            }


            return Succeeded;
        }

        static bool IsPrimaryStore(Dictionary<string, string> store_data)
        {
            string value;
            if (store_data.TryGetValue("primary", out value))
            {
                return bool.Parse(value);
            }

            return false;
        }

        // Verify that the staged files are reasonable to fit in a git repo filtered by bifrost
        static int CmdHookPreCommit(string[] args)
        {
            string[] staged_files = null;

//            LogLine(LogNoiseLevel.Normal, "Bifrost: Doing pre-commit checks...");


            // Load get a list of the staged files
            {
                Process git_proc = StartGit("diff --name-only --cached -z");

                string proc_output = git_proc.StandardOutput.ReadToEnd();

                if (git_proc.WaitForExitFail(true))
                {
                    LogLine(LogNoiseLevel.Normal, "Git failed");
                    return git_proc.ExitCode;
                }

                staged_files = proc_output.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);
            }

            char[] scratch_buffer = new char[(64 * Kilobytes) / sizeof(char)];

            bool succeeded = true;
            bool files_over_limit = false;
            bool files_need_restaging = false;

            int file_number = 0;

            foreach (string file in staged_files)
            {
                bool bifrost_filtered = GetFilterAttribute(file) == "bifrost";

                Log(LogNoiseLevel.Normal, GetProgressString("Bifrost: Validating staged files", file_number, staged_files.Length, "\r"));

                ++file_number;

                using (Process git_proc = StartGit(string.Format("show :\"{0}\"", file)))
                {
                    if (bifrost_filtered)
                    {
                        // If the file is already filtered by bifrost, make sure that the staged file is in fact
                        // a bifrost proxy file if it isn't the user needs to restage the file in order to allow the clean filter to run.
                        // This is likely to happen when you modify your git attributes to include a file in bifrost.

                        int chars_read = git_proc.StandardOutput.ReadBlock(scratch_buffer, 0, BifrostProxyId.Length);

                        string proxyfile_tag = new string(scratch_buffer, 0, chars_read);

                        if (proxyfile_tag != BifrostProxyId)
                        {
                            LogLine(LogNoiseLevel.Loud, "Bifrost: Needs restaging '{0}'.", file);
                            succeeded = false;
                            files_need_restaging = true;
                        }

                        LogLine(LogNoiseLevel.Debug, "Bifrost: Filtered '{0}'.", file);
                    }
                    else
                    {
                        LogLine(LogNoiseLevel.Debug, "Bifrost: Unfiltered '{0}'.", file);

                        int chars_read = git_proc.StandardOutput.ReadBlock(scratch_buffer, 0, scratch_buffer.Length);
                        int size = chars_read * sizeof(char);

                        bool is_binary = GetAttributeSet(file, "binary");

                        // Just becasue there isn't an attribute saying it's binary, that doesn't mean it isn't.
                        // Scan the first block to see if it is.

                        if (!is_binary)
                        {
                            for (int i = 0; i < chars_read; ++i)
                            {
                                char c = scratch_buffer[i];
                                if ((byte)c == 0 || (byte)(c >> 8) == 0)
                                {
                                    is_binary = true;
                                    break;
                                }
                            }
                        }

                        do
                        {
                            chars_read = git_proc.StandardOutput.ReadBlock(scratch_buffer, 0, scratch_buffer.Length);
                            size += chars_read * sizeof(char);
                        }
                        while(chars_read > 0);

                        if (git_proc.WaitForExitFail())
                        {
                            return git_proc.ExitCode;
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
                            string type = is_binary ? "Binary" : "Text";
                            LogLine(LogNoiseLevel.Normal, "Bifrost: {0} file too big '{1}' ({2} bytes).", type, file, size);

                            files_over_limit = true;
                        }
                    }
                }
            }

            LogLine(LogNoiseLevel.Normal, GetProgressString("Bifrost: Running pre-commit checks", staged_files.Length, staged_files.Length, ", done."));

            if (!succeeded)
            {
                if (files_over_limit)
                {
                    LogLine(LogNoiseLevel.Normal, "Bifrost: Add a filter for the file/extention or bump up your limits and don't forget to restage");
                    LogLine(LogNoiseLevel.Normal, "Bifrost: If you have updated your .gitattributes, make sure it has been staged for this commit.");
                }

                if (files_need_restaging)
                {
                    LogLine(LogNoiseLevel.Normal, "Bifrost: Files were just added to be filtered by git-bifrost. You need to restage before you can commit.");
                }

                LogLine(LogNoiseLevel.Normal, "Bifrost: Aborting commit.");
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
                LogLine(LogNoiseLevel.Normal, "Bifrost: File '{0}' is already bifrost proxy, why are you cleaning again?", arg_filepath);

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


            LogLine(LogNoiseLevel.Loud, " Name: {0}", arg_filepath);
            LogLine(LogNoiseLevel.Loud, " Hash: {0}", file_hash);
            LogLine(LogNoiseLevel.Loud, "Bytes: {0}", file_stream.Length);


            // Dump the real file into the local store
            string output_filename = GetFilePath(file_hash);


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
                    LogLine(LogNoiseLevel.Normal, "Bifrost: '{0}' is not a bifrost proxy file but is being smudged.", arg_filepath);
                    return Failed;
                }

                expected_file_hash = input_reader.ReadLine();
                expected_file_size = int.Parse(input_reader.ReadLine());
            }

            string input_filename = GetFilePath(expected_file_hash);


            bool succeeded = false;

            var store_interfaces = GetStoreInterfaces();

            var stores = GetStores();

            LogLine(LogNoiseLevel.Debug, "Bifrost: Store count: {0}", stores.Count);

            // Walk through all the stores/interfaces and attempt to retrevie a matching file from any of them
            foreach (var store in stores)
            {
                // It's perfectly normal for some urls to be invalid uris becasue
                // different plaforms have different rules for file system uris
                Uri store_uri;
                if (!Uri.TryCreate(store["url"], UriKind.Absolute, out store_uri))
                {
                    continue;
                }

                IStoreInterface store_interface = store_interfaces[store_uri.Scheme];

                if (store_interface != null)
                {
                    if (store_interface.OpenStore(store_uri, store))
                    {
                        byte[] file_contents = store_interface.PullFile(store_uri, input_filename);

                        store_interface.CloseStore();

                        if (file_contents != null)
                        {
                            int loaded_file_size = file_contents.Length;

                            byte[] loaded_file_hash_bytes = SHA1.Create().ComputeHash(file_contents, 0, loaded_file_size);
                            string loaded_file_hash = BitConverter.ToString(loaded_file_hash_bytes).Replace("-", "");


                            LogLine(LogNoiseLevel.Loud, "    Repo File: {0}", arg_filepath);
                            LogLine(LogNoiseLevel.Loud, "   Store Name: {0}", input_filename);
                            LogLine(LogNoiseLevel.Loud, "  Expect Hash: {0}", expected_file_hash);
                            LogLine(LogNoiseLevel.Loud, "  Loaded Hash: {0}", loaded_file_hash);
                            LogLine(LogNoiseLevel.Loud, "Expected Size: {0}", expected_file_size);
                            LogLine(LogNoiseLevel.Loud, "  Loaded Size: {0}", loaded_file_size);

                            //
                            // Safety checking size and hash
                            //

                            if (expected_file_size != loaded_file_size)
                            {
                                LogLine(LogNoiseLevel.Normal, "!!!ERROR!!!");
                                LogLine(LogNoiseLevel.Normal, "File size missmatch with '{0}'", arg_filepath);
                                LogLine(LogNoiseLevel.Normal, "Store '{0}'", store_uri.AbsoluteUri);
                                LogLine(LogNoiseLevel.Normal, "Expected {0}, got {1}", expected_file_size, loaded_file_size);
                                LogLine(LogNoiseLevel.Normal, "Will try another store, but this one should be tested for integrity");
                                continue;
                            }

                            if (loaded_file_hash != expected_file_hash)
                            {
                                LogLine(LogNoiseLevel.Normal, "!!!ERROR!!!");
                                LogLine(LogNoiseLevel.Normal, "File hash missmatch with '{0}'", arg_filepath);
                                LogLine(LogNoiseLevel.Normal, "Store '{0}'", store_uri.AbsoluteUri);
                                LogLine(LogNoiseLevel.Normal, "Expected {0}, got {1}", expected_file_hash, loaded_file_hash);
                                LogLine(LogNoiseLevel.Normal, "Will try another store, but this one should be tested for integrity");
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
                            LogLine(LogNoiseLevel.Loud, "Bifrost: Store {0} does not contain file.", store_uri.AbsoluteUri);
                            LogLine(LogNoiseLevel.Loud, "    Repo File: {0}", arg_filepath);
                            LogLine(LogNoiseLevel.Loud, "   Store Name: {0}", input_filename);
                        }
                    }
                }
                else
                {
                    LogLine(LogNoiseLevel.Normal, "Unrecognized store type in '{0}'.", store_uri.ToString());
                }
            }

            if (!succeeded)
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to get file '{0}'.", arg_filepath);
            }

            return succeeded ? Succeeded : Failed;
        }

        static void WriteToLocalStore(Stream file_stream, string filename)
        {
            string filepath = Path.Combine(LocalStoreLocation, filename);

            string filedir = Path.GetDirectoryName(filepath);

            if (!File.Exists(filepath))
            {
                if (!Directory.Exists(filedir))
                {
                    Directory.CreateDirectory(filedir);
                }

                using (FileStream output_stream = new FileStream(filepath, FileMode.Create, FileAccess.Write))
                {
                    file_stream.CopyTo(output_stream);

                    LogLine(LogNoiseLevel.Loud, "Bifrost: Local store updated with '{0}'.", filepath);
                }
            }
            else
            {
                LogLine(LogNoiseLevel.Loud, "Bifrost: Local store update skipped");
            }
        }

        static int CmdHelp(string[] args)
        {
            LogLine(LogNoiseLevel.Normal, "usage: git-bifrost <command> [options]");
            LogLine(LogNoiseLevel.Normal, "");
            LogLine(LogNoiseLevel.Normal, "Commands:");
            LogLine(LogNoiseLevel.Normal, "   init        Installs bifrost into the specified git repository.");
            LogLine(LogNoiseLevel.Normal, "   clone       like a normal git-clone but installs git-bifrost prior to checkout.");

            return Succeeded;
        }

        static int CmdClone(string[] args)
        {
            string clone_args = string.Join(" ", args, 1, args.Length - 1);

            if (StartGit("clone", "--no-checkout", clone_args).WaitForExitSucceed(true))
            {
                string arg_directory = args.Length > 2 ? args[args.Length - 1] : Path.GetFileNameWithoutExtension(args[1].TrimEnd('/', '\\'));

                Directory.SetCurrentDirectory(arg_directory);

                InstallBifrost();

                if (StartGit("checkout").WaitForExitSucceed(true))
                {
                    return Succeeded;
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

//                GitConfigSet("localstore.depth", "0", ".gitbifrost");

                GitConfigSet("store.luminawesome.mac.remote", "/Users/kylerocha/dev/BifrostTest.git", ".gitbifrost");
                GitConfigSet("store.luminawesome.mac.url", "ftp://localhost/dev/BifrostTestCDN", ".gitbifrost");
                GitConfigSet("store.luminawesome.mac.primary", "true", ".gitbifrost");
                GitConfigSet("store.luminawesome.mac.username", "USERNAME", ".gitbifrost");
                GitConfigSet("store.luminawesome.mac.password", "PASSWORD", ".gitbifrost");

                GitConfigSet("store.luminawesome.mac-backup.remote", "/Users/kylerocha/dev/BifrostTest.git", ".gitbifrost");
                GitConfigSet("store.luminawesome.mac-backup.url", "/Users/kylerocha/dev/BifrostTestCDN2", ".gitbifrost");
            }

            if (args.Contains("-ica", StringComparer.CurrentCultureIgnoreCase) ||
                args.Contains("--include-common-attributes", StringComparer.CurrentCultureIgnoreCase))
            {
                File.WriteAllText(".gitattributes", 
@"## Common
*.bmp  filter=bifrost
*.exe  filter=bifrost
*.dae  filter=bifrost
*.dll  filter=bifrost
*.fbx  filter=bifrost
*.ico  filter=bifrost
*.jpg  filter=bifrost
*.ma   filter=bifrost
*.max  filter=bifrost
*.mb   filter=bifrost
*.mp3  filter=bifrost
*.obj  filter=bifrost
*.ogg  filter=bifrost
*.png  filter=bifrost
*.psd  filter=bifrost
*.so   filter=bifrost
*.tga  filter=bifrost
*.ttf  filter=bifrost
*.tiff filter=bifrost
*.ztl  filter=bifrost
*.wav  filter=bifrost

## UE4
*.uasset filter=bifrost
*.umap   filter=bifrost

## Unity
*.unity filter=bifrost"
);
            }

            return Succeeded;
        }

        static int InstallBifrost()
        {
            if (!Directory.Exists(".git"))
            {
                Console.WriteLine("No git repository at '{0}'. Init a repo before using bifrost.", Directory.GetCurrentDirectory());
                return Failed;
            }

            if (!GitConfigSet("filter.bifrost.clean", "git-bifrost filter-clean %f") ||
                !GitConfigSet("filter.bifrost.smudge", "git-bifrost filter-smudge %f") ||
                !GitConfigSet("filter.bifrost.required", "true"))
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

            Console.WriteLine("Bifrost successfully installed into repo.");

            return Succeeded;
        }

        public static void Log(LogNoiseLevel Level, string format, params object[] arg)
        {
            if ((int)NoiseLevel >= (int)Level)
            {
                try
                {
                    Console.Error.Write(format, arg);
                }
                catch { }
            }
        }

        public static void LogLine(LogNoiseLevel Level, string format, params object[] arg)
        {
            if ((int)NoiseLevel >= (int)Level)
            {
                try
                {
                    Console.Error.WriteLine(format, arg);
                }
                catch
                {

                }
            }
        }


        static string GetProgressString(string message, int num, int total, string suffix = null)
        {
            int percent_complete = (int)((((float)num) / total) * 100);
            return string.Format("{0}: {1}% ({2}/{3}){4}",
                message, percent_complete, num, total, suffix == null ? "" : suffix);
        }


        static string GetFilePath(string file_hash)
        {
            // Use the first 3 hex characters as the directory names which is 4096 directories
            return String.Format("{0}/{1}/{2}/{3}.bin", file_hash[0], file_hash[1], file_hash[2], file_hash);
        }

        static Dictionary<string, IStoreInterface> GetStoreInterfaces()
        {
            var store_interfaces = new Dictionary<string, IStoreInterface>();

            store_interfaces[Uri.UriSchemeFile] = new StoreFileSystem();
            store_interfaces[Uri.UriSchemeFtp] = new StoreFtp();
            store_interfaces["ftps"] = new StoreFtp();

            return store_interfaces;
        }

        static List<Dictionary<string, string>> GetStores()
        {
            var stores = new List<Dictionary<string, string>>(7); // Arbitrary prime number

            // Add internal store
            {
                var local = new Dictionary<string, string>();
                local["name"] = "store.BIFROST.INTERNAL"; // Unique, reserved name
                local["url"] = Path.Combine(Directory.GetCurrentDirectory(), LocalStoreLocation);

                stores.Add(local);
            }


            string[] data_stores_text = GitConfigGetRegex(@"^store\..*", ".gitbifrost");

            foreach (string store_text in data_stores_text)
            {
                string[] store_kv = store_text.Split(new char[]{ ' ' }, 2);

                string store_name = Path.GetFileNameWithoutExtension(store_kv[0]);
                string key = Path.GetExtension(store_kv[0]).Substring(1);

                var store_data = stores.Find((Dictionary<string, string> match) =>
                        {
                            return match["name"] == store_name;
                        });

                if (store_data == null)
                {
                    store_data = new Dictionary<string, string>();
                    store_data["name"] = store_name;

                    stores.Add(store_data);
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

            return StartGit(command).WaitForExitSucceed(true);
        }

        static string[] GitConfigGetRegex(string key, string file)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            Process git_proc = StartGit("config --get-regexp", fileArg, key);

            List<string> lines = new List<string>();

            string line = null;
            while((line = git_proc.StandardOutput.ReadLine()) != null)
            {
                lines.Add(line);
            }

            git_proc.WaitForExit();
            git_proc.Dispose();

            return lines.ToArray();
        }

        static int GitConfigGetInt(string key, int defaultValue, string file)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            Process git_proc = StartGit("config --null --get", fileArg, key);

            string value_str = git_proc.StandardOutput.ReadToEnd();

            if (git_proc.WaitForExitFail(true))
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
            Process git_check_attr = StartGit(string.Format("check-attr --cached -z filter \"{0}\"", file));

            string value = git_check_attr.StandardOutput.ReadToEnd();

            if (git_check_attr.WaitForExitFail(true))
            {
                return null;
            }

            // 0 File name
            // 1 Attribute (filter)
            // 2 Value
            string[] attribute_tokens = value.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);

            return attribute_tokens[2];
        }

        static bool GetAttributeSet(string file, string attribute)
        {
            Process git_check_attr = StartGit(string.Format("check-attr -z {0} \"{1}\"", attribute, file));

            string value = git_check_attr.StandardOutput.ReadToEnd();

            if (git_check_attr.WaitForExitCode(true) != 0)
            {
                return false;
            }

            // File name
            // Attribute
            // Value
            string[] attribute_tokens = value.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);

            return attribute_tokens[2] == "set";
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

            LogLine(LogNoiseLevel.Normal, "Failed to start git.");
            return null;
        }
    }

    static class ProcessEx
    {
        public static int WaitForExitCode(this Process self, bool disposeOnExit = false)
        {
            self.WaitForExit();

            int exitCode = self.ExitCode;

            if (disposeOnExit)
            {
                self.Dispose();
            }

            return exitCode;
        }

        public static bool WaitForExitFail(this Process self, bool disposeOnExit = false)
        {
            return WaitForExitCode(self, disposeOnExit) != 0;
        }

        public static bool WaitForExitSucceed(this Process self, bool disposeOnExit = false)
        {
            return WaitForExitCode(self, disposeOnExit) == 0;
        }
    }

    static class DictionaryEx
    {
        public static string GetValue(this Dictionary<string, string> self, string key, string default_value)
        {
            string value;
            if (self.TryGetValue(key, out value))
            {
                return value;
            }

            return default_value;
        }
    }

}
