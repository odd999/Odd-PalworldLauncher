using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;

namespace PalworldLauncher
{
    public class ModInfo
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Hash { get; set; } = "";
        public string TargetDir { get; set; } = "Pal/Content/Paks/~mods";
    }

    public class ModManifest
    {
        public string ServerIp { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 8211;
        public string ServerPassword { get; set; } = "";
        public string AuthApiUrl { get; set; } = "";
        public string ApiSecret { get; set; } = "";
        public string WorkshopCollectionId { get; set; } = "";
        public List<ModInfo> Mods { get; set; } = new List<ModInfo>();
        public List<string> CleanDirs { get; set; } = new List<string> { "Pal/Content/Paks/~mods" };
        public List<string> DllCleanupList { get; set; } = new List<string> { "dwmapi.dll", "dxgi.dll", "UE4SS.dll", "version.dll", "winmm.dll" };
        public List<string> ApprovedWorkshopIds { get; set; } = new List<string>();
        public List<string> ApprovedManagedMods { get; set; } = new List<string>();
        public List<string> ApprovedPakMods { get; set; } = new List<string>();
        public List<string> ApprovedNativeMods { get; set; } = new List<string>();
        public List<string> ApprovedUe4ssMods { get; set; } = new List<string>();
        public List<string> ApprovedLogicMods { get; set; } = new List<string>();
        public List<string> ApprovedUe4ssRootFiles { get; set; } = new List<string>();
        public List<string> ApprovedPalSchemaMods { get; set; } = new List<string>();
    }

    public class LauncherLogic
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        public static string TryGetGameInstallPath()
        {
            // Search Registry locations
            string[] registryPaths = new[]
            {
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1623730",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1623730",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1623730"
            };

            foreach (var keyPath in registryPaths)
            {
                try
                {
                    string path = Registry.GetValue(keyPath, "InstallLocation", null) as string;
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && File.Exists(Path.Combine(path, "Palworld.exe")))
                    {
                        return path;
                    }
                }
                catch { }
            }

            // Secondary check: look up standard Steam location if the registry keys were missing
            try
            {
                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(steamPath))
                {
                    string standardPath = Path.Combine(steamPath, @"steamapps\common\Palworld");
                    if (Directory.Exists(standardPath) && File.Exists(Path.Combine(standardPath, "Palworld.exe")))
                    {
                        return standardPath;
                    }
                }
            }
            catch { }

            return "";
        }

        public static async Task<ModManifest> FetchManifestAsync(string url)
        {
            try
            {
                string json = await HttpClient.GetStringAsync(url);
                var manifest = JsonSerializer.Deserialize<ModManifest>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                });
                return manifest;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to download manifest: {ex.Message}", ex);
            }
        }

        public static string ConvertGoogleDriveLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            if (url.Contains("drive.google.com") && url.Contains("/file/d/"))
            {
                int startIdx = url.IndexOf("/file/d/") + "/file/d/".Length;
                int endIdx = url.IndexOf("/", startIdx);
                if (endIdx == -1)
                {
                    endIdx = url.IndexOf("?", startIdx);
                }
                string fileId = endIdx == -1 ? url.Substring(startIdx) : url.Substring(startIdx, endIdx - startIdx);
                return $"https://docs.google.com/uc?export=download&id={fileId}&confirm=t";
            }

            return url;
        }

        public static async Task<HttpResponseMessage> GetGoogleDriveResponseAsync(string url)
        {
            var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if (response.IsSuccessStatusCode &&
                response.Content.Headers.ContentType != null &&
                response.Content.Headers.ContentType.MediaType != null &&
                response.Content.Headers.ContentType.MediaType.Contains("html"))
            {
                string html = await response.Content.ReadAsStringAsync();
                if (html.Contains("Virus scan warning") || html.Contains("download-form"))
                {
                    string actionUrl = "https://drive.usercontent.google.com/download";
                    var actionMatch = System.Text.RegularExpressions.Regex.Match(html, "action=[\"']([^\"']+)[\"']");
                    if (actionMatch.Success)
                    {
                        actionUrl = System.Net.WebUtility.HtmlDecode(actionMatch.Groups[1].Value);
                    }

                    var inputMatches = System.Text.RegularExpressions.Regex.Matches(html, "<input type=[\"']hidden[\"'] name=[\"']([^\"']+)[\"'] value=[\"']([^\"']+)[\"']>");
                    var queryParams = new List<string>();
                    foreach (System.Text.RegularExpressions.Match match in inputMatches)
                    {
                        string name = match.Groups[1].Value;
                        string val = match.Groups[2].Value;
                        queryParams.Add($"{name}={System.Uri.EscapeDataString(val)}");
                    }

                    if (queryParams.Count > 0)
                    {
                        string separator = actionUrl.Contains("?") ? "&" : "?";
                        string finalUrl = actionUrl + separator + string.Join("&", queryParams);
                        System.Diagnostics.Debug.WriteLine($"Redirecting Google Drive download to: {finalUrl}");

                        response.Dispose();
                        return await HttpClient.GetAsync(finalUrl, HttpCompletionOption.ResponseHeadersRead);
                    }
                }
            }

            return response;
        }

        public static async Task<long> GetRemoteFileSizeAsync(string url)
        {
            try
            {
                url = ConvertGoogleDriveLink(url);
                using (var response = await GetGoogleDriveResponseAsync(url))
                {
                    if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
                    {
                        return response.Content.Headers.ContentLength.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to get remote file size: {ex.Message}");
            }

            return -1;
        }

        public static async Task<bool> IsSyncRequiredAsync(string gamePath, ModManifest manifest)
        {
            if (manifest.Mods == null || manifest.Mods.Count == 0)
                return false;

            foreach (var mod in manifest.Mods)
            {
                string destDir = Path.Combine(gamePath, mod.TargetDir.Replace('/', '\\'));
                string destFile = Path.Combine(destDir, mod.Name);

                if (!File.Exists(destFile))
                    return true;

                // Dynamically check remote file size
                long remoteSize = await GetRemoteFileSizeAsync(mod.Url);
                if (remoteSize > 0)
                {
                    var fileInfo = new FileInfo(destFile);
                    if (fileInfo.Length != remoteSize)
                    {
                        System.Diagnostics.Debug.WriteLine($"Mod sync required: Local size {fileInfo.Length} != Remote size {remoteSize}");
                        return true;
                    }
                }
                else
                {
                    // Fallback to static checks
                    bool isSizeTimestampHash = mod.Hash.Contains("_");
                    if (isSizeTimestampHash)
                    {
                        var parts = mod.Hash.Split('_');
                        if (parts.Length == 2 && long.TryParse(parts[0], out long expectedSize))
                        {
                            var fileInfo = new FileInfo(destFile);
                            if (fileInfo.Length != expectedSize)
                                return true;
                        }
                    }
                    else if (!string.IsNullOrEmpty(mod.Hash))
                    {
                        string localHash = ComputeSHA256(destFile);
                        if (!string.Equals(localHash, mod.Hash, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            return false;
        }

        public static void ClearAllModFolders(string gamePath)
        {
            string[] directoriesToClear = new[]
            {
                Path.Combine(gamePath, @"Pal\Content\Paks\LogicMods"),
                Path.Combine(gamePath, @"Pal\Content\Paks\~mods"),
                Path.Combine(gamePath, @"Pal\Binaries\Win64\ue4ss\Mods"),
            };

            foreach (var dir in directoriesToClear)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                        System.Diagnostics.Debug.WriteLine($"Cleared directory completely: {dir}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to clear directory {dir}: {ex.Message}");
                    }
                }
            }
        }

        public static bool HasUnapprovedMods(string gamePath, ModManifest manifest)
        {
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                return false;

            // 1. Check ~mods and LogicMods
            var approvedPak = new HashSet<string>(manifest.ApprovedPakMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var approvedLogic = new HashSet<string>(manifest.ApprovedLogicMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // Check Pal\Content\Paks\~mods
            string modsPath = Path.Combine(gamePath, @"Pal\Content\Paks\~mods");
            if (Directory.Exists(modsPath))
            {
                foreach (var file in Directory.GetFiles(modsPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!approvedPak.Contains(fileName))
                        return true;
                }
            }

            // Check Pal\Content\Paks\LogicMods
            string logicModsPath = Path.Combine(gamePath, @"Pal\Content\Paks\LogicMods");
            if (Directory.Exists(logicModsPath))
            {
                foreach (var file in Directory.GetFiles(logicModsPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!approvedLogic.Contains(fileName))
                        return true;
                }
            }

            // 2. Check ue4ss Mods
            string ue4ssModsPath = Path.Combine(gamePath, @"Pal\Binaries\Win64\ue4ss\Mods");
            if (Directory.Exists(ue4ssModsPath))
            {
                var approvedUe4ss = new HashSet<string>(manifest.ApprovedUe4ssMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                approvedUe4ss.Add("shared");

                foreach (var dir in Directory.GetDirectories(ue4ssModsPath))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!approvedUe4ss.Contains(dirName))
                        return true;
                }

                foreach (var file in Directory.GetFiles(ue4ssModsPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!approvedUe4ss.Contains(fileName))
                        return true;
                }
            }

            // 3. Check ue4ss root
            string ue4ssRootPath = Path.Combine(gamePath, @"Pal\Binaries\Win64\ue4ss");
            if (Directory.Exists(ue4ssRootPath))
            {
                var approvedUe4ssRoot = new HashSet<string>(manifest.ApprovedUe4ssRootFiles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(ue4ssRootPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!approvedUe4ssRoot.Contains(fileName))
                        return true;
                }
            }

            // 4. Check PalSchema mods
            string palSchemaModsPath = Path.Combine(gamePath, @"Pal\Binaries\Win64\ue4ss\Mods\PalSchema\mods");
            if (Directory.Exists(palSchemaModsPath))
            {
                var approvedPalSchema = new HashSet<string>(manifest.ApprovedPalSchemaMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var dir in Directory.GetDirectories(palSchemaModsPath))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!approvedPalSchema.Contains(dirName))
                        return true;
                }

                foreach (var file in Directory.GetFiles(palSchemaModsPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!approvedPalSchema.Contains(fileName))
                        return true;
                }
            }

            // 5. Check DLLs in Pal\Binaries\Win64\
            string win64Path = Path.Combine(gamePath, @"Pal\Binaries\Win64");
            if (Directory.Exists(win64Path) && manifest.DllCleanupList != null)
            {
                foreach (var dll in manifest.DllCleanupList)
                {
                    string dllPath = Path.Combine(win64Path, dll);
                    if (File.Exists(dllPath))
                        return true;
                }
            }

            return false;
        }

        public static async Task SyncAndCleanAsync(
            string gamePath,
            ModManifest manifest,
            Action<string, double> progressReporter,
            bool forceCleanInstall = false)
        {
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                throw new DirectoryNotFoundException("Invalid Palworld installation path.");

            if (forceCleanInstall)
            {
                progressReporter("Performing clean mod installation...", 2);
                ClearAllModFolders(gamePath);
            }

            // 1. Download and sync all approved mods first (so they are available locally before clean/extraction)
            double totalMods = manifest.Mods.Count;
            var downloadedModFiles = new List<(ModInfo Mod, string FilePath)>();

            if (totalMods > 0)
            {
                for (int i = 0; i < manifest.Mods.Count; i++)
                {
                    var mod = manifest.Mods[i];
                    double progressStart = 5.0 + (i / totalMods) * 45.0;
                    double progressEnd = 5.0 + ((i + 1) / totalMods) * 45.0;

                    progressReporter($"Checking mod: {mod.Name}...", progressStart);

                    string destDir = Path.Combine(gamePath, mod.TargetDir.Replace('/', '\\'));
                    if (!Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    string destFile = Path.Combine(destDir, mod.Name);
                    downloadedModFiles.Add((mod, destFile));

                    bool needsDownload = true;
                    bool isSizeTimestampHash = mod.Hash.Contains("_");
                    long expectedSize = 0;
                    long expectedTimestamp = 0;

                    if (isSizeTimestampHash)
                    {
                        var parts = mod.Hash.Split('_');
                        if (parts.Length == 2 && long.TryParse(parts[0], out expectedSize) && long.TryParse(parts[1], out expectedTimestamp))
                        {
                            // Valid size_timestamp hash
                        }
                        else
                        {
                            isSizeTimestampHash = false; // Fall back to string comparison if format is invalid
                        }
                    }

                    if (File.Exists(destFile))
                    {
                        if (isSizeTimestampHash)
                        {
                            var fileInfo = new FileInfo(destFile);
                            long localSize = fileInfo.Length;
                            long localTimestamp = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                            
                            // Check if size matches, and timestamp is within a small tolerance (2 seconds / 2000 ms to avoid filesystem accuracy quirks)
                            if (localSize == expectedSize && Math.Abs(localTimestamp - expectedTimestamp) < 2000)
                            {
                                needsDownload = false;
                            }
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(mod.Hash))
                            {
                                long remoteSize = await GetRemoteFileSizeAsync(mod.Url);
                                if (remoteSize > 0)
                                {
                                    var fileInfo = new FileInfo(destFile);
                                    if (fileInfo.Length == remoteSize)
                                    {
                                        needsDownload = false;
                                    }
                                }
                                else
                                {
                                    needsDownload = false;
                                }
                            }
                            else
                            {
                                string localHash = ComputeSHA256(destFile);
                                if (string.Equals(localHash, mod.Hash, StringComparison.OrdinalIgnoreCase))
                                {
                                    needsDownload = false;
                                }
                            }
                        }
                    }

                    if (needsDownload)
                    {
                        progressReporter($"Downloading mod: {mod.Name}...", progressStart);
                        await DownloadFileAsync(ConvertGoogleDriveLink(mod.Url), destFile, (pct) =>
                        {
                            double overallPct = progressStart + (pct / 100.0) * (progressEnd - progressStart);
                            progressReporter($"Downloading mod: {mod.Name} ({pct:F0}%)...", overallPct);
                        });

                        // Set the file modification time to match the Google Drive timestamp if using size_timestamp hash
                        if (isSizeTimestampHash)
                        {
                            try
                            {
                                var utcDateTime = DateTimeOffset.FromUnixTimeMilliseconds(expectedTimestamp).UtcDateTime;
                                File.SetLastWriteTimeUtc(destFile, utcDateTime);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to set file timestamp: {ex.Message}");
                            }

                            // Re-verify size after download
                            var fileInfo = new FileInfo(destFile);
                            if (fileInfo.Length != expectedSize)
                            {
                                throw new Exception($"File size verification failed for mod {mod.Name}. Got {fileInfo.Length} bytes, expected {expectedSize} bytes.");
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(mod.Hash))
                            {
                                // Verify SHA-256 hash after download
                                string localHash = ComputeSHA256(destFile);
                                if (!string.Equals(localHash, mod.Hash, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new Exception($"Hash verification failed for mod {mod.Name}. Got {localHash}, expected {mod.Hash}");
                                }
                            }
                        }
                    }
                }
            }

            // 2. Scan downloaded zip files to dynamically whitelist their contents before cleanups
            progressReporter("Scanning mod archives...", 50);
            foreach (var (mod, filePath) in downloadedModFiles)
            {
                if (mod.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ScanZipEntries(filePath, manifest);
                }
            }

            // 3. Clean Dlls/folders to prevent unauthorized script injections
            progressReporter("Cleaning unauthorized injections...", 60);
            CleanInjections(gamePath, manifest);

            // 4. Clear out any unapproved .pak files in target dirs and clean workshop/native/managed directories
            progressReporter("Cleaning unapproved mods...", 70);
            CleanMods(gamePath, manifest);
            CleanAllModsDynamic(gamePath, manifest);

            // 5. Extract mod zip files (which will overwrite any custom/changed files inside the game directory)
            progressReporter("Extracting mod archives...", 80);
            foreach (var (mod, filePath) in downloadedModFiles)
            {
                if (mod.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string destDir = Path.Combine(gamePath, mod.TargetDir.Replace('/', '\\'));
                    ExtractZip(filePath, gamePath, destDir);
                }
            }

            // 6. Configure Engine.ini for Fast Travel mod safety
            progressReporter("Configuring engine settings...", 90);
            ConfigureEngineIniForFastTravel();

            // 7. Configure Technology mod settings
            progressReporter("Configuring technology settings...", 95);
            ConfigureTechnologyMod(gamePath);

            progressReporter("Verification and sync complete!", 100);
        }

        private static void ScanZipEntries(string zipPath, ModManifest manifest)
        {
            try
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string entryPath = entry.FullName.Replace('/', '\\');

                        // 1. Check for PAK files in Pal/Content/Paks/~mods
                        if (entryPath.StartsWith(@"Pal\Content\Paks\~mods\", StringComparison.OrdinalIgnoreCase) && 
                            entryPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                        {
                            string pakName = Path.GetFileName(entryPath);
                            if (!string.IsNullOrEmpty(pakName))
                            {
                                if (manifest.ApprovedPakMods == null) manifest.ApprovedPakMods = new List<string>();
                                if (!manifest.ApprovedPakMods.Contains(pakName))
                                {
                                    manifest.ApprovedPakMods.Add(pakName);
                                    System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted PAK: {pakName}");
                                }
                            }
                        }

                        // 1.5 Check for files in Pal/Content/Paks/LogicMods
                        if (entryPath.StartsWith(@"Pal\Content\Paks\LogicMods\", StringComparison.OrdinalIgnoreCase))
                        {
                            string fileName = Path.GetFileName(entryPath);
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                if (manifest.ApprovedLogicMods == null) manifest.ApprovedLogicMods = new List<string>();
                                if (!manifest.ApprovedLogicMods.Contains(fileName))
                                {
                                    manifest.ApprovedLogicMods.Add(fileName);
                                    System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted LogicMod: {fileName}");
                                }
                            }
                        }

                        // 2. Check for ManagedMods folders: Pal\Binaries\Win64\Mods\ManagedMods\<ModName>\
                        if (entryPath.StartsWith(@"Pal\Binaries\Win64\Mods\ManagedMods\", StringComparison.OrdinalIgnoreCase))
                        {
                            string relative = entryPath.Substring(@"Pal\Binaries\Win64\Mods\ManagedMods\".Length);
                            string[] parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                string modFolderName = parts[0];
                                if (manifest.ApprovedManagedMods == null) manifest.ApprovedManagedMods = new List<string>();
                                if (!manifest.ApprovedManagedMods.Contains(modFolderName))
                                {
                                    manifest.ApprovedManagedMods.Add(modFolderName);
                                    System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted Managed Mod: {modFolderName}");
                                }
                            }
                        }

                        // 3. Check for NativeMods folders: Pal\Binaries\Win64\Mods\NativeMods\<ModName>\
                        if (entryPath.StartsWith(@"Pal\Binaries\Win64\Mods\NativeMods\", StringComparison.OrdinalIgnoreCase))
                        {
                            string relative = entryPath.Substring(@"Pal\Binaries\Win64\Mods\NativeMods\".Length);
                            string[] parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                string modFolderName = parts[0];
                                if (manifest.ApprovedNativeMods == null) manifest.ApprovedNativeMods = new List<string>();
                                if (!manifest.ApprovedNativeMods.Contains(modFolderName))
                                {
                                    manifest.ApprovedNativeMods.Add(modFolderName);
                                    System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted Native Mod: {modFolderName}");
                                }
                            }
                        }

                        // 3.5 Check for folders and configuration files inside Pal/Binaries/Win64/ue4ss/Mods/
                        if (entryPath.StartsWith(@"Pal\Binaries\Win64\ue4ss\Mods\", StringComparison.OrdinalIgnoreCase))
                        {
                            string relative = entryPath.Substring(@"Pal\Binaries\Win64\ue4ss\Mods\".Length);
                            string[] parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                string modFolderName = parts[0];
                                if (manifest.ApprovedUe4ssMods == null) manifest.ApprovedUe4ssMods = new List<string>();
                                if (parts.Length == 1)
                                {
                                    string fileName = parts[0];
                                    if (!manifest.ApprovedUe4ssMods.Contains(fileName))
                                    {
                                        manifest.ApprovedUe4ssMods.Add(fileName);
                                        System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted UE4SS Mod File: {fileName}");
                                    }
                                }
                                else
                                {
                                    if (!manifest.ApprovedUe4ssMods.Contains(modFolderName))
                                    {
                                        manifest.ApprovedUe4ssMods.Add(modFolderName);
                                        System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted UE4SS Mod Folder: {modFolderName}");
                                    }
                                }
                            }
                        }

                        // 3.6 Check for files in Pal/Binaries/Win64/ue4ss/
                        if (entryPath.StartsWith(@"Pal\Binaries\Win64\ue4ss\", StringComparison.OrdinalIgnoreCase) &&
                            !entryPath.StartsWith(@"Pal\Binaries\Win64\ue4ss\Mods\", StringComparison.OrdinalIgnoreCase))
                        {
                            string fileName = Path.GetFileName(entryPath);
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                if (manifest.ApprovedUe4ssRootFiles == null) manifest.ApprovedUe4ssRootFiles = new List<string>();
                                if (!manifest.ApprovedUe4ssRootFiles.Contains(fileName))
                                {
                                    manifest.ApprovedUe4ssRootFiles.Add(fileName);
                                    System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted UE4SS root file: {fileName}");
                                }
                            }
                        }

                        // 3.7 Check for folders and files inside Pal/Binaries/Win64/ue4ss/Mods/PalSchema/mods/
                        if (entryPath.StartsWith(@"Pal\Binaries\Win64\ue4ss\Mods\PalSchema\mods\", StringComparison.OrdinalIgnoreCase))
                        {
                            string relative = entryPath.Substring(@"Pal\Binaries\Win64\ue4ss\Mods\PalSchema\mods\".Length);
                            string[] parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                string subFolderName = parts[0];
                                if (manifest.ApprovedPalSchemaMods == null) manifest.ApprovedPalSchemaMods = new List<string>();
                                if (parts.Length == 1)
                                {
                                    string fileName = parts[0];
                                    if (!manifest.ApprovedPalSchemaMods.Contains(fileName))
                                    {
                                        manifest.ApprovedPalSchemaMods.Add(fileName);
                                        System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted PalSchema Mod File: {fileName}");
                                    }
                                }
                                else
                                {
                                    if (!manifest.ApprovedPalSchemaMods.Contains(subFolderName))
                                    {
                                        manifest.ApprovedPalSchemaMods.Add(subFolderName);
                                        System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted PalSchema Mod Folder: {subFolderName}");
                                    }
                                }
                            }
                        }

                        // 4. Check for DLLs in Pal\Binaries\Win64\
                        if (entryPath.StartsWith(@"Pal\Binaries\Win64\", StringComparison.OrdinalIgnoreCase) && 
                            !entryPath.Substring(@"Pal\Binaries\Win64\".Length).Contains("\\") &&
                            entryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            string dllName = Path.GetFileName(entryPath);
                            if (!string.IsNullOrEmpty(dllName))
                            {
                                if (manifest.DllCleanupList != null && manifest.DllCleanupList.Contains(dllName))
                                {
                                    manifest.DllCleanupList.Remove(dllName);
                                    System.Diagnostics.Debug.WriteLine($"Dynamically whitelisted DLL from cleanup list: {dllName}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning zip entries for {zipPath}: {ex.Message}");
            }
        }

        private static bool IsLockedConfigOrModFile(string relativePath)
        {
            string normalized = relativePath.Replace('/', '\\');

            // 1. Locked Mods specified by the user (matched anywhere in the path or filename)
            if (normalized.Contains("MoreTechnologyPointsx8", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("ExtendedBaseRange", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("vuxWeightCapacityIncrease10k", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Locked PalAnalyzer configuration json file
            if (normalized.Contains("PalAnalyzerConfig.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 3. For any other mod configuration files:
            // Allow users to edit config files (having config/settings in name or standard config extensions)
            string ext = Path.GetExtension(normalized).ToLower();
            if (ext == ".ini" || ext == ".json" || ext == ".cfg" || ext == ".conf" || ext == ".txt" ||
                normalized.Contains("config", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("settings", StringComparison.OrdinalIgnoreCase))
            {
                return false; // Allowed to edit (NOT locked)
            }

            // By default, mod files (.pak, .lua scripts, dlls, asset files) are locked and always overwritten to prevent cheating/broken mods.
            return true;
        }

        private static void ExtractZip(string zipPath, string gamePath, string destDir)
        {
            try
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    // Check if the zip file contains a "Pal" directory structure
                    bool hasPalPrefix = false;
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.StartsWith("Pal/", StringComparison.OrdinalIgnoreCase) || 
                            entry.FullName.StartsWith(@"Pal\", StringComparison.OrdinalIgnoreCase))
                        {
                            hasPalPrefix = true;
                            break;
                        }
                    }

                    string targetExtractPath = hasPalPrefix ? gamePath : destDir;

                    foreach (var entry in archive.Entries)
                    {
                        // Skip directory entries (they end with / or \)
                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                            continue;

                        // Skip Windows desktop.ini system files
                        if (entry.FullName.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string destinationPath = Path.GetFullPath(Path.Combine(targetExtractPath, entry.FullName));

                        // Ensure target directory exists
                        string? dir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        // Overwrite file only if it doesn't exist yet OR is locked
                        if (!File.Exists(destinationPath) || IsLockedConfigOrModFile(entry.FullName))
                        {
                            entry.ExtractToFile(destinationPath, overwrite: true);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipping extraction of editable configuration file: {entry.FullName}");
                        }
                    }
                }
                System.Diagnostics.Debug.WriteLine($"Successfully extracted zip mod: {zipPath}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to extract mod zip file '{Path.GetFileName(zipPath)}': {ex.Message}", ex);
            }
        }

        private static void CleanInjections(string gamePath, ModManifest manifest)
        {
            string win64Path = Path.Combine(gamePath, @"Pal\Binaries\Win64");
            if (!Directory.Exists(win64Path)) return;

            // Delete unauthorized DLL files
            var dllsToClean = manifest.DllCleanupList ?? new List<string> { "dwmapi.dll", "dxgi.dll", "UE4SS.dll", "version.dll", "winmm.dll" };
            foreach (var dll in dllsToClean)
            {
                string dllPath = Path.Combine(win64Path, dll);
                if (File.Exists(dllPath))
                {
                    try
                    {
                        // Remove Read-Only attribute if set
                        var attributes = File.GetAttributes(dllPath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(dllPath, attributes & ~FileAttributes.ReadOnly);
                        }

                        File.Delete(dllPath);
                        System.Diagnostics.Debug.WriteLine($"Cleaned injection DLL: {dll}");
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Failed to remove unauthorized file '{dll}' from your game directory. Please close any running game processes and delete it manually from '{win64Path}' or run the launcher as Administrator. Error: {ex.Message}");
                    }
                }
            }

            // Delete standard Mods folder if present (used by UE4SS/Lua mods)
            string ue4ssModsFolder = Path.Combine(win64Path, "Mods");
            if (Directory.Exists(ue4ssModsFolder))
            {
                bool isOldSetupApproved = (manifest.ApprovedManagedMods != null && manifest.ApprovedManagedMods.Count > 0) ||
                                          (manifest.ApprovedNativeMods != null && manifest.ApprovedNativeMods.Count > 0);
                if (!isOldSetupApproved)
                {
                    try
                    {
                        Directory.Delete(ue4ssModsFolder, true);
                        System.Diagnostics.Debug.WriteLine("Cleaned UE4SS Mods directory.");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to delete Mods directory: {ex.Message}");
                    }
                }
            }
        }

        private static void CleanAllModsDynamic(string gamePath, ModManifest manifest)
        {
            try
            {
                string workshopPath = Path.GetFullPath(Path.Combine(gamePath, @"..\..\workshop\content\1623730"));
                var approvedIds = new HashSet<string>(manifest.ApprovedWorkshopIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                // 1. Clean the Workshop cache folder first
                if (Directory.Exists(workshopPath))
                {
                    var directories = Directory.GetDirectories(workshopPath);
                    foreach (var dir in directories)
                    {
                        string dirName = Path.GetFileName(dir);
                        if (!approvedIds.Contains(dirName))
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                                System.Diagnostics.Debug.WriteLine($"Cleaned unapproved Workshop Mod ID: {dirName}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete Workshop Mod ID {dirName}: {ex.Message}");
                            }
                        }
                    }
                }

                // 2. Resolve approved package names and types from remaining Workshop folders
                var approvedManaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var approvedNative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Always allow native UE4SS framework if present natively
                approvedNative.Add("UE4SS");

                if (Directory.Exists(workshopPath))
                {
                    var directories = Directory.GetDirectories(workshopPath);
                    foreach (var dir in directories)
                    {
                        string infoJsonPath = Path.Combine(dir, "Info.json");
                        if (File.Exists(infoJsonPath))
                        {
                            try
                            {
                                string json = File.ReadAllText(infoJsonPath);
                                using (var doc = JsonDocument.Parse(json))
                                {
                                    var root = doc.RootElement;
                                    if (root.TryGetProperty("PackageName", out var pkgProp))
                                    {
                                        string packageName = pkgProp.GetString();
                                        if (!string.IsNullOrEmpty(packageName))
                                        {
                                            bool isNative = false;
                                            if (root.TryGetProperty("InstallRule", out var ruleProp) && 
                                                ruleProp.ValueKind == JsonValueKind.Array && 
                                                ruleProp.GetArrayLength() > 0)
                                            {
                                                var firstRule = ruleProp[0];
                                                if (firstRule.TryGetProperty("Type", out var typeProp))
                                                {
                                                    string typeStr = typeProp.GetString();
                                                    if (string.Equals(typeStr, "UE4SS", StringComparison.OrdinalIgnoreCase) || 
                                                        string.Equals(typeStr, "Native", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        isNative = true;
                                                    }
                                                }
                                            }

                                            if (isNative)
                                            {
                                                approvedNative.Add(packageName);
                                            }
                                            else
                                            {
                                                approvedManaged.Add(packageName);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error reading Info.json in {dir}: {ex.Message}");
                            }
                        }
                    }
                }

                // Add manual explicit overrides if owner still wants local folder whitelists
                if (manifest.ApprovedManagedMods != null)
                {
                    foreach (var name in manifest.ApprovedManagedMods) approvedManaged.Add(name);
                }
                if (manifest.ApprovedNativeMods != null)
                {
                    foreach (var name in manifest.ApprovedNativeMods) approvedNative.Add(name);
                }

                // 2.5 Clean the new UE4SS Mods folder (Pal\Binaries\Win64\ue4ss\Mods)
                string ue4ssModsPath = Path.Combine(gamePath, @"Pal\Binaries\Win64\ue4ss\Mods");
                if (Directory.Exists(ue4ssModsPath))
                {
                    var approvedUe4ss = new HashSet<string>(manifest.ApprovedUe4ssMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    
                    // Always allow shared directory
                    approvedUe4ss.Add("shared");

                    // List all subdirectories under ue4ss\Mods
                    var directories = Directory.GetDirectories(ue4ssModsPath);
                    foreach (var dir in directories)
                    {
                        string dirName = Path.GetFileName(dir);
                        if (!approvedUe4ss.Contains(dirName))
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                                System.Diagnostics.Debug.WriteLine($"Cleaned unapproved UE4SS Mod Folder: {dirName}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete UE4SS Mod Folder {dirName}: {ex.Message}");
                            }
                        }
                    }

                    // List all files directly under ue4ss\Mods (like mods.txt, mods.json)
                    var files = Directory.GetFiles(ue4ssModsPath);
                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!approvedUe4ss.Contains(fileName))
                        {
                            try
                            {
                                File.Delete(file);
                                System.Diagnostics.Debug.WriteLine($"Cleaned unapproved UE4SS Mod File directly in Mods: {fileName}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete UE4SS Mod File {fileName}: {ex.Message}");
                            }
                        }
                    }
                }

                // 2.6 Clean unapproved files inside the ue4ss root folder (Pal\Binaries\Win64\ue4ss)
                string ue4ssRootPath = Path.Combine(gamePath, @"Pal\Binaries\Win64\ue4ss");
                if (Directory.Exists(ue4ssRootPath))
                {
                    var approvedUe4ssRoot = new HashSet<string>(manifest.ApprovedUe4ssRootFiles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    
                    var files = Directory.GetFiles(ue4ssRootPath);
                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!approvedUe4ssRoot.Contains(fileName))
                        {
                            try
                            {
                                File.Delete(file);
                                System.Diagnostics.Debug.WriteLine($"Cleaned unapproved UE4SS root file: {fileName}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete UE4SS root file {fileName}: {ex.Message}");
                            }
                        }
                    }
                }

                // 2.7 Clean the new PalSchema Mods folder (Pal\Binaries\Win64\ue4ss\Mods\PalSchema\mods)
                string palSchemaModsPath = Path.Combine(gamePath, @"Pal\Binaries\Win64\ue4ss\Mods\PalSchema\mods");
                if (Directory.Exists(palSchemaModsPath))
                {
                    var approvedPalSchema = new HashSet<string>(manifest.ApprovedPalSchemaMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                    // List all subdirectories under PalSchema\mods
                    var directories = Directory.GetDirectories(palSchemaModsPath);
                    foreach (var dir in directories)
                    {
                        string dirName = Path.GetFileName(dir);
                        if (!approvedPalSchema.Contains(dirName))
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                                System.Diagnostics.Debug.WriteLine($"Cleaned unapproved PalSchema Mod Folder: {dirName}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete PalSchema Mod Folder {dirName}: {ex.Message}");
                            }
                        }
                    }

                    // List all files directly under PalSchema\mods
                    var files = Directory.GetFiles(palSchemaModsPath);
                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
                            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!approvedPalSchema.Contains(fileName))
                        {
                            try
                            {
                                File.Delete(file);
                                System.Diagnostics.Debug.WriteLine($"Cleaned unapproved PalSchema Mod File directly in mods: {fileName}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete PalSchema Mod File {fileName}: {ex.Message}");
                            }
                        }
                    }
                }

                // 3. Clean Mods/ManagedMods folder
                var managedPaths = new[] { Path.Combine(gamePath, @"Mods\ManagedMods"), Path.Combine(gamePath, @"Pal\Binaries\Win64\Mods\ManagedMods") };
                foreach (var managedModsPath in managedPaths)
                {
                    if (Directory.Exists(managedModsPath))
                    {
                        var directories = Directory.GetDirectories(managedModsPath);
                        foreach (var dir in directories)
                        {
                            string dirName = Path.GetFileName(dir);
                            if (!approvedManaged.Contains(dirName))
                            {
                                try
                                {
                                    Directory.Delete(dir, true);
                                    System.Diagnostics.Debug.WriteLine($"Cleaned unapproved Managed Mod: {dirName}");
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to delete Managed Mod {dirName}: {ex.Message}");
                                }
                            }
                        }
                    }
                }

                // 4. Clean Mods/NativeMods folder
                var nativePaths = new[] { Path.Combine(gamePath, @"Mods\NativeMods"), Path.Combine(gamePath, @"Pal\Binaries\Win64\Mods\NativeMods") };
                foreach (var nativeModsPath in nativePaths)
                {
                    if (Directory.Exists(nativeModsPath))
                    {
                        var directories = Directory.GetDirectories(nativeModsPath);
                        foreach (var dir in directories)
                        {
                            string dirName = Path.GetFileName(dir);
                            if (!approvedNative.Contains(dirName))
                            {
                                try
                                {
                                    Directory.Delete(dir, true);
                                    System.Diagnostics.Debug.WriteLine($"Cleaned unapproved Native Mod: {dirName}");
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to delete Native Mod {dirName}: {ex.Message}");
                                }
                            }
                        }
                    }
                }

                // 5. Clean PalModSettings.ini ActiveModList
                var iniPaths = new[] { Path.Combine(gamePath, @"Mods\PalModSettings.ini"), Path.Combine(gamePath, @"Pal\Binaries\Win64\Mods\PalModSettings.ini") };
                foreach (var iniPath in iniPaths)
                {
                    if (File.Exists(iniPath))
                    {
                        var lines = File.ReadAllLines(iniPath);
                        var newLines = new List<string>();

                        foreach (var line in lines)
                        {
                            string trimmed = line.Trim();
                            if (trimmed.StartsWith("ActiveModList=", StringComparison.OrdinalIgnoreCase))
                            {
                                string modName = trimmed.Substring("ActiveModList=".Length).Trim();
                                if (approvedManaged.Contains(modName) || approvedNative.Contains(modName))
                                {
                                    newLines.Add(line);
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"Removed unapproved active mod from settings: {modName}");
                                }
                            }
                            else
                            {
                                newLines.Add(line);
                            }
                        }

                        File.WriteAllLines(iniPath, newLines);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during dynamic mod cleaning: {ex.Message}");
            }
        }

        private static void CleanMods(string gamePath, ModManifest manifest)
        {
            // Build a whitelist set of approved mod files for both ~mods and LogicMods
            var approvedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manifest.Mods != null)
            {
                foreach (var mod in manifest.Mods)
                {
                    approvedMods.Add(Path.Combine(mod.TargetDir.Replace('/', '\\'), mod.Name));
                }
            }

            if (manifest.ApprovedPakMods != null)
            {
                foreach (var name in manifest.ApprovedPakMods)
                {
                    approvedMods.Add(Path.Combine(@"Pal\Content\Paks\~mods", name));
                }
            }

            // 1. Clean Pal\Content\Paks\~mods
            string pakModsDir = Path.Combine(gamePath, @"Pal\Content\Paks\~mods");
            if (Directory.Exists(pakModsDir))
            {
                var files = Directory.GetFiles(pakModsDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    string relativeFile = Path.GetRelativePath(gamePath, file).Replace('/', '\\');
                    if (!approvedMods.Contains(relativeFile))
                    {
                        try
                        {
                            File.Delete(file);
                            System.Diagnostics.Debug.WriteLine($"Cleaned unapproved mod file in ~mods: {relativeFile}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to delete file {relativeFile}: {ex.Message}");
                        }
                    }
                }
            }

            // 2. Clean Pal\Content\Paks\LogicMods
            string logicModsDir = Path.Combine(gamePath, @"Pal\Content\Paks\LogicMods");
            if (Directory.Exists(logicModsDir))
            {
                var approvedLogic = new HashSet<string>(manifest.ApprovedLogicMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var files = Directory.GetFiles(logicModsDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);
                    if (!approvedLogic.Contains(fileName))
                    {
                        try
                        {
                            File.Delete(file);
                            System.Diagnostics.Debug.WriteLine($"Cleaned unapproved LogicMod file: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to delete LogicMod file {fileName}: {ex.Message}");
                        }
                    }
                }
            }
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, Action<double> progressCallback)
        {
            using (var response = await GetGoogleDriveResponseAsync(url))
            {
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue)
                        {
                            double progress = (double)totalRead / totalBytes.Value * 100.0;
                            progressCallback(progress);
                        }
                    }
                }
            }
        }

        public static string ComputeSHA256(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hashBytes = sha.ComputeHash(stream);
                var sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static async Task KnockServerApiAsync(string apiUrl, string secret, string steamId)
        {
            if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(secret))
                return;

            try
            {
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string message = $"{timestamp}:{steamId}";
                byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);

                string signature = "";
                using (var hmac = new HMACSHA256(keyBytes))
                {
                    byte[] hashBytes = hmac.ComputeHash(messageBytes);
                    var sb = new StringBuilder();
                    foreach (byte b in hashBytes)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    signature = sb.ToString();
                }

                using (var request = new HttpRequestMessage(HttpMethod.Post, apiUrl))
                {
                    request.Headers.Add("X-Timestamp", timestamp.ToString());
                    request.Headers.Add("X-Steam-ID", steamId);
                    request.Headers.Add("X-Signature", signature);
                    request.Content = new StringContent("", Encoding.UTF8, "application/json");

                    using (var response = await HttpClient.SendAsync(request))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string content = await response.Content.ReadAsStringAsync();
                            throw new Exception($"Server API rejected verification (Status {response.StatusCode}): {content}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to authenticate connection with server: {ex.Message}", ex);
            }
        }

        public static string LastViolationInfo { get; set; } = "";

        public static bool CheckProcessIntegrity(Process process, List<string>? unapprovedDlls = null)
        {
            try
            {
                if (process.HasExited)
                    return false;

                LastViolationInfo = "";

                // Mod/hijack DLLs to block ONLY if they are loaded from the game folder
                var hijackDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { 
                    "dwmapi.dll", "dxgi.dll", "UE4SS.dll", "version.dll", "winmm.dll" 
                };

                // Explicit cheat DLLs to block from any path
                var cheatDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "CheatEngine", "cheat", "hack", "injector", "trainer"
                };

                process.Refresh();
                foreach (ProcessModule module in process.Modules)
                {
                    string moduleName = module.ModuleName ?? "";
                    string modulePath = module.FileName ?? "";
                    string normalizedPath = modulePath.Replace('/', '\\');

                    // 1. Check for DLL hijacking (loaded from game folder instead of System32)
                    if (hijackDlls.Contains(moduleName))
                    {
                        if (normalizedPath.IndexOf(@"\Pal\Binaries\Win64\", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            LastViolationInfo = $"Hijacked DLL: {moduleName} loaded from game path ({modulePath})";
                            return false;
                        }
                    }

                    // 2. Check for explicit cheat/injection tool names in the module name
                    foreach (var cheat in cheatDlls)
                    {
                        if (moduleName.IndexOf(cheat, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            LastViolationInfo = $"Cheat Tool: {moduleName} ({modulePath})";
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Integrity check warning: {ex.Message}");
                return true; // Ignore transient access errors to prevent false-positive kills
            }
        }

        public static Process? LaunchPalworld(string gamePath, string serverIp, int serverPort, string password, bool autoConnect)
        {
            if (autoConnect && !string.IsNullOrEmpty(serverIp))
            {
                // Try to find SteamExe in the registry to launch it directly
                string? steamExe = null;
                try
                {
                    steamExe = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read SteamExe registry: {ex.Message}");
                }

                string connectArgs = $"+connect {serverIp}:{serverPort}";
                if (!string.IsNullOrEmpty(password))
                {
                    connectArgs += $" +password \"{password}\"";
                }

                if (!string.IsNullOrEmpty(steamExe) && File.Exists(steamExe))
                {
                    System.Diagnostics.Debug.WriteLine($"Launching via Steam.exe directly: {steamExe} -applaunch 1623730 {connectArgs}");
                    return Process.Start(new ProcessStartInfo(steamExe)
                    {
                        Arguments = $"-applaunch 1623730 {connectArgs}",
                        UseShellExecute = false
                    });
                }
                else
                {
                    // Fallback to steam:// protocol if registry check failed
                    string connectUri = $"steam://connect/{serverIp}:{serverPort}";
                    if (!string.IsNullOrEmpty(password))
                    {
                        connectUri += $"/{password}";
                    }

                    System.Diagnostics.Debug.WriteLine($"Fallback launching Steam connection URI: {connectUri}");
                    return Process.Start(new ProcessStartInfo(connectUri)
                    {
                        UseShellExecute = true
                    });
                }
            }
            else
            {
                // Fallback to standard Steam launch URI
                return Process.Start(new ProcessStartInfo("steam://run/1623730")
                {
                    UseShellExecute = true
                });
            }
        }

        public static string GetActiveSteamId64()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("ActiveUser");
                        if (val != null)
                        {
                            long accountId = Convert.ToInt64(val);
                            if (accountId > 0)
                            {
                                long steamId64 = 76561197960265728 + accountId;
                                return steamId64.ToString();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to get active Steam ID: {ex.Message}");
            }
            return "";
        }

        public static void ConfigureEngineIniForFastTravel(string? customEngineIniPath = null)
        {
            try
            {
                var pathsToUpdate = new List<string>();

                if (!string.IsNullOrEmpty(customEngineIniPath))
                {
                    pathsToUpdate.Add(customEngineIniPath);
                }
                else
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    
                    // Steam config path
                    string steamConfigDir = Path.Combine(localAppData, @"Pal\Saved\Config\Windows");
                    pathsToUpdate.Add(Path.Combine(steamConfigDir, "Engine.ini"));

                    // Gamepass config path
                    string gamepassConfigDir = Path.Combine(localAppData, @"Pal\Saved\Config\WinGDK");
                    pathsToUpdate.Add(Path.Combine(gamepassConfigDir, "Engine.ini"));
                }

                foreach (var engineIniPath in pathsToUpdate)
                {
                    string? dir = Path.GetDirectoryName(engineIniPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string targetSection = "[/game/mods/fasttravelfromanywhere/modactor.modactor_c]";
                    string targetKey = "FastTravelToNotDiscoveredPoints";
                    string targetValueLine = $"{targetKey}=false";

                    if (!File.Exists(engineIniPath))
                    {
                        File.WriteAllText(engineIniPath, $"{targetSection}\r\n{targetValueLine}\r\n", Encoding.UTF8);
                        continue;
                    }

                    var lines = new List<string>(File.ReadAllLines(engineIniPath));
                    int sectionIndex = -1;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].Trim().Equals(targetSection, StringComparison.OrdinalIgnoreCase))
                        {
                            sectionIndex = i;
                            break;
                        }
                    }

                    if (sectionIndex == -1)
                    {
                        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                        {
                            lines.Add("");
                        }
                        lines.Add(targetSection);
                        lines.Add(targetValueLine);
                    }
                    else
                    {
                        bool keyFound = false;
                        int insertIndex = sectionIndex + 1;

                        for (int i = sectionIndex + 1; i < lines.Count; i++)
                        {
                            string lineTrimmed = lines[i].Trim();

                            if (lineTrimmed.StartsWith("[") && lineTrimmed.EndsWith("]"))
                            {
                                insertIndex = i;
                                break;
                            }

                            if (lineTrimmed.StartsWith(targetKey, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!lineTrimmed.Equals(targetValueLine, StringComparison.OrdinalIgnoreCase))
                                {
                                    lines[i] = targetValueLine;
                                }
                                keyFound = true;
                                break;
                            }

                            insertIndex = i + 1;
                        }

                        if (!keyFound)
                        {
                            lines.Insert(insertIndex, targetValueLine);
                        }
                    }

                    File.WriteAllLines(engineIniPath, lines, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to update Engine.ini configuration: {ex.Message}", ex);
            }
        }

        public static void ConfigureTechnologyMod(string gamePath, string? customConfigPath = null)
        {
            try
            {
                var paths = new List<string>();
                if (!string.IsNullOrEmpty(customConfigPath))
                {
                    paths.Add(customConfigPath);
                }
                else
                {
                    paths.Add(Path.Combine(gamePath, @"Mods\NativeMods\UE4SS\Mods\Technology\Scripts\config.lua"));
                    paths.Add(Path.GetFullPath(Path.Combine(gamePath, @"..\..\workshop\content\1623730\3703535503\Scripts\config.lua")));
                }

                foreach (var path in paths)
                {
                    if (File.Exists(path))
                    {
                        var lines = File.ReadAllLines(path);
                        bool modified = false;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            string trimmed = lines[i].Trim();
                            if (trimmed.StartsWith("onlyUnlockUpToCurrentLevel", StringComparison.OrdinalIgnoreCase))
                            {
                                int eqIndex = lines[i].IndexOf('=');
                                if (eqIndex != -1)
                                {
                                    string prefix = lines[i].Substring(0, eqIndex + 1);
                                    string suffix = lines[i].Substring(eqIndex + 1);
                                    string comma = suffix.Trim().EndsWith(",") ? "," : "";
                                    lines[i] = $"{prefix} true{comma}";
                                    modified = true;
                                }
                            }
                        }

                        if (modified)
                        {
                            File.WriteAllLines(path, lines, Encoding.UTF8);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to configure technology mod: {ex.Message}", ex);
            }
        }
    }
}
