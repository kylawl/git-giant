// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace GitBifrostTests
{
    class Program
    {
        const string CommonRemote = "commonremote.git";
        static string CommonRemotePath;
        static string CommonRemoteStore;
        static string TestingRoot;
        
        public static string SanitizePath(string path)
        {
#if !(__MonoCS__)            
            return path.Replace('/', '\\');
#endif
            return path;
        }

        public static void SanitizePaths(string[] paths)
        {
            for(int i = 0; i < paths.Length; ++i)
            {
                paths[i] = SanitizePath(paths[i]);
            }
        }

        public static string PathCombine(params string[] paths)
        {
            return SanitizePath(Path.Combine(paths));
        }

        public static void Main(string[] args)
        {
            //
            // Cleanup / Setup
            //

            if (Directory.Exists("bifrost_test_products"))
            {
                Directory.Delete("bifrost_test_products", true);
            }

            Directory.CreateDirectory("bifrost_test_products");

            TestingRoot = PathCombine(Directory.GetCurrentDirectory(), "bifrost_test_products");
            Directory.SetCurrentDirectory(TestingRoot);

            CommonRemotePath = PathCombine(TestingRoot, CommonRemote);
            CommonRemoteStore = PathCombine(TestingRoot, "commonremote.store");

            Directory.CreateDirectory(CommonRemoteStore);
            StartProcessWaitForExitCode("git", "init --bare " + CommonRemotePath);

            Random rand = new Random();

            //
            // Create Test Data
            //////////////////////////////////////////////////////////////////////////

            // Binary file small enough to pass through the filter
            string smallBinaryName = "some/directory/BinaryFileSmall.bin";
            byte[] smallBinaryData = new byte[50 * 1024];
            rand.NextBytes(smallBinaryData);
            string smallBinarySHA = SHA1FromBytes(smallBinaryData);
            string smallBinaryLocalStorePath = PathCombine(".git/bifrost/data", GetStorePathFromSHA(smallBinarySHA));

            // Binary file too big to go in the normal git repo, must pass through bifrost
            string bigBinaryName = "BinaryFileBig.bin";
            byte[] bigBinaryData = new byte[500 * 1024];
            rand.NextBytes(bigBinaryData);
            string bigBinarySHA = SHA1FromBytes(bigBinaryData);
            string bigBinaryLocalStorePath = PathCombine(".git/bifrost/data", GetStorePathFromSHA(bigBinarySHA));

            // Text file small enough to pass through the filter
            string smallTextName = "TextFileSmall.txt";
            byte[] smallTextData = new byte[500 * 1024];
            RandomText(rand, smallTextData);
            string smallTextSHA = SHA1FromBytes(smallTextData);

            // Text file too big to go in the normal git repo, must pass through bifrost
            string bigTextName = "some/directory/TextFileBig.big_txt";
            byte[] bigTextData = new byte[10 * 1024 * 1024];
            RandomText(rand, bigTextData);
            string bigTextSHA = SHA1FromBytes(bigTextData);
            string bigTextLocalStorePath = PathCombine(".git/bifrost/data", GetStorePathFromSHA(bigTextSHA));


            //
            // Add/Commit/Push
            //////////////////////////////////////////////////////////////////////////
            {
                //
                // Add
                //

                string test_root = PathCombine(TestingRoot, "add_commit_push");

                IsZero("Init git repo", StartProcessWaitForExitCode("git", "init " + test_root));

                Directory.SetCurrentDirectory(test_root);
                IsZero("Install bifrost", StartProcessWaitForExitCode("git", "bifrost init"));

                Directory.CreateDirectory(Path.GetDirectoryName(smallBinaryName));
                File.WriteAllBytes(smallBinaryName, smallBinaryData);
                File.WriteAllBytes(bigBinaryName, bigBinaryData);
                File.WriteAllBytes(smallTextName, smallTextData);
                File.WriteAllBytes(bigTextName, bigTextData);

                IsZero("Add all files", StartProcessWaitForExitCode("git", "add --all"));

                //
                // Commit
                //////////////////////////////////////////////////////////////////////////

                // Should fail because some files are too big.
                NotZero("Detect over budget files", StartProcessWaitForExitCode("git", "commit -m \"Added test files.\""));

                File.WriteAllLines(".gitattributes", new string[] { "*.bin filter=bifrost", "*.big_txt filter=bifrost" });
                IsZero("Add .gitattributes", StartProcessWaitForExitCode("git", "add .gitattributes"));

                // Should fail because the files need to be restaged after adding the filter
                NotZero("Detect files need to be restages", StartProcessWaitForExitCode("git", "commit -m \"Added test files.\""));

                IsZero("Reset files", StartProcessWaitForExitCode("git", "reset"));
                IsZero("Add all files", StartProcessWaitForExitCode("git", "add --all"));
                IsZero("Valid commit", StartProcessWaitForExitCode("git", "commit -m \"Added test files.\""));

                // Inspect the internal bifrost store to make sure the files in there are good
                {
                    string[] files = Directory.GetFiles(".git/bifrost/data", "*.*", SearchOption.AllDirectories);
                    SanitizePaths(files);

                    AreEqual("Internal store file count", 3, files.Length);

                    // Small binary
                    IsTrue(string.Format("Internal store contains {0}", smallBinaryLocalStorePath), files.Contains(smallBinaryLocalStorePath));
                    CheckFileConsistency(smallBinaryName, smallBinaryLocalStorePath, smallBinarySHA, smallBinaryData);

                    // Big binary
                    IsTrue(string.Format("Internal store contains {0}", bigBinaryLocalStorePath), files.Contains(bigBinaryLocalStorePath));
                    CheckFileConsistency(bigBinaryName, bigBinaryLocalStorePath, bigBinarySHA, bigBinaryData);
                    
                    // Big text
                    IsTrue(string.Format("Internal store contains {0}", bigTextLocalStorePath), files.Contains(bigTextLocalStorePath));
                    CheckFileConsistency(bigTextName, bigTextLocalStorePath, bigTextSHA, bigTextData);
                }

                //
                // Push
                //////////////////////////////////////////////////////////////////////////

                // Push should fail becasue we don't have a .gitbifrost config to tell us where the primary store is
                NotZero("Fail push to empty remote", StartProcessWaitForExitCode("git", string.Format("push {0} master", CommonRemotePath)));

                // Push should succeed now
                WriteBifrostConfig();
                IsZero("Successfull push to remote and store", StartProcessWaitForExitCode("git", string.Format("push {0} master", CommonRemotePath)));

                // Check remote store for consistency
                {
                    string[] files = Directory.GetFiles(CommonRemoteStore, "*.*", SearchOption.AllDirectories);
                    SanitizePaths(files);

                    AreEqual("Internal store file count", 3, files.Length);

                    // Small binary
                    string smallBinaryCommonStorePath = PathCombine(CommonRemoteStore, GetStorePathFromSHA(smallBinarySHA));
                    IsTrue(string.Format("Internal store contains {0}", smallBinaryLocalStorePath), files.Contains(smallBinaryCommonStorePath));
                    CheckFileConsistency(smallBinaryName, smallBinaryLocalStorePath, smallBinarySHA, smallBinaryData);

                    // Big binary
                    string bigBinaryCommonStorePath = PathCombine(CommonRemoteStore, GetStorePathFromSHA(bigBinarySHA));
                    IsTrue(string.Format("Internal store contains {0}", bigBinaryLocalStorePath), files.Contains(bigBinaryCommonStorePath));
                    CheckFileConsistency(bigBinaryName, bigBinaryLocalStorePath, bigBinarySHA, bigBinaryData);

                    // Big text
                    string bigTextCommonStorePath = PathCombine(CommonRemoteStore, GetStorePathFromSHA(bigTextSHA));
                    IsTrue(string.Format("Internal store contains {0}", bigTextLocalStorePath), files.Contains(bigTextCommonStorePath));
                    CheckFileConsistency(bigTextName, bigTextLocalStorePath, bigTextSHA, bigTextData);
                }


                Directory.SetCurrentDirectory(TestingRoot);
            }

            //
            // Clone / Edit / Push
            //////////////////////////////////////////////////////////////////////////

            {
                //
                // Clone
                // 

                string test_root = PathCombine(TestingRoot, "clone_edit_push");

                // The first clone should fail becasue there is no .gitbifrost file checked in, 
                // therefor there is no way to retreive files form the store
                NotZero("Bifrost clone", StartProcessWaitForExitCode("git", string.Format("bifrost clone {0} {1}", CommonRemotePath, test_root)));

                Directory.SetCurrentDirectory(test_root);

                WriteBifrostConfig();

                // Force checkout with attribute file in place
                IsZero("Bifrost checkout", StartProcessWaitForExitCode("git", string.Format("checkout -f master")));

                // Inspect the internal bifrost store to make sure the files in there are good
                {
                    string[] files = Directory.GetFiles(".git/bifrost/data", "*.*", SearchOption.AllDirectories);
                    SanitizePaths(files);

                    AreEqual("Internal store file count", 3, files.Length);

                    // Small binary
                    IsTrue(string.Format("Internal store contains {0}", smallBinaryLocalStorePath), files.Contains(smallBinaryLocalStorePath));
                    CheckFileConsistency(smallBinaryName, smallBinaryLocalStorePath, smallBinarySHA, smallBinaryData);

                    // Big binary
                    IsTrue(string.Format("Internal store contains {0}", bigBinaryLocalStorePath), files.Contains(bigBinaryLocalStorePath));
                    CheckFileConsistency(bigBinaryName, bigBinaryLocalStorePath, bigBinarySHA, bigBinaryData);

                    // Big text
                    IsTrue(string.Format("Internal store contains {0}", bigTextLocalStorePath), files.Contains(bigTextLocalStorePath));
                    CheckFileConsistency(bigTextName, bigTextLocalStorePath, bigTextSHA, bigTextData);
                }

                //
                // Add / Commit .gitbifrost, modified file and new file
                //

                // Modified big text file from before
                string modifiedBigTextName = "some/directory/TextFileBig.big_txt";
                byte[] modifiedBigTextData = new byte[bigTextData.Length];
                RandomText(rand, modifiedBigTextData);
                string modifiedBigTextSHA = SHA1FromBytes(modifiedBigTextData);
                string modifiedBigTextLocalStorePath = PathCombine(".git/bifrost/data", GetStorePathFromSHA(modifiedBigTextSHA));

                // Another binary file too big to go in the normal git repo, must pass through bifrost
                string bigBinaryName2 = "BinaryFileBigNumber2.bin";
                byte[] bigBinaryData2 = new byte[50 * 1024 * 1024];
                rand.NextBytes(bigBinaryData2);
                string bigBinarySHA2 = SHA1FromBytes(bigBinaryData2);
                string bigBinaryLocalStorePath2 = PathCombine(".git/bifrost/data", GetStorePathFromSHA(bigBinarySHA2));

                File.WriteAllBytes(modifiedBigTextName, modifiedBigTextData);
                File.WriteAllBytes(bigBinaryName2, bigBinaryData2);

                IsZero("Add all files", StartProcessWaitForExitCode("git", "add --all"));
                IsZero("Valid commit", StartProcessWaitForExitCode("git", "commit -m \"Added more test files.\""));

                // 
                // Push
                //

                IsZero("Successfull push to remote and store", StartProcessWaitForExitCode("git", string.Format("push {0} master", CommonRemotePath)));

                // Check remote store for consistency
                {
                    string[] files = Directory.GetFiles(CommonRemoteStore, "*.*", SearchOption.AllDirectories);
                    SanitizePaths(files);

                    AreEqual("Internal store file count", 5, files.Length);

                    // Small binary
                    string smallBinaryCommonStorePath = PathCombine(CommonRemoteStore, GetStorePathFromSHA(smallBinarySHA));
                    IsTrue(string.Format("Internal store contains {0}", smallBinaryLocalStorePath), files.Contains(smallBinaryCommonStorePath));
                    CheckFileConsistency(smallBinaryName, smallBinaryLocalStorePath, smallBinarySHA, smallBinaryData);

                    // Big binary
                    string bigBinaryCommonStorePath = PathCombine(CommonRemoteStore, GetStorePathFromSHA(bigBinarySHA));
                    IsTrue(string.Format("Internal store contains {0}", bigBinaryLocalStorePath), files.Contains(bigBinaryCommonStorePath));
                    CheckFileConsistency(bigBinaryName, bigBinaryLocalStorePath, bigBinarySHA, bigBinaryData);

                    // Big text
                    string bigTextCommonStorePath = PathCombine(CommonRemoteStore, GetStorePathFromSHA(bigTextSHA));
                    IsTrue(string.Format("Internal store contains {0}", bigTextLocalStorePath), files.Contains(bigTextCommonStorePath));
                    CheckFileConsistency(bigTextName, bigTextLocalStorePath, bigTextSHA, bigTextData);

                    // Big text modified
                    string modifiedBigTextCommonStorePath = PathCombine(CommonRemoteStore, GetStorePathFromSHA(modifiedBigTextSHA));
                    IsTrue(string.Format("Internal store contains {0}", modifiedBigTextCommonStorePath), files.Contains(modifiedBigTextCommonStorePath));
                    CheckFileConsistency(modifiedBigTextName, modifiedBigTextLocalStorePath, modifiedBigTextSHA, modifiedBigTextData);

                    // Big binary2
                    string bigBinaryCommonStorePath2 = PathCombine(CommonRemoteStore, GetStorePathFromSHA(bigBinarySHA2));
                    IsTrue(string.Format("Internal store contains {0}", bigBinaryLocalStorePath2), files.Contains(bigBinaryCommonStorePath2));
                    CheckFileConsistency(bigBinaryName2, bigBinaryLocalStorePath2, bigBinarySHA2, bigBinaryData2);
                }


                Directory.SetCurrentDirectory(TestingRoot);
            }
        }

        static void CheckFileConsistency(string displayName, string filePath, string expectedSHA, byte[] expectedBytes)
        {
            byte[] testBytes = File.ReadAllBytes(filePath);
            IsTrue(string.Format("File bytes consistent '{0}'", displayName), ArraysEqual(testBytes, expectedBytes));

            AreEqual(string.Format("File SHA consistent '{0}'", displayName), SHA1FromBytes(testBytes), expectedSHA);
        }
            
        // Testers
        static void IsZero(string test_name, int value)
        {
            if (!(value == 0))
            {
                throw new Exception(string.Format("'{0}' failed, value is not 0.", test_name));
            }
            TestPassedMsg(test_name);
        }

        static void NotZero(string test_name, int value)
        {
            if (!(value != 0))
            {
                throw new Exception(string.Format("'{0}' failed, value is 0", test_name));
            }
            TestPassedMsg(test_name);
        }

        static void AreEqual<T>(string test_name, T a, T b)
        {
            if (!a.Equals(b))
            {
                throw new Exception(string.Format("'{0}' failed, values are not equal ({1} != {2})", test_name, a, b));
            }
            TestPassedMsg(test_name);
        }

        static void AreNotEqual<T>(string test_name, T a, T b)
        {
            if (a.Equals(b))
            {
                throw new Exception(string.Format("'{0}' failed, values are equal ({1} == {2})", test_name, a, b));
            }
            TestPassedMsg(test_name);
        }

        static bool ArraysEqual<T>(T[] a1, T[] a2)
        {
            if (ReferenceEquals(a1,a2))
                return true;

            if (a1 == null || a2 == null)
                return false;

            if (a1.Length != a2.Length)
                return false;

            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a1.Length; i++)
            {
                if (!comparer.Equals(a1[i], a2[i])) return false;
            }
            return true;
        }

        static void IsFalse(string test_name, bool isFalse)
        {
            if (!isFalse)
            {
                throw new Exception(string.Format("'{0}' failed, value is true", test_name));
            }
            TestPassedMsg(test_name);
        }

        static void IsTrue(string test_name, bool isTrue)
        {
            if (!isTrue)
            {
                throw new Exception(string.Format("'{0}' failed, value is false", test_name));
            }
            TestPassedMsg(test_name);
        }

        static void TestPassedMsg(string test_name)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine("Test: {0}... Passed", test_name);
            Console.ResetColor();
        }

        // Utils

        static void WriteBifrostConfig()
        {
            File.WriteAllText(".gitbifrost", string.Format(
                @"[repo]
                    text-size-threshold = 5242880
                    bin-size-threshold = 102400
                [store ""luminawesome.mac""]
                    remote = {0}
                    url = {1}
                    primary = true",
                CommonRemotePath.Replace("\\","\\\\"), 
                CommonRemoteStore.Replace("\\", "\\\\"))
            );
        }

        static string GetStorePathFromSHA(string sha)
        {
            return PathCombine(string.Format("{0}/{1}/{2}", sha[0], sha[1], sha[2]), sha + ".bin");
        }

        static string SHA1FromBytes(byte[] bytes)
        {
            return BitConverter.ToString(SHA1Managed.Create().ComputeHash(bytes)).Replace("-", "");
        }

        static void RandomText(Random rand, byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; ++i)
            {
                if (i % 100 == 99)
                {
                    buffer[i] = (byte)'\n';
                }
                else
                {
                    buffer[i] = (byte)rand.Next(32, 127);
                }
            }
        }

        static int StartProcessWaitForExitCode(string fileName, string arguments, string workingDir = null)
        {
            int exitCode = -1;

            if (workingDir == null)
            {
                workingDir = Directory.GetCurrentDirectory();
            }

            using (Process proc = Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = false
                }))
            {
                proc.WaitForExit();

                exitCode = proc.ExitCode;
            }

            return exitCode;
        }
    }
}
