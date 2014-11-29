// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;

using Mono.Unix.Native;

namespace GitBifrost
{
    class StoreData : Dictionary<string, string> { }
    class StoreContainer : Dictionary<string, StoreData> { }

    class Program
    {
        const int StartingBufferSize = 1024 * 1024;

        const string DefaultLocalDataLocation = ".git/bifrost/data";
        const string RemoteDataStore = "D:/Work/BifrostTestCDN";

        static readonly string[] StandardAttributes = new string[]
        {
            "*.bmp filter=bifrost",
            "*.dae filter=bifrost",
            "*.fbx filter=bifrost",
            "*.max filter=bifrost",
            "*.obj filter=bifrost",
            "*.png filter=bifrost",
            "*.psd filter=bifrost",
            "*.tga filter=bifrost",
            "*.ttf filter=bifrost",
            "*.ztl filter=bifrost",
        };

        static StreamWriter LogWriter;

        static void LogLine(string format, params object[] arg)
        {
            LogWriter.WriteLine(format, arg);
            Console.Error.WriteLine(format, arg);
        }

        delegate void CommandDelegate(string[] args);

        static int Main(string[] args)
        {
            //Debugger.Break();

            int result = 0;

            using (LogWriter = new StreamWriter(File.Open("bifrostlog.txt", FileMode.Append, FileAccess.Write)))
            {
                LogLine("Bifrost: {0}", string.Join(" ", args));
                //LogLine("Current Dir: {0}", Directory.GetCurrentDirectory());
                //LogLine("PATH: {0}", Environment.GetEnvironmentVariable("PATH"));

                Dictionary<string, CommandDelegate> Commands = new Dictionary<string, CommandDelegate>(10);

                Commands["hook-sync"] = HookSync;
                Commands["hook-push"] = HookPush;
                Commands["filter-clean"] = FilterClean;
                Commands["filter-smudge"] = FilterSmudge;
                Commands["help"] = Help;
                Commands["activate"] = Activate;


                string arg_command = args.Length > 0 ? args[0].ToLower() : null;

                if (arg_command != null)
                {
                    Commands[arg_command](args);
                }

                LogLine("");
            }

            return result;
        }

        static void HookSync(string[] args)
        {

        }

        static void HookPush(string[] args)
        {
            string arg_branch = args[1].TrimEnd('/');
            string arg_remote = args[2].TrimEnd('/');

            LogLine("Bifrost: Updating datastore(s) for remote '{0}'", arg_remote);

            StoreContainer stores = GetStores();
            foreach (KeyValuePair<string, StoreData> store_kvp in stores)
            {
                StoreData store_data = store_kvp.Value;

                string store_url = store_data["url"].TrimEnd('/');
                string store_remote_url = store_data["remote"].TrimEnd('/');    

                if (store_remote_url != arg_remote || !Directory.Exists(store_url))
                {
                    continue;
                }

                LogLine("Bifrost: Pushing to '{0}'", store_url);

                int files_pushed = 0;
                int files_skipped = 0;
                int files_skipped_late = 0;

                string[] source_files = Directory.GetFiles(DefaultLocalDataLocation);

                foreach (string file in source_files)
                {
                    string filename = Path.GetFileName(file);

                    string dest_filepath = Path.Combine(store_url, filename);

                    if (!File.Exists(dest_filepath))
                    {
                        Guid guid = Guid.NewGuid();

                        string temp_file = string.Format("{0}.tmp", guid.ToString().Replace("-", ""));

                        string dest_filepath_temp = Path.Combine(store_url, temp_file);

                        File.Copy(file, dest_filepath_temp);                    

                        if (!File.Exists(dest_filepath))
                        {
                            File.Move(dest_filepath_temp, dest_filepath);
                            ++files_pushed;
                        }
                        else
                        {
                            File.Delete(temp_file);
                            ++files_skipped_late;
                        }
                    }
                    else
                    {
                        // TODO: Safety check for comparing size of local file to hash file (incase of collision)

                        files_skipped++;
                    }
                }

                LogLine("Bifrost: {0} Copied, {1} Skipped (total), {2} Skipped late", files_pushed, files_skipped, files_skipped_late);
            }
        }

        static void FilterClean(string[] args)
        {            
            string arg_filepath = null;

            if (args.Length > 1)
            {
                arg_filepath = args[1];                
            }
            else
            {
                return;
            }

            MemoryStream file_stream = new MemoryStream(StartingBufferSize); // Start with a meg

            string file_hash_string = null;

            using (Stream stdin = Console.OpenStandardInput())
            {
                stdin.CopyTo(file_stream);
            }

            file_stream.Position = 0;

            LogLine("Bytes: {0}", file_stream.Length);

            using (StreamWriter output_writer = new StreamWriter(Console.OpenStandardOutput()))
            {
                HashAlgorithm hashcalc = SHA1.Create();
                byte[] file_hash = hashcalc.ComputeHash(file_stream);

                file_hash_string = BitConverter.ToString(file_hash).Replace("-", "");

                string link_data = string.Format("SHA1:{0}", file_hash_string);

                output_writer.WriteLine(link_data);
            }


            LogLine("Name: {0}", arg_filepath);
            LogLine("Hash: {0}", file_hash_string);

            string output_filename = String.Format("{0}-{1}.bin", file_hash_string, Path.GetFileName(arg_filepath));
            string output_filepath = Path.Combine(DefaultLocalDataLocation, output_filename);

            if (!File.Exists(output_filepath))
            {
                Directory.CreateDirectory(DefaultLocalDataLocation);

                using (FileStream cache_stream = new FileStream(output_filepath, FileMode.Create, FileAccess.Write))
                {                    
                    file_stream.WriteTo(cache_stream);
                }
            }
        }

