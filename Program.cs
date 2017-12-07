using System;
using NDesk.Options;

namespace YSync {
    using Core;
    using System.Collections.Generic;

    class FileChangesSync {
        static string User;
        static string SourcePath;
        static string DestinationPath;
        static string Host;
        static string PrivateKey;
        static string Passphrase;
        static int Verbosity = 0;
        static string Excludes = "";
        static bool DryRun = false;

        static void Main(string[] args) {
            if (!ParseArgs(args)) {
                return;
            }

            var changesSync = new SimpleFileChangesSync() {
                User = User,
                SourcePath = SourcePath,
                DestinationPath = DestinationPath,
                Host = Host,
                PrivateKey = PrivateKey,
                Passphrase = Passphrase,
                Verbosity = Verbosity,
                Excludes = new HashSet<string>(Excludes.Split(',')),
                DryRun = DryRun
            };

            changesSync.Run();
        }

        static bool ParseArgs(string[] args) {
            bool help = false;
            var opts = new OptionSet() {
                { "u|user=", "user name", v => User = v },
                { "s|src=", "source path", v => SourcePath = v },
                { "d|dst=", "destination path in remote host", v => DestinationPath = v },
                { "x|host=", "remote host", v => Host = v },
                { "k|key=", "path to private key", v => PrivateKey = v },
                { "p|passphrase=", "passphrase for key", v => Passphrase = v },
                { "e|exclude=", "comma-separated exclude name list", v => Excludes = v },
                { "v", "verbosity level 1", v => Verbosity = 1 },
                { "V", "verbosity level 2", v => Verbosity = 2 },
                { "dry-run", "fake transfer mode", v => DryRun = true },
                { "h|help",  "show this message and exit", v => help = v != null },
            };

            opts.Parse(args);
            if (help) {
                ShowHelp(opts);
                return false;
            }

            bool unsuficientArgs = false;
            if (User == null) {
                Console.WriteLine("ERROR: User is not set");
                unsuficientArgs = true;
            }

            if (SourcePath == null) {
                Console.WriteLine("ERROR: Source path is not set");
                unsuficientArgs = true;
            }

            if (DestinationPath == null) {
                Console.WriteLine("ERROR: Destination path is not set");
                unsuficientArgs = true;
            }

            if (Host == null) {
                Console.WriteLine("ERROR: Host is not set");
                unsuficientArgs = true;
            }

            if (PrivateKey == null) {
                Console.WriteLine("ERROR: Path to private key is not set");
                unsuficientArgs = true;
            }

            if (unsuficientArgs) {
                ShowHelp(opts);
                return false;
            }

            return true;
        }

        static void ShowHelp(OptionSet opts) {
            Console.WriteLine("Usage: ysync.exe [OPTIONS]");
            Console.WriteLine("Options:");
            opts.WriteOptionDescriptions(Console.Out);
        }
    }
}
