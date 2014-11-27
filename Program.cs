/* Copyright (c) 2014 Luminawesome Games Ltd. All Rights Reserved. */

using System;
using System.IO;

namespace GitBifrost
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args[0] == "activate")
            {
                Activate();
            }
        }

        static void Activate()
        {
            Console.WriteLine("Bifrost is now active");

            File.WriteAllText(".gitbifrost", "# Add something meaningful here");
        }
    }
}