        static void FilterSmudge(string[] args)
        {
//            Debugger.Break();

            string arg_filepath = null;

            if (args.Length > 1)
            {
                arg_filepath = args[1];
            }
            else
            {
                return;
            }

            string hash_type = null;
            string expected_file_hash = null;

            using (StreamReader input_reader = new StreamReader(Console.OpenStandardInput()))
            {
                string link_data = input_reader.ReadLine();
                string[] link_tokens = link_data.Split(':');

                hash_type = link_tokens[0];
                expected_file_hash = link_tokens[1];
            }

            string input_filename = String.Format("{0}-{1}.bin", expected_file_hash, Path.GetFileName(arg_filepath));

            string[] data_stores = null;

			string[] loaded_stores = GitConfigGetRegex(@".*\.url", ".gitbifrost");
            data_stores = new string[loaded_stores.Length + 1];

            data_stores[0] = DefaultLocalDataLocation;

            int index = 1;
            foreach(string store in loaded_stores)
            {
                string[] store_tokens = store.Split(new char[] {' '}, 2);
                data_stores[index++] = store_tokens[1];
            }
                
            foreach (string datastore_location in data_stores)
            {            
                string input_filepath = Path.Combine(datastore_location, input_filename);       

                if (File.Exists(input_filepath))
                {
                    int file_size = 0;
                    byte[] file_contents = File.ReadAllBytes(input_filepath);
                    file_size = file_contents.Length;

                    HashAlgorithm hashcalc = SHA1.Create();
                    byte[] loaded_file_hash = hashcalc.ComputeHash(file_contents, 0, file_size);
                    string loaded_file_hash_string = BitConverter.ToString(loaded_file_hash).Replace("-", "");

                    LogLine("Name: {0}", arg_filepath);
                    LogLine("Expect Hash: {0}", expected_file_hash);
                    LogLine("Loaded Hash: {0}", loaded_file_hash_string);

                    // TODO: Safety check for comparing size of local file to hash file (incase of collision)

                    if (loaded_file_hash_string != expected_file_hash)
                    {
                        throw new InvalidDataException();
                    }

                    using (Stream stdout = Console.OpenStandardOutput())
                    {
                        stdout.Write(file_contents, 0, file_size);
                    }

                    break;
                }
            }
        }

        static void Help(string[] args)
        {

        }

        static void Activate(string[] args)
        {
            if (!Directory.Exists(".git"))
            {
                Console.WriteLine("No git repository at {0}", Directory.GetCurrentDirectory());
                return;
            }

            GitConfigSet("filter.bifrost.clean", "git-bifrost filter-clean %f");
            GitConfigSet("filter.bifrost.smudge", "git-bifrost filter-smudge %f");
            GitConfigSet("filter.bifrost.required", "true");

            //using (StreamWriter post_checkout = new StreamWriter(File.OpenWrite(".git/hooks/post-checkout")))
            //{
            //    post_checkout.BaseStream.Position = post_checkout.BaseStream.Length - 1;

            //    post_checkout.WriteLine("git-bifrost sync");
            //}

			File.WriteAllText(".git/hooks/pre-push", "#!/bin/bash\ngit-bifrost hook-push \"$@\"");
			Syscall.chmod(".git/hooks/pre-push", FilePermissions.ACCESSPERMS);
			File.WriteAllText(".git/hooks/post-checkout", "#!/bin/bash\ngit-bifrost hook-sync \"$@\"");
			Syscall.chmod(".git/hooks/post-checkout", FilePermissions.ACCESSPERMS);


            //SetConfig("localstore.slim", "true", ".gitbifrost");

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


            //SetConfig("luminawesome.offsite.storeurl", "D:/Work/BifrostTestCDN", ".bifroststores");

            //StreamWriter File.CreateText

            //File.WriteAllText(".gitbifrost", "# Add something meaningful here");

            if (args.Contains("-ica", StringComparer.CurrentCultureIgnoreCase) ||
                args.Contains("--include-common-attributes", StringComparer.CurrentCultureIgnoreCase))
            {
                File.WriteAllLines(".gitattributes", StandardAttributes);
            }

            Console.WriteLine("Bifrost is now active");
        }

        static StoreContainer GetStores()
        {
            StoreContainer stores = new StoreContainer();

            string[] data_stores_text = GitConfigGetRegex(@"store\..*", ".gitbifrost");

            foreach (string store_text in data_stores_text)
            {
                string[] store_kv = store_text.Split(new char[]{ ' ' }, 2);

                string store_name = Path.GetFileNameWithoutExtension(store_kv[0]);
                string key = Path.GetExtension(store_kv[0]).Substring(1);

                StoreData store = null;

                if (!stores.TryGetValue(store_name, out store))
                {
                    store = new StoreData();
                    stores[store_name] = store;
                }

                store[key] = store_kv[1];
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

            return Git(command).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);;
        }

        static string Git(string arguments)
        {
            Process process = new Process();
            ProcessStartInfo psi = process.StartInfo;
            psi.FileName = "git";
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.EnvironmentVariables["PATH"] = psi.EnvironmentVariables["PATH"].Replace(@"\", @"\\");                   
            
            string output = string.Empty;
            if (process.Start())
            {
                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
            }
            return output;
        }
    }
}