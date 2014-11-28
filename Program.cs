// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;

namespace GitBifrost
{
    class Program
    {
        const string DefaultLocalDataLocation = ".git/bifrost/data";

        static readonly string[] StandardAttributes = new string[]
        {
            "*.max filter=bifrost",
            "*.ttf filter=bifrost",
            "*.tga filter=bifrost",
            "*.png filter=bifrost",
            "*.psd filter=bifrost",
            "*.fbx filter=bifrost"
        };

        static StreamWriter LogWriter;

        static void LogLine(string format, params object[] arg)
        {
            LogWriter.WriteLine(format, arg);
            Console.Error.WriteLine(format, arg);
        }

        static int Main(string[] args)
        {
            //Debugger.Break();
            using (LogWriter = new StreamWriter(File.Open("bifrostlog.txt", FileMode.Append, FileAccess.Write)))
            {
                LogLine("=== Bifrost started ===");
                //LogWriter.WriteLine("Current Dir: {0}", Directory.GetCurrentDirectory());
                //LogWriter.WriteLine("PATH: {0}", Environment.GetEnvironmentVariable("PATH"));

                string arg_command = args.Length > 0 ? args[0].ToLower() : null;

                switch (arg_command)
                {
                    case "activate":
                        {
                            Activate();
                        }
                        break;
                    case "filter-clean":
                        {
                            if (args.Length > 1)
                            {
                                string arg_filename = args[1];
                                FilterClean(arg_filename);
                            }
                        }
                        break;
                    case "filter-smudge":
                        {
                            if (args.Length > 1)
                            {
                                string arg_filename = args[1];
                                FilterSmudge(arg_filename);
                            }
                        }
                        break;
                    default:
                        break;
                }

                LogWriter.WriteLine();
            }

            return 0;
        }

        static void FilterClean(string filepath)
        {
            LogLine("filter-clean");

            MemoryStream file_stream = new MemoryStream(1024 * 1024);

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


            LogLine("Name: {0}", filepath);
            LogLine("Hash: {0}", file_hash_string);

            string output_filename = String.Format("{0}-{1}.bin", file_hash_string, Path.GetFileName(filepath));
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
        static void FilterSmudge(string filepath)
        {
            LogLine("Action: filter-smudge");
            
            string hash_type = null;
            string expected_file_hash = null;

            using (StreamReader input_reader = new StreamReader(Console.OpenStandardInput()))
            {
                string link_data = input_reader.ReadLine();               
                string[] link_tokens = link_data.Split(':');

                hash_type = link_tokens[0];
                expected_file_hash = link_tokens[1];
            }

            string input_filename = String.Format("{0}-{1}.bin", expected_file_hash, Path.GetFileName(filepath));
            string input_filepath = Path.Combine(DefaultLocalDataLocation, input_filename);


            int file_size = 0;
            byte[] file_contents = File.ReadAllBytes(input_filepath);
            file_size = file_contents.Length;

            HashAlgorithm hashcalc = SHA1.Create();
            byte[] loaded_file_hash = hashcalc.ComputeHash(file_contents, 0, file_size);
            string loaded_file_hash_string = BitConverter.ToString(loaded_file_hash).Replace("-", "");


            LogLine("Name: {0}", filepath);
            LogLine("Expect Hash: {0}", expected_file_hash);
            LogLine("Loaded Hash: {0}", loaded_file_hash_string);

            if (loaded_file_hash_string != expected_file_hash)
            {
                throw new InvalidDataException();
            }

            using (Stream stdout = Console.OpenStandardOutput())
            {
                stdout.Write(file_contents, 0, file_size);
            }
        }

        static void Git(string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo("git.exe", arguments);
            psi.UseShellExecute = false;
            psi.EnvironmentVariables["PATH"] = psi.EnvironmentVariables["PATH"].Replace(@"\", @"\\");
            Process.Start(psi).WaitForExit();                        
        }

        static void Activate()
        {
            Console.WriteLine("Bifrost is now active");

            Git("config filter.bifrost.clean \"git-bifrost filter-clean %f\"");
            Git("config filter.bifrost.smudge \"git-bifrost filter-smudge %f\"");
            Git("config filter.bifrost.required true");
            

            //File.WriteAllText(".gitbifrost", "# Add something meaningful here");

            File.WriteAllLines(".gitattributes", StandardAttributes);
        }
    }
}
