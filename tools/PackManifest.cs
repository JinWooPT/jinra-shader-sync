// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace JinraShaderSync
{
    internal static class PackManifest
    {
        private static void Main(string[] args)
        {
            if (args.Length != 5 || !Regex.IsMatch(args[0], @"\A[0-9A-F]{16}\z"))
            {
                Console.Error.WriteLine("Usage: PackManifest TITLEID extracted-cache.bin archive.7z github-asset-name output.json");
                Environment.ExitCode = 1;
                return;
            }

            using (var cache = File.OpenRead(args[1]))
            using (var archive = File.OpenRead(args[2]))
            using (var sha = SHA256.Create())
            {
                var entries = ShaderCache.Scan(cache, true, CancellationToken.None);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string[] ids = entries.Select(entry => entry.Id).Where(seen.Add).ToArray();
                using (var output = File.Create(args[4]))
                {
                    var manifest = new Manifest {
                        Format = 1, TitleId = args[0], Version = ShaderCache.Version,
                        TotalEntries = ids.Length,
                        Chunks = new[] { new Chunk {
                            File = args[3], Tag = ShaderSync.CatalogTag, Size = checked((int)archive.Length),
                            Hash = ShaderCache.Hex(sha.ComputeHash(archive), 32), UnpackedSize = cache.Length,
                            Entries = ids.Length, Ids = ids
                        } }
                    };
                    new DataContractJsonSerializer(typeof(Manifest)).WriteObject(output, manifest);
                }
                using (var output = File.OpenRead(args[4]))
                    ShaderSync.ReadManifest(output, args[0]);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Validated {0} unique cache entries.", ids.Length));
            }
        }
    }
}

