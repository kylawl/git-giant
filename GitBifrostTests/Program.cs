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
        static string TestingRoot;

        public static void Main(string[] args)
        {
            if (Directory.Exists("bifrost_test_products"))
            {
                Directory.Delete("bifrost_test_products", true);
            }

            Directory.CreateDirectory("bifrost_test_products");

            TestingRoot = Path.Combine(Directory.GetCurrentDirectory(), "bifrost_test_products");
            Directory.SetCurrentDirectory(TestingRoot);

            TestAddCommitPush();
            TestCloneEditPush();
        }

        static void TestAddCommitPush()
        {
            string test_root = Path.Combine(TestingRoot, "add_commit_push");

            //
            // Add
            //

            IsZero("Init git repo", StartProcessWaitForExitCode("git", "init " + test_root));

            Directory.SetCurrentDirectory(test_root);
            IsZero("Install bifrost", StartProcessWaitForExitCode("git", "bifrost init"));

            Random rand = new Random();

            // Binary file small enough to pass through the filter
            string smallBinaryName = "some/directory/BinaryFileSmall.bin";
            byte[] smallBinaryData = new byte[50 * 1024];
            rand.NextBytes(smallBinaryData);
            string smallBinarySHA = SHA1FromBytes(smallBinaryData);
            Directory.CreateDirectory(Path.GetDirectoryName(smallBinaryName));
            using (FileStream stream = File.Create(smallBinaryName))
            {
                stream.Write(smallBinaryData, 0, smallBinaryData.Length);
            }

            // Binary file too big to go in the normal git repo, must pass through bifrost
            string bigBinaryName = "BinaryFileBig.bin";
            byte[] bigBinaryData = new byte[500 * 1024];
            rand.NextBytes(bigBinaryData);
            string bigBinarySHA = SHA1FromBytes(bigBinaryData);
//            File.WriteAllBytes(bigBinaryName, bigBinaryData);
            using (FileStream stream = File.Create(bigBinaryName))
            {
                stream.Write(bigBinaryData, 0, bigBinaryData.Length);
            }

            // Text file small enough to pass through the filter
            string smallTextName = "TextFileSmall.txt";
            byte[] smallTextData = new byte[500 * 1024];
            RandomText(rand, smallTextData);
            string smallTextSHA = SHA1FromBytes(smallTextData);
            using (FileStream stream = File.Create(smallTextName))
            {
                stream.Write(smallTextData, 0, smallTextData.Length);
            }

            // Text file too big to go in the normal git repo, must pass through bifrost
            string bigTextName = "some/directory/TextFileBig.big_txt";
            byte[] bigTextData = new byte[10 * 1024 * 1024];
            RandomText(rand, bigTextData);
            string bigTextSHA = SHA1FromBytes(bigTextData);
            using (FileStream stream = File.Create(bigTextName))
            {
                stream.Write(bigTextData, 0, bigTextData.Length);
            }


            IsZero("Add all files", StartProcessWaitForExitCode("git", "add --all"));

            //
            // Commit
            //

            // Should fail because some files are too big.
            NotZero("Detect over budget files", StartProcessWaitForExitCode("git", "commit -m \"Added test files.\""));

            File.WriteAllLines(".gitattributes", new string[] {"*.bin filter=bifrost", "*.big_txt filter=bifrost"} );
            IsZero("Add .gitattributes", StartProcessWaitForExitCode("git", "add .gitattributes"));

            // Should fail because the files need to be restaged after adding the filter
            NotZero("Detect files need to be restages", StartProcessWaitForExitCode("git", "commit -m \"Added test files.\""));

            IsZero("Reset files", StartProcessWaitForExitCode("git", "reset"));
            IsZero("Add all files", StartProcessWaitForExitCode("git", "add --all"));
            IsZero("Valid commit", StartProcessWaitForExitCode("git", "commit -m \"Added test files.\""));

            // Inspect the internal bifrost store to make sure the files in there are good
            string[] files = Directory.GetFiles(".git/bifrost/data", "*.*", SearchOption.AllDirectories);

            AreEqual("Internal store file count", 3, files.Length);

            // Small binary
            string smallBinaryStorePath = Path.Combine(".git/bifrost/data", GetStorePathFromSHA(smallBinarySHA));
            IsTrue(string.Format("Internal store contains {0}", smallBinaryStorePath), files.Contains(smallBinaryStorePath));

            byte[] smallBinaryTestBytes = File.ReadAllBytes(smallBinaryStorePath);
            IsTrue(string.Format("File bytes consistent {0}", smallBinaryName), ArraysEqual(smallBinaryTestBytes, smallBinaryData));

            AreEqual("Small binary SHA", SHA1FromBytes(smallBinaryTestBytes), smallBinarySHA);

            // Big binary
            string bigBinaryStorePath = Path.Combine(".git/bifrost/data", GetStorePathFromSHA(bigBinarySHA));
            IsTrue(string.Format("Internal store contains {0}", bigBinaryStorePath), files.Contains(bigBinaryStorePath));

            byte[] bigBinaryTestBytes = File.ReadAllBytes(bigBinaryStorePath);
            IsTrue(string.Format("File bytes consistent {0}", bigBinaryName), ArraysEqual(bigBinaryTestBytes, bigBinaryData));

            AreEqual("Small binary SHA", SHA1FromBytes(bigBinaryTestBytes), bigBinarySHA);

            // Big text
            string bitTextStorePath = Path.Combine(".git/bifrost/data", GetStorePathFromSHA(bigTextSHA));
            IsTrue(string.Format("Internal store contains {0}", bitTextStorePath), files.Contains(bitTextStorePath));

            byte[] bigTextTestBytes = File.ReadAllBytes(bitTextStorePath);
            IsTrue(string.Format("File bytes consistent {0}", bigTextName), ArraysEqual(bigTextTestBytes, bigTextTestBytes));

            AreEqual("Small binary SHA", SHA1FromBytes(bigTextTestBytes), bigTextSHA);

            //
            // Push
            //

            Directory.SetCurrentDirectory(TestingRoot);
        }

        static void TestCloneEditPush()
        {
            string test_root = Path.Combine(TestingRoot, "clone_edit_push");

            IsZero("Bifrost clone", StartProcessWaitForExitCode("git", "bifrost " + test_root));
        }
            
        // Testers
        static void IsZero(string test_name, int value)
        {
            if (!(value == 0))
            {
                throw new Exception(string.Format("'{0}' failed, value is not 0.", test_name));
            }
        }

        static void NotZero(string test_name, int value)
        {
            if (!(value != 0))
            {
                throw new Exception(string.Format("'{0}' failed, value is 0", test_name));
            }
        }

        static void AreEqual<T>(string test_name, T a, T b)
        {
            if (!a.Equals(b))
            {
                throw new Exception(string.Format("'{0}' failed, values are not equal ({1} != {2})", test_name, a, b));
            }
        }

        static void AreNotEqual<T>(string test_name, T a, T b)
        {
            if (a.Equals(b))
            {
                throw new Exception(string.Format("'{0}' failed, values are equal ({1} == {2})", test_name, a, b));
            }
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
        }

        static void IsTrue(string test_name, bool isTrue)
        {
            if (!isTrue)
            {
                throw new Exception(string.Format("'{0}' failed, value is false", test_name));
            }
        }

        // Utils

        static string GetStorePathFromSHA(string sha)
        {
            return Path.Combine(string.Format("{0}/{1}/{2}", sha[0], sha[1], sha[2]), sha + ".bin");
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

        static Tuple<string, byte[]>[] GenerateTestData(uint fileCount, string fileName, string extention, int minSize, int maxSize, int maxDirDepth)
        {
            Random rand = new Random();

            Tuple<string, byte[]>[] output = new Tuple<string, byte[]>[fileCount];

            for (int i = 0; i < fileCount; ++i)
            {
                int size = rand.Next(minSize, maxSize);
                int depth = rand.Next(1, maxDirDepth);

                string path = "";
                for (int d = 0; d < depth; ++d)
                {
                    path = Path.Combine(path, Path.GetDirectoryName(Path.GetRandomFileName()));
                }
                path = Path.Combine(path, string.Format("{0}_{1}.{2}", fileName, i, extention));


                byte[] data = new byte[size];
                rand.NextBytes(data);

                output[i] = new Tuple<string, byte[]>(path, data);
            }

            return output;
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
                    WorkingDirectory = workingDir 
                }))
            {
                proc.WaitForExit();

                exitCode = proc.ExitCode;
            }

            return exitCode;
        }
    }
}
