// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace JinraShaderSync
{
    [DataContract]
    internal sealed class Manifest
    {
        [DataMember(Name = "format", IsRequired = true)] public int Format { get; internal set; }
        [DataMember(Name = "title_id", IsRequired = true)] public string TitleId { get; internal set; }
        [DataMember(Name = "source_version", IsRequired = true)] public int Version { get; internal set; }
        [DataMember(Name = "total_entries", IsRequired = true)] public int TotalEntries { get; internal set; }
        [DataMember(Name = "chunks", IsRequired = true)] public Chunk[] Chunks { get; internal set; }
    }

    [DataContract]
    internal sealed class Chunk
    {
        [DataMember(Name = "file", IsRequired = true)] public string File { get; internal set; }
        [DataMember(Name = "tag", IsRequired = true)] public string Tag { get; internal set; }
        [DataMember(Name = "sha256", IsRequired = true)] public string Hash { get; internal set; }
        [DataMember(Name = "size", IsRequired = true)] public int Size { get; internal set; }
        [DataMember(Name = "unpacked_size")] public long UnpackedSize { get; internal set; }
        [DataMember(Name = "entries", IsRequired = true)] public int Entries { get; internal set; }
        [DataMember(Name = "ids", IsRequired = true)] public string[] Ids { get; internal set; }

        internal bool IsArchive { get { return File.EndsWith(".7z", StringComparison.Ordinal); } }
    }

    internal sealed class SyncPlan
    {
        internal string CachePath;
        internal Chunk[] Chunks;
        internal int Missing;
        internal long DownloadBytes;
    }

    internal static class ShaderSync
    {
        internal const string Repository = "JinWooPT/jinra-shader-sync";
        internal const string CatalogTag = "shader-catalog-v54";
        internal const string Feed = "https://github.com/" + Repository;
        private const string Downloads = Feed + "/releases/download/";
        private const int MiB = 1024 * 1024;
        private static readonly Regex TitlePattern = new Regex(@"\A[0-9A-F]{16}\z");
        private static readonly Regex HashPattern = new Regex(@"\A[0-9a-f]{64}\z");
        private static readonly Regex IdPattern = new Regex(@"\A[0-9a-f]{32}\z");
        private static readonly Regex TagSuffixPattern = new Regex(@"\A[0-9]{8}[a-z0-9-]*\z");
        private static readonly Regex ArchiveNamePattern = new Regex(@"\A[A-Za-z0-9][A-Za-z0-9 ._-]{0,120}\.7z\z");

        internal static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jinra-Shader-Sync/1.1");
            return client;
        }

        internal static async Task<SyncPlan> CheckAsync(HttpClient client, string titleId,
                                                        string cachePath, CancellationToken cancellation)
        {
            if (!TitlePattern.IsMatch(titleId))
                throw new ArgumentException("The title ID must contain 16 hexadecimal characters.");

            byte[] bytes = await DownloadAsync(client, Downloads + CatalogTag + "/" + titleId + ".json",
                                               8 * MiB, true, cancellation).ConfigureAwait(false);
            if (bytes == null)
                return null;

            Manifest manifest;
            using (var stream = new MemoryStream(bytes, false))
                manifest = ReadManifest(stream, titleId);
            using (var cache = OpenCache(cachePath))
                return Plan(cachePath, manifest.Chunks, ReadIds(cache, cancellation));
        }

        internal static Manifest ReadManifest(Stream stream, string titleId)
        {
            var serializer = new DataContractJsonSerializer(typeof(Manifest),
                new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 400000 });
            var manifest = (Manifest)serializer.ReadObject(stream);
            if (manifest == null || manifest.Format != 1 || manifest.TitleId != titleId ||
                manifest.Version != ShaderCache.Version || manifest.Chunks == null)
                throw new InvalidDataException("The shared pack does not match this title or Jinra's cache version.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            string tagPrefix = titleId + "-";
            foreach (Chunk chunk in manifest.Chunks)
            {
                if (chunk == null || chunk.File == null || !HashPattern.IsMatch(chunk.Hash ?? "") ||
                    (chunk.Tag != CatalogTag && (chunk.Tag == null ||
                     !chunk.Tag.StartsWith(tagPrefix, StringComparison.Ordinal) ||
                     !TagSuffixPattern.IsMatch(chunk.Tag.Substring(tagPrefix.Length)))) ||
                    chunk.Size <= 0 || chunk.Ids == null ||
                    chunk.Entries <= 0 || chunk.Ids.Length != chunk.Entries)
                    throw new InvalidDataException("Invalid chunk in the shared catalog.");

                if (chunk.IsArchive ? !ArchiveNamePattern.IsMatch(chunk.File) || chunk.Size > 512 * MiB ||
                                      chunk.UnpackedSize < 12 || chunk.UnpackedSize > 8L * 1024 * MiB
                                    : chunk.File != chunk.Hash.Substring(0, 16) + ".gz" || chunk.Size > 64 * MiB)
                    throw new InvalidDataException("Invalid package name or size in the shared catalog.");

                foreach (string id in chunk.Ids)
                    if (!IdPattern.IsMatch(id ?? "") || !ids.Add(id))
                        throw new InvalidDataException("Invalid or repeated shader ID in the shared catalog.");
            }
            if (manifest.TotalEntries != ids.Count)
                throw new InvalidDataException("The shared catalog entry count is inconsistent.");
            return manifest;
        }

        private static FileStream OpenCache(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static HashSet<string> ReadIds(Stream cache, CancellationToken cancellation)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (cache != null)
                foreach (ShaderCache.Entry entry in ShaderCache.Scan(cache, true, cancellation))
                    ids.Add(entry.Id);
            return ids;
        }

        private static SyncPlan Plan(string path, Chunk[] chunks, HashSet<string> ids)
        {
            var pending = new List<Chunk>();
            var plan = new SyncPlan { CachePath = path };
            foreach (Chunk chunk in chunks)
            {
                int missing = chunk.Ids.Count(id => !ids.Contains(id));
                if (missing == 0)
                    continue;
                pending.Add(chunk);
                plan.Missing += missing;
                plan.DownloadBytes += chunk.Size;
            }
            plan.Chunks = pending.ToArray();
            return plan;
        }

        internal static async Task<int> ApplyAsync(HttpClient client, SyncPlan plan,
                                                   IProgress<string> progress, CancellationToken cancellation)
        {
            EnsureJinraClosed();
            using (var mutex = new Semaphore(1, 1, "Local\\JinraShaderSync"))
            {
                if (!mutex.WaitOne(0))
                    throw new IOException("Another Shader Sync operation is in progress.");
                try
                {
                    return await MergeAsync(client, plan, progress, cancellation).ConfigureAwait(false);
                }
                finally { mutex.Release(); }
            }
        }

        private static async Task<int> MergeAsync(HttpClient client, SyncPlan plan,
                                                  IProgress<string> progress, CancellationToken cancellation)
        {
            string path = plan.CachePath;
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string chunkPath = temp + ".chunk";
            using (var original = OpenCache(path))
            {
                var ids = ReadIds(original, cancellation);
                if (!plan.Chunks.SelectMany(chunk => chunk.Ids).Any(id => !ids.Contains(id)))
                    return 0;

                int added = 0;
                byte[] buffer = new byte[128 * 1024];
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                try
                {
                    using (var merged = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (original == null)
                            ShaderCache.WriteHeader(merged);
                        else
                        {
                            original.Position = 0;
                            ShaderCache.Copy(original, merged, original.Length, buffer, cancellation);
                        }

                        for (int i = 0; i < plan.Chunks.Length; i++)
                        {
                            Chunk chunk = plan.Chunks[i];
                            if (chunk.Ids.All(ids.Contains))
                                continue;
                            progress.Report(string.Format("Downloading and checking part {0} of {1}…", i + 1, plan.Chunks.Length));
                            using (var unpacked = new FileStream(chunkPath, FileMode.CreateNew, FileAccess.ReadWrite,
                                                                 FileShare.None, buffer.Length, FileOptions.DeleteOnClose))
                            {
                                var entries = await ReadChunkAsync(client, chunk, unpacked, temp, buffer,
                                                                  progress, cancellation).ConfigureAwait(false);
                                foreach (ShaderCache.Entry entry in entries)
                                {
                                    if (!ids.Add(entry.Id))
                                        continue;
                                    unpacked.Position = entry.Offset;
                                    ShaderCache.Copy(unpacked, merged, entry.Length, buffer, cancellation);
                                    added++;
                                }
                            }
                        }
                        merged.Flush(true);
                    }

                    cancellation.ThrowIfCancellationRequested();
                    EnsureJinraClosed();
                    if (original == null)
                        File.Move(temp, path);
                    else
                        File.Replace(temp, path, path + ".jinra-sync.bak");
                    return added;
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }
        }

        private static async Task<List<ShaderCache.Entry>> ReadChunkAsync(HttpClient client, Chunk chunk,
            Stream unpacked, string temp, byte[] buffer, IProgress<string> progress, CancellationToken cancellation)
        {
            string url = Downloads + chunk.Tag + "/" + Uri.EscapeDataString(chunk.File);
            if (chunk.IsArchive)
                await UnpackArchiveAsync(client, chunk, url, unpacked, temp + ".7z", buffer, progress,
                                         cancellation).ConfigureAwait(false);
            else
            {
                byte[] bytes = await DownloadAsync(client, url, chunk.Size, false, cancellation).ConfigureAwait(false);
                using (var compressed = new MemoryStream(bytes, false))
                using (var zip = new GZipStream(compressed, CompressionMode.Decompress))
                {
                    VerifyDownload(compressed, chunk);
                    int read;
                    while ((read = zip.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (unpacked.Length + read > 256L * MiB)
                            throw new InvalidDataException("A downloaded part is too large when unpacked.");
                        unpacked.Write(buffer, 0, read);
                    }
                }
            }
            var entries = ShaderCache.Scan(unpacked, chunk.IsArchive, cancellation);
            IEnumerable<string> actualIds = entries.Select(entry => entry.Id);
            if (chunk.IsArchive)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                actualIds = actualIds.Where(seen.Add);
            }
            if (!actualIds.SequenceEqual(chunk.Ids, StringComparer.Ordinal))
                throw new InvalidDataException("A downloaded part does not match the catalog's shader IDs.");
            return entries;
        }

        private static async Task UnpackArchiveAsync(HttpClient client, Chunk chunk, string url, Stream unpacked,
            string archivePath, byte[] buffer, IProgress<string> progress, CancellationToken cancellation)
        {
            try
            {
                using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    await DownloadToAsync(client, url, chunk.Size, false, archive, cancellation).ConfigureAwait(false);
                    VerifyDownload(archive, chunk);
                }

                progress.Report("Extracting and checking the transfer cache…");
                string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
                if (!File.Exists(tar))
                    throw new IOException("Windows tar.exe is required to open this shader pack.");

                // Read only the expected file. No archive-controlled paths are written to disk.
                var command = new ProcessStartInfo(tar, "-xOf \"" + archivePath + "\" -- " + ShaderCache.FileName) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                using (var process = Process.Start(command))
                {
                    try
                    {
                        var output = process.StandardOutput.BaseStream;
                        ShaderCache.Copy(output, unpacked, chunk.UnpackedSize, buffer, cancellation);
                        if (output.ReadByte() != -1)
                            throw new InvalidDataException("The extracted cache exceeds its catalog size.");
                        process.WaitForExit();
                        if (process.ExitCode != 0)
                            throw new InvalidDataException("Windows could not extract the shader cache.");
                    }
                    finally
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit();
                        }
                    }
                }
            }
            finally
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);
            }
        }

        private static void VerifyDownload(Stream data, Chunk chunk)
        {
            data.Position = 0;
            using (var sha = SHA256.Create())
                if (data.Length != chunk.Size || ShaderCache.Hex(sha.ComputeHash(data), 32) != chunk.Hash)
                    throw new InvalidDataException("A downloaded part failed its SHA-256 check.");
            data.Position = 0;
        }

        private static async Task<byte[]> DownloadAsync(HttpClient client, string url, int maxBytes,
                                                        bool allowMissing, CancellationToken cancellation)
        {
            using (var data = new MemoryStream())
                return await DownloadToAsync(client, url, maxBytes, allowMissing, data, cancellation)
                    .ConfigureAwait(false) ? data.ToArray() : null;
        }

        private static async Task<bool> DownloadToAsync(HttpClient client, string url, int maxBytes,
            bool allowMissing, Stream data, CancellationToken cancellation)
        {
            var uri = new Uri(url);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(maxBytes > 64 * MiB ? 30 : 5));
                for (int redirects = 0; redirects <= 4; redirects++)
                {
                    if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                        (uri.Host != "github.com" && uri.Host != "release-assets.githubusercontent.com" &&
                         uri.Host != "objects.githubusercontent.com" && uri.Host != "github-releases.githubusercontent.com"))
                        throw new InvalidDataException("The catalog redirected outside GitHub releases.");

                    using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                                                                deadline.Token).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 &&
                            response.Headers.Location != null)
                        {
                            uri = new Uri(uri, response.Headers.Location);
                            continue;
                        }
                        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound)
                            return false;
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > maxBytes)
                            throw new InvalidDataException("A download exceeds its expected size.");

                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            byte[] buffer = new byte[128 * 1024];
                            int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) != 0)
                            {
                                if (data.Length + read > maxBytes)
                                    throw new InvalidDataException("A download exceeds its expected size.");
                                data.Write(buffer, 0, read);
                            }
                            return true;
                        }
                    }
                }
            }
            throw new IOException("GitHub redirected the download too many times.");
        }

        private static void EnsureJinraClosed()
        {
            foreach (string name in new[] { "jinra", "jinra-cli" })
            {
                Process[] processes = Process.GetProcessesByName(name);
                foreach (Process process in processes)
                    process.Dispose();
                if (processes.Length != 0)
                    throw new IOException("Close Jinra before syncing its shader cache.");
            }
        }
    }
}

