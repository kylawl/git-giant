// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.IO;
using System.Diagnostics;

namespace GitBifrost
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Bifrost started");

            if (args[0] == "activate")
            {
                Activate();
            }
            else
            {
                foreach (string str in args)
                {
                    //Console.WriteLine(str);
                }
            }
        }

        static void Activate()
        {
            Console.WriteLine("Bifrost is now active");

            Process.Start("git", "config filter.bifrost.clean 'git-bifrost clean'");
            Process.Start("git", "config filter.bifrost.smudge 'git-bifrost smudge'");

            File.WriteAllText(".gitbifrost", "# Add something meaningful here");
        }
    }
}
