using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.IO.Compression;
using Xunit;
using PalworldLauncher;

namespace PalworldLauncher.Tests
{
    public class LauncherTests
    {
        private string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "PalworldMock_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(path);
            return path;
        }

        private string CreateTempGameStructure(
            out string workshopPath, 
            out string managedModsPath, 
            out string nativeModsPath,
            out string iniPath)
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "PalworldTest_" + Guid.NewGuid().ToString());
            string gamePath = Path.Combine(baseDir, @"steamapps\common\Palworld");
            workshopPath = Path.Combine(baseDir, @"steamapps\workshop\content\1623730");
            
            string modsDir = Path.Combine(gamePath, "Mods");
            managedModsPath = Path.Combine(modsDir, "ManagedMods");
            nativeModsPath = Path.Combine(modsDir, "NativeMods");
            iniPath = Path.Combine(modsDir, "PalModSettings.ini");

            Directory.CreateDirectory(gamePath);
            Directory.CreateDirectory(workshopPath);
            Directory.CreateDirectory(managedModsPath);
            Directory.CreateDirectory(nativeModsPath);

            return gamePath;
        }

        private string ComputeHash(string content)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                var sb = new StringBuilder();
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        [Fact]
        public async Task Test_SyncAndClean_DeletesUnapprovedModsAndDLLs()
        {
            // Arrange
            string mockGamePath = CreateTempDirectory();
            
            // Create game directory structure
            string modsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\~mods");
            string win64Dir = Path.Combine(mockGamePath, @"Pal\Binaries\Win64");
            string ue4ssModsDir = Path.Combine(win64Dir, "Mods");

            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(win64Dir);
            Directory.CreateDirectory(ue4ssModsDir);

            // Create some files
            string approvedModName = "approved_mod.pak";
            string approvedModContent = "approved mod content";
            string approvedModHash = ComputeHash(approvedModContent);
            string approvedModPath = Path.Combine(modsDir, approvedModName);
            File.WriteAllText(approvedModPath, approvedModContent);

            string unapprovedModPath = Path.Combine(modsDir, "cheat_mod.pak");
            File.WriteAllText(unapprovedModPath, "cheat mod content");

            string dxgiDllPath = Path.Combine(win64Dir, "dxgi.dll");
            File.WriteAllText(dxgiDllPath, "fake injector");

            string someScriptPath = Path.Combine(ue4ssModsDir, "cheat_script.lua");
            File.WriteAllText(someScriptPath, "cheat lua code");

            // Setup manifest
            var manifest = new ModManifest
            {
                ServerIp = "127.0.0.1",
                ServerPort = 8211,
                Mods = new System.Collections.Generic.List<ModInfo>
                {
                    new ModInfo
                    {
                        Name = approvedModName,
                        Url = "http://localhost/dummy", // won't be downloaded because hash matches
                        Hash = approvedModHash,
                        TargetDir = @"Pal\Content\Paks\~mods"
                    }
                }
            };

            // Act
            await LauncherLogic.SyncAndCleanAsync(mockGamePath, manifest, (status, pct) => {
                System.Diagnostics.Debug.WriteLine($"{status} - {pct}%");
            });

            // Assert
            // 1. Approved mod should still exist
            Assert.True(File.Exists(approvedModPath));

            // 2. Unapproved mod should be deleted
            Assert.False(File.Exists(unapprovedModPath));

            // 3. dxgi.dll should be deleted
            Assert.False(File.Exists(dxgiDllPath));

            // 4. UE4SS Mods folder should be deleted
            Assert.False(Directory.Exists(ue4ssModsDir));

            // Clean up
            try
            {
                Directory.Delete(mockGamePath, true);
            }
            catch { }
        }

        [Fact]
        public async Task Test_SyncAndClean_HandlesSizeTimestampHash()
        {
            // Arrange
            string mockGamePath = CreateTempDirectory();
            string modsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\~mods");
            Directory.CreateDirectory(modsDir);

            // Create local mod file
            string modName = "gdrive_mod.pak";
            string modPath = Path.Combine(modsDir, modName);
            string modContent = "mock google drive mod content";
            File.WriteAllText(modPath, modContent);

            // Fetch actual file properties to generate matching size_timestamp hash
            var fileInfo = new FileInfo(modPath);
            long expectedSize = fileInfo.Length;
            long expectedTimestamp = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            string matchingHash = $"{expectedSize}_{expectedTimestamp}";

            var manifest = new ModManifest
            {
                ServerIp = "127.0.0.1",
                ServerPort = 8211,
                Mods = new System.Collections.Generic.List<ModInfo>
                {
                    new ModInfo
                    {
                        Name = modName,
                        Url = "http://localhost/dummy", // won't download if matches
                        Hash = matchingHash,
                        TargetDir = @"Pal\Content\Paks\~mods"
                    }
                }
            };

            // Act
            // Should complete successfully without throwing download error because size and timestamp match
            await LauncherLogic.SyncAndCleanAsync(mockGamePath, manifest, (status, pct) => {});

            // Assert
            Assert.True(File.Exists(modPath));

            // Clean up
            try
            {
                Directory.Delete(mockGamePath, true);
            }
            catch { }
        }

        [Fact]
        public async Task Test_SyncAndClean_FullModWhitelistingAndSettingsCleanup()
        {
            // Arrange
            string gamePath = CreateTempGameStructure(
                out string workshopPath, 
                out string managedModsPath,
                out string nativeModsPath,
                out string iniPath);

            // 1. Steam Workshop Setup
            string approvedWorkshopId = "3625223587";
            string unapprovedWorkshopId = "9999999999";
            Directory.CreateDirectory(Path.Combine(workshopPath, approvedWorkshopId));
            Directory.CreateDirectory(Path.Combine(workshopPath, unapprovedWorkshopId));

            // 2. ManagedMods Setup
            string approvedManagedMod = "ExtendedBaseRange";
            string unapprovedManagedMod = "CheatMenu";
            Directory.CreateDirectory(Path.Combine(managedModsPath, approvedManagedMod));
            Directory.CreateDirectory(Path.Combine(managedModsPath, unapprovedManagedMod));

            // 3. NativeMods Setup
            string approvedNativeMod = "UE4SS";
            string unapprovedNativeMod = "CheatNativeMod";
            Directory.CreateDirectory(Path.Combine(nativeModsPath, approvedNativeMod));
            Directory.CreateDirectory(Path.Combine(nativeModsPath, unapprovedNativeMod));

            // 4. PalModSettings.ini Setup
            string iniContent = 
                "[PalModSettings]\r\n" +
                "bGlobalEnableMod=True\r\n" +
                "WorkshopRootDir=E:\\SteamLibrary\\steamapps\\workshop\\content\\1623730\r\n" +
                $"ActiveModList={approvedNativeMod}\r\n" +
                $"ActiveModList={approvedManagedMod}\r\n" +
                $"ActiveModList={unapprovedManagedMod}\r\n";
            File.WriteAllText(iniPath, iniContent);

            // Setup manifest
            var manifest = new ModManifest
            {
                ServerIp = "127.0.0.1",
                ServerPort = 8211,
                ApprovedWorkshopIds = new System.Collections.Generic.List<string> { approvedWorkshopId },
                ApprovedManagedMods = new System.Collections.Generic.List<string> { approvedManagedMod },
                ApprovedNativeMods = new System.Collections.Generic.List<string> { approvedNativeMod }
            };

            // Act
            await LauncherLogic.SyncAndCleanAsync(gamePath, manifest, (status, pct) => {});

            // Assert
            // Workshop: Approved ID remains, unapproved deleted
            Assert.True(Directory.Exists(Path.Combine(workshopPath, approvedWorkshopId)));
            Assert.False(Directory.Exists(Path.Combine(workshopPath, unapprovedWorkshopId)));

            // ManagedMods: Approved mod remains, unapproved deleted
            Assert.True(Directory.Exists(Path.Combine(managedModsPath, approvedManagedMod)));
            Assert.False(Directory.Exists(Path.Combine(managedModsPath, unapprovedManagedMod)));

            // NativeMods: Approved mod remains, unapproved deleted
            Assert.True(Directory.Exists(Path.Combine(nativeModsPath, approvedNativeMod)));
            Assert.False(Directory.Exists(Path.Combine(nativeModsPath, unapprovedNativeMod)));

            // PalModSettings.ini: Approved lists are kept, unapproved is stripped
            string updatedIni = File.ReadAllText(iniPath);
            Assert.Contains($"ActiveModList={approvedNativeMod}", updatedIni);
            Assert.Contains($"ActiveModList={approvedManagedMod}", updatedIni);
            Assert.DoesNotContain($"ActiveModList={unapprovedManagedMod}", updatedIni);

            // Clean up base folder
            try
            {
                string baseDir = Path.GetFullPath(Path.Combine(gamePath, @"..\..\.."));
                Directory.Delete(baseDir, true);
            }
            catch { }
        }

        [Fact]
        public void Test_ConfigureEngineIniForFastTravel_UpdatesEngineIni()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "PalworldIniTest_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            string testIniPath = Path.Combine(tempDir, "Engine.ini");

            try
            {
                // Test case 1: File does not exist
                LauncherLogic.ConfigureEngineIniForFastTravel(testIniPath);
                Assert.True(File.Exists(testIniPath));
                string content1 = File.ReadAllText(testIniPath);
                Assert.Contains("[/game/mods/fasttravelfromanywhere/modactor.modactor_c]", content1);
                Assert.Contains("FastTravelToNotDiscoveredPoints=false", content1);

                // Test case 2: File exists but section missing, has other settings
                string otherSetting = "[SystemSettings]\r\nr.VSync=1";
                File.WriteAllText(testIniPath, otherSetting);
                LauncherLogic.ConfigureEngineIniForFastTravel(testIniPath);
                string content2 = File.ReadAllText(testIniPath);
                Assert.Contains("[SystemSettings]", content2);
                Assert.Contains("r.VSync=1", content2);
                Assert.Contains("[/game/mods/fasttravelfromanywhere/modactor.modactor_c]", content2);
                Assert.Contains("FastTravelToNotDiscoveredPoints=false", content2);

                // Test case 3: Section exists but key set to true
                string sectionWithTrue = "[/game/mods/fasttravelfromanywhere/modactor.modactor_c]\r\nFastTravelToNotDiscoveredPoints=true\r\n[OtherSection]\r\nfoo=bar";
                File.WriteAllText(testIniPath, sectionWithTrue);
                LauncherLogic.ConfigureEngineIniForFastTravel(testIniPath);
                string content3 = File.ReadAllText(testIniPath);
                Assert.Contains("FastTravelToNotDiscoveredPoints=false", content3);
                Assert.DoesNotContain("FastTravelToNotDiscoveredPoints=true", content3);
                Assert.Contains("[OtherSection]", content3);
                Assert.Contains("foo=bar", content3);

                // Test case 4: Section exists and key is false (no duplication)
                LauncherLogic.ConfigureEngineIniForFastTravel(testIniPath);
                string content4 = File.ReadAllText(testIniPath);
                int count = 0;
                int pos = 0;
                while ((pos = content4.IndexOf("FastTravelToNotDiscoveredPoints=false", pos)) != -1)
                {
                    count++;
                    pos += "FastTravelToNotDiscoveredPoints=false".Length;
                }
                Assert.Equal(1, count);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        [Fact]
        public void Test_ConfigureTechnologyMod_UpdatesConfigLua()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "PalworldLuaTest_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            string testLuaPath = Path.Combine(tempDir, "config.lua");

            try
            {
                string originalContent = 
                    "local ATM_config = {\r\n" +
                    "    onlyUnlockUpToCurrentLevel = false,\r\n" +
                    "    delayToAttemptToLoadTechnologies = 10000,\r\n" +
                    "}\r\n" +
                    "return ATM_config";
                File.WriteAllText(testLuaPath, originalContent);

                // Act
                LauncherLogic.ConfigureTechnologyMod(tempDir, testLuaPath);

                // Assert
                string content = File.ReadAllText(testLuaPath);
                Assert.Contains("onlyUnlockUpToCurrentLevel = true,", content);
                Assert.DoesNotContain("onlyUnlockUpToCurrentLevel = false,", content);
                Assert.Contains("delayToAttemptToLoadTechnologies = 10000,", content);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        [Fact]
        public async Task Test_SyncAndClean_DownloadsAndExtractsZipModPack()
        {
            // Arrange
            string mockGamePath = CreateTempDirectory();
            string modsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\~mods");
            string win64Dir = Path.Combine(mockGamePath, @"Pal\Binaries\Win64");
            string managedModsDir = Path.Combine(win64Dir, @"Mods\ManagedMods");
            string nativeModsDir = Path.Combine(win64Dir, @"Mods\NativeMods");

            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(win64Dir);

            // Create a mock zip file in a local temp cache directory to simulate download
            string cacheDir = Path.Combine(mockGamePath, "LauncherCache");
            Directory.CreateDirectory(cacheDir);

            string zipPath = Path.Combine(cacheDir, "mods.zip");

            // We will pack:
            // 1. Pal/Content/Paks/~mods/new_approved.pak
            // 2. Pal/Binaries/Win64/dwmapi.dll
            // 3. Pal/Binaries/Win64/Mods/ManagedMods/TestManagedMod/config.lua
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // Create pak file
                var entryPak = zip.CreateEntry(@"Pal/Content/Paks/~mods/new_approved.pak");
                using (var writer = new StreamWriter(entryPak.Open()))
                {
                    writer.Write("approved pak content");
                }

                // Create dwmapi.dll
                var entryDll = zip.CreateEntry(@"Pal/Binaries/Win64/dwmapi.dll");
                using (var writer = new StreamWriter(entryDll.Open()))
                {
                    writer.Write("approved dll content");
                }

                // Create a managed mod config file
                var entryLua = zip.CreateEntry(@"Pal/Binaries/Win64/Mods/ManagedMods/TestManagedMod/config.lua");
                using (var writer = new StreamWriter(entryLua.Open()))
                {
                    writer.Write("print('managed mod config')");
                }
            }

            // Create some unapproved files that should be cleaned up
            string unapprovedPak = Path.Combine(modsDir, "cheat_pak.pak");
            File.WriteAllText(unapprovedPak, "cheat content");

            string unapprovedDll = Path.Combine(win64Dir, "dxgi.dll");
            File.WriteAllText(unapprovedDll, "cheat dll");

            string unapprovedManagedModDir = Path.Combine(managedModsDir, "CheatManaged");
            Directory.CreateDirectory(unapprovedManagedModDir);
            File.WriteAllText(Path.Combine(unapprovedManagedModDir, "config.lua"), "cheat managed");

            // Setup manifest with the zip mod
            var manifest = new ModManifest
            {
                ServerIp = "127.0.0.1",
                ServerPort = 8211,
                DllCleanupList = new System.Collections.Generic.List<string> { "dwmapi.dll", "dxgi.dll" },
                Mods = new System.Collections.Generic.List<ModInfo>
                {
                    new ModInfo
                    {
                        Name = "mods.zip",
                        Url = "http://localhost/dummy",
                        Hash = "",
                        TargetDir = "LauncherCache"
                    }
                }
            };

            // Set the local zip file timestamp/hash so it doesn't try to download from URL
            var fileInfo = new FileInfo(zipPath);
            manifest.Mods[0].Hash = $"{fileInfo.Length}_{new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds()}";

            // Act
            await LauncherLogic.SyncAndCleanAsync(mockGamePath, manifest, (status, pct) => {});

            // Assert
            // 1. Check extracted approved pak exists
            string extractedPak = Path.Combine(modsDir, "new_approved.pak");
            Assert.True(File.Exists(extractedPak));

            // 2. Check extracted approved dll exists and was not deleted by CleanInjections
            string extractedDll = Path.Combine(win64Dir, "dwmapi.dll");
            Assert.True(File.Exists(extractedDll));

            // 3. Check extracted managed mod exists
            string extractedLua = Path.Combine(managedModsDir, @"TestManagedMod\config.lua");
            Assert.True(File.Exists(extractedLua));

            // 4. Check unapproved pak was deleted
            Assert.False(File.Exists(unapprovedPak));

            // 5. Check unapproved dll was deleted
            Assert.False(File.Exists(unapprovedDll));

            // 6. Check unapproved managed mod was deleted
            Assert.False(Directory.Exists(unapprovedManagedModDir));

            // Clean up
            try
            {
                Directory.Delete(mockGamePath, true);
            }
            catch { }
        }

        [Fact]
        public async Task Test_SyncAndClean_CleansNewUe4ssStructure()
        {
            // Arrange
            string mockGamePath = CreateTempDirectory();
            string modsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\~mods");
            string logicModsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\LogicMods");
            string win64Dir = Path.Combine(mockGamePath, @"Pal\Binaries\Win64");
            string ue4ssDir = Path.Combine(win64Dir, "ue4ss");
            string ue4ssModsDir = Path.Combine(ue4ssDir, "Mods");
            string palSchemaModsDir = Path.Combine(ue4ssModsDir, @"PalSchema\mods");

            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(logicModsDir);
            Directory.CreateDirectory(win64Dir);
            Directory.CreateDirectory(ue4ssDir);
            Directory.CreateDirectory(ue4ssModsDir);
            Directory.CreateDirectory(palSchemaModsDir);

            // Create a mock zip file in a local temp cache directory to simulate download
            string cacheDir = Path.Combine(mockGamePath, "LauncherCache");
            Directory.CreateDirectory(cacheDir);

            string zipPath = Path.Combine(cacheDir, "nexus_mods.zip");

            // We will pack:
            // 1. Pal/Content/Paks/LogicMods/PalAnalyzer.pak
            // 2. Pal/Binaries/Win64/ue4ss/UE4SS.dll
            // 3. Pal/Binaries/Win64/ue4ss/Mods/BPModLoaderMod/main.lua
            // 4. Pal/Binaries/Win64/ue4ss/Mods/PalSchema/mods/William_MoreEyes_P/config.json
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entryLogic = zip.CreateEntry(@"Pal/Content/Paks/LogicMods/PalAnalyzer.pak");
                using (var writer = new StreamWriter(entryLogic.Open()))
                {
                    writer.Write("approved logic pak content");
                }

                var entryDll = zip.CreateEntry(@"Pal/Binaries/Win64/ue4ss/UE4SS.dll");
                using (var writer = new StreamWriter(entryDll.Open()))
                {
                    writer.Write("approved ue4ss dll content");
                }

                var entryLua = zip.CreateEntry(@"Pal/Binaries/Win64/ue4ss/Mods/BPModLoaderMod/main.lua");
                using (var writer = new StreamWriter(entryLua.Open()))
                {
                    writer.Write("print('bp mod loader')");
                }

                var entrySchema = zip.CreateEntry(@"Pal/Binaries/Win64/ue4ss/Mods/PalSchema/mods/William_MoreEyes_P/config.json");
                using (var writer = new StreamWriter(entrySchema.Open()))
                {
                    writer.Write("{}");
                }
            }

            // Create some unapproved files that should be cleaned up
            string unapprovedLogicPak = Path.Combine(logicModsDir, "cheat_logic.pak");
            File.WriteAllText(unapprovedLogicPak, "cheat logic content");

            string unapprovedUe4ssModDir = Path.Combine(ue4ssModsDir, "NoMoreHoldButtonJustClick");
            Directory.CreateDirectory(unapprovedUe4ssModDir);
            File.WriteAllText(Path.Combine(unapprovedUe4ssModDir, "main.lua"), "cheat logic config");

            string unapprovedUe4ssRootFile = Path.Combine(ue4ssDir, "unapproved_root.dll");
            File.WriteAllText(unapprovedUe4ssRootFile, "cheat root dll");

            string unapprovedPalSchemaModDir = Path.Combine(palSchemaModsDir, "CheatSchemaMod");
            Directory.CreateDirectory(unapprovedPalSchemaModDir);
            File.WriteAllText(Path.Combine(unapprovedPalSchemaModDir, "config.json"), "cheat schema mod");

            // Setup manifest with the zip mod
            var manifest = new ModManifest
            {
                ServerIp = "127.0.0.1",
                ServerPort = 8211,
                DllCleanupList = new System.Collections.Generic.List<string> { "dwmapi.dll", "dxgi.dll" },
                Mods = new System.Collections.Generic.List<ModInfo>
                {
                    new ModInfo
                    {
                        Name = "nexus_mods.zip",
                        Url = "http://localhost/dummy",
                        Hash = "",
                        TargetDir = "LauncherCache"
                    }
                }
            };

            // Set the local zip file timestamp/hash so it doesn't try to download from URL
            var fileInfo = new FileInfo(zipPath);
            manifest.Mods[0].Hash = $"{fileInfo.Length}_{new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds()}";

            // Act
            await LauncherLogic.SyncAndCleanAsync(mockGamePath, manifest, (status, pct) => {});

            // Assert
            // 1. Check extracted logic mods exist
            string extractedLogicPak = Path.Combine(logicModsDir, "PalAnalyzer.pak");
            Assert.True(File.Exists(extractedLogicPak));

            // 2. Check extracted approved ue4ss root file exists
            string extractedUe4ssDll = Path.Combine(ue4ssDir, "UE4SS.dll");
            Assert.True(File.Exists(extractedUe4ssDll));

            // 3. Check extracted approved ue4ss mod exists
            string extractedLua = Path.Combine(ue4ssModsDir, @"BPModLoaderMod\main.lua");
            Assert.True(File.Exists(extractedLua));

            // 3.5 Check extracted approved PalSchema mod exists
            string extractedSchemaJson = Path.Combine(palSchemaModsDir, @"William_MoreEyes_P\config.json");
            Assert.True(File.Exists(extractedSchemaJson));

            // 4. Check unapproved logic pak was deleted
            Assert.False(File.Exists(unapprovedLogicPak));

            // 5. Check unapproved ue4ss mod directory was deleted
            Assert.False(Directory.Exists(unapprovedUe4ssModDir));

            // 6. Check unapproved ue4ss root file was deleted
            Assert.False(File.Exists(unapprovedUe4ssRootFile));

            // 7. Check unapproved PalSchema mod directory was deleted
            Assert.False(Directory.Exists(unapprovedPalSchemaModDir));

            // Clean up
            try
            {
                Directory.Delete(mockGamePath, true);
            }
            catch { }
        }

        [Fact]
        public async Task Test_IsSyncRequired_DetectsChanges()
        {
            // Arrange
            string mockGamePath = CreateTempDirectory();
            string modsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\~mods");
            Directory.CreateDirectory(modsDir);

            string modName = "test_sync.pak";
            string modPath = Path.Combine(modsDir, modName);
            string modContent = "mod content";
            File.WriteAllText(modPath, modContent);

            var fileInfo = new FileInfo(modPath);
            long expectedSize = fileInfo.Length;
            long expectedTimestamp = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();

            var manifest = new ModManifest
            {
                Mods = new System.Collections.Generic.List<ModInfo>
                {
                    new ModInfo
                    {
                        Name = modName,
                        Url = "http://localhost/dummy",
                        Hash = $"{expectedSize}_{expectedTimestamp}",
                        TargetDir = @"Pal\Content\Paks\~mods"
                    }
                }
            };

            // Act & Assert 1: Files match, no sync required
            Assert.False(await LauncherLogic.IsSyncRequiredAsync(mockGamePath, manifest));

            // Act & Assert 2: File missing -> sync required
            File.Delete(modPath);
            Assert.True(await LauncherLogic.IsSyncRequiredAsync(mockGamePath, manifest));

            // Act & Assert 3: Size mismatch -> sync required
            File.WriteAllText(modPath, "different size content");
            Assert.True(await LauncherLogic.IsSyncRequiredAsync(mockGamePath, manifest));

            // Clean up
            try
            {
                Directory.Delete(mockGamePath, true);
            }
            catch { }
        }

        [Fact]
        public void Test_ClearAllModFolders_CleansDirectories()
        {
            // Arrange
            string mockGamePath = CreateTempDirectory();
            string logicModsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\LogicMods");
            string modsDir = Path.Combine(mockGamePath, @"Pal\Content\Paks\~mods");
            string ue4ssModsDir = Path.Combine(mockGamePath, @"Pal\Binaries\Win64\ue4ss\Mods");

            Directory.CreateDirectory(logicModsDir);
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(ue4ssModsDir);

            File.WriteAllText(Path.Combine(logicModsDir, "file.pak"), "content");
            File.WriteAllText(Path.Combine(modsDir, "file.pak"), "content");
            File.WriteAllText(Path.Combine(ue4ssModsDir, "file.lua"), "content");

            // Act
            LauncherLogic.ClearAllModFolders(mockGamePath);

            // Assert
            Assert.False(Directory.Exists(logicModsDir));
            Assert.False(Directory.Exists(modsDir));
            Assert.False(Directory.Exists(ue4ssModsDir));

            // Clean up
            try
            {
                Directory.Delete(mockGamePath, true);
            }
            catch { }
        }
    }
}