using Oxide.Core.Plugins;
using Oxide.Core;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using UnityEngine.Networking;
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using System.IO;
using Oxide.Core.Libraries.Covalence;
using System.Text.RegularExpressions;

namespace Oxide.Plugins
{
    [Info("Server Armour Updater", "Pho3niX90", "1.0.5")]
    [Description("Automatically updates plugins from serverarmour.com")]
    class ServerArmourUpdater : CovalencePlugin
    {
        Dictionary<string, byte[]> fileBackups = new Dictionary<string, byte[]>();
        List<string> ignoredPlugins = new List<string>();
        const bool debug = false;

        void OnServerInitialized(bool first)
        {
            timer.Once(10, CheckForUpdates);
        }

        private string manifest(string plugin, string author) => $"https://serverarmour.com/api/v1/marketplace/search?plugin={plugin}&author={author}&market=ServerArmour";

        [Command("sa.update_plugin")]
        void UpdatePlugin(IPlayer player, string command, string[] args)
        {
            Puts("Update requested");
            if (!player.IsServer || args.Length != 4)
            {
                return;
            }

            var pluginName = args[0].ToString();
            var downloadUrl = args[1].ToString();
            var vTemp = new Version();

            try
            {
                vTemp = new Version(args[2].ToString());
            }
            catch (Exception) { }

            var currentVersion = vTemp;
            var latestVersion = new Version(args[3].ToString());

            var plugin = new PluginInfo { Name = pluginName.Replace(".cs", ""), Filename = pluginName, Version = currentVersion };
            ServerMgr.Instance.StartCoroutine(StartDownload(plugin, downloadUrl, latestVersion, "uMod"));
        }

        private void CheckForUpdates()
        {
            ServerMgr.Instance.StartCoroutine(checkPlugins());
        }

        IEnumerator checkPlugins()
        {
            List<PluginInfo> plugins = GetAllPlugins();
            LogDebug("Starting update checking.");
            foreach (PluginInfo plugin in plugins)
            {
                LogDebug($"Progress: {plugins.IndexOf(plugin) + 1}/{plugins.Count}");
                webrequest.Enqueue(this.manifest(plugin.Name, plugin.Author), string.Empty, (code, data) => HandleUpdateRequest(plugin, code, data), this);
                yield return new WaitForSeconds(1);
            }
            LogDebug($"Progress: Done");
            timer.Once(15 * 60, CheckForUpdates);
        }

        private bool IsUpdateAvailable(PluginInfo plugin, Version latestVersion)
        {
            Version currentVersion = new Version(plugin.Version.ToString());

            int comparison = currentVersion.CompareTo(latestVersion);

            if (comparison < 0)
            {
                LogDebug($"An update is available for {plugin.Name}!");
                return true;
            }
            else
            {
                LogDebug($"{plugin.Name} already up to date: {plugin.Version}");
            }

            return false;
        }

        private void HandleUpdateRequest(PluginInfo plugin, int code, string data)
        {
            if (code != 200)
            {
                LogDebug($"Failed to retrieve update information for {plugin.Name}.");
                return;
            }

            try
            {
                var jArray = JArray.Parse(data);
                if (jArray.Count == 0)
                {
                    ignoredPlugins.Add(plugin.Name);
                    LogDebug($"{plugin.Name} not found on Server Armour");
                    return;
                }
                var jObject = (JObject)jArray[0];


                if (jObject == null)
                    return;

                var latestVersion = new Version(jObject.GetValue("latestVersion").ToString());
                var downloadUrl = jObject.GetValue("downloadUrl")?.ToString() ?? "";


                if (IsUpdateAvailable(plugin, latestVersion))
                {
                    ServerMgr.Instance.StartCoroutine(StartDownload(plugin, downloadUrl, latestVersion, "Server Armour Store"));
                }
            }
            catch (Exception ex)
            {
                LogDebug(data);
                LogDebug($"Failed to parse update information for  {plugin.Name}: {ex.Message}");
            }
        }

        private void QueueDownload(string filename, string downloadUrl, string downloadFrom)
        {
            Puts($"Download request received for {filename}");
            ServerMgr.Instance.StartCoroutine(StartDownload(new PluginInfo { Filename = filename, Name = filename, Version = new Version(0, 0) }, downloadUrl, new Version(1, 0), downloadFrom));
        }

        private IEnumerator StartDownload(PluginInfo plugin, string downloadUrl, Version newVersion, string downloadFrom)
        {
            Puts($"Updating {plugin.Name} from {downloadFrom} (version {plugin.Version} -> {newVersion})");

            var filename = plugin.Filename;

            if (!filename.EndsWith(".cs"))
                filename = filename + ".cs";
            if (!filename.StartsWith(Interface.Oxide.PluginDirectory))
                filename = Interface.Oxide.PluginDirectory + "/" + filename;

            // we cache the file contents, since unity downloads a file anyways if an error is observed.
            if (File.Exists(filename))
            {
                LogDebug($"Storing backup of {filename}");
                fileBackups[filename] = File.ReadAllBytes(filename);
                LogDebug($"{filename} size: {fileBackups[filename].Length}");
            }

            var www = new UnityWebRequest(downloadUrl, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerFile(filename)
            };

            yield return www.SendWebRequest();
            if (www.responseCode == 200)
            {
                if (fileBackups.ContainsKey(filename))
                {
                    LogDebug($"SUCCESS: Removing backup of {filename} size: {fileBackups[filename].Length}");
                    fileBackups.Remove(filename);
                }

                LogDebug($"\"{filename}\" update downloaded successfully!");
#if !CARBON
                timer.Once(1, () =>
                {
                    var pl = plugins.PluginManager.GetPlugin(plugin.Name);
                    if (pl == null || !pl.IsLoaded)
                        Interface.Oxide.LoadPlugin(plugin.Name);
                    else if (!pl.Version.Equals(new VersionNumber(newVersion.Major, newVersion.Minor, newVersion.Build)))
                        Interface.Oxide.ReloadPlugin(plugin.Name);

                });
#endif
            }
            else if (www.responseCode == 401)
            {
                LogDebug($"You don't own a license for \"{plugin.Name}\". Skipped");
                ignoredPlugins.Add(plugin.Name);
            }
            else
            {
                LogDebug($"Failed to download update: {www.error}");
            }
            if (fileBackups.ContainsKey(filename))
            {
                LogDebug($"FAILURE: Restoring backup of {filename} size: {fileBackups[filename].Length}");
                File.WriteAllBytes(filename, fileBackups[filename]);
                fileBackups.Remove(filename);
            }
        }

        private List<PluginInfo> GetAllPlugins()
        {
            List<PluginInfo> loadedPlugins = plugins.PluginManager.GetPlugins().Where(pl => !pl.IsCorePlugin)
                .Select(x => new PluginInfo { Name = x.Name, Author = x.Author, Filename = x.Filename, Version = new Version(x.Version.ToString()), IsLoaded = x.IsLoaded }).ToList();

            loadedPlugins.AddRange(ScanDirForUnloadedPlugins(loadedPlugins.Select(pl => pl.Name).ToList()));

            loadedPlugins.RemoveAll(x => ignoredPlugins.Contains(x.Name));
            /// we only want my plugins, others might not follow the semantic rules. 
            loadedPlugins.RemoveAll(x => x.Author != this.Author);
            return loadedPlugins;
        }

        private List<PluginInfo> ScanDirForUnloadedPlugins(List<string> ignore)
        {
            // add global ignore list
            ignore.AddRange(ignoredPlugins);

            var foundPlugins = new DirectoryInfo(Interface.Oxide.PluginDirectory)
                .GetFiles()
                .Where(x => x.Extension == ".cs")
                .Select(x => x.Name.Split(".")[0])
                .Except(ignore);
            List<PluginInfo> loadedPlugins = new List<PluginInfo>();

            LogDebug($"Found unloaded plugins = {foundPlugins.Count()}");

            foreach (string name in foundPlugins)
            {
                var path = $"{Interface.Oxide.PluginDirectory}/{name}.cs";
                loadedPlugins.Add(GetPluginInfoOfFile(name, path));
            }

            return loadedPlugins;
        }

        private PluginInfo GetPluginInfoOfFile(string name, string path)
        {
            try
            {
                string fileContents = File.ReadAllText(path);
                var matches = Regex.Matches(fileContents, "Info.*?\"(.*?)\".*?\"(.*?)\".*?\"(.*?)\".*?");
                foreach (Match match in matches)
                {
                    try
                    {
                        return new PluginInfo { Name = name, Filename = path, Author = match.Groups[2].Value, IsLoaded = false, Version = new Version(match.Groups[3].Value) };
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return new PluginInfo { Name = name, Filename = path, Version = new Version(0, 0, 0), IsLoaded = false };
        }

        private void LogDebug(string txt)
        {
            if (debug) Puts($"DEBUG: {txt}");
        }

        public class PluginInfo
        {
            public string Name;
            public string Filename;
            public string Author;
            public bool IsLoaded = true;
            public Version Version;
            public static PluginInfo from(Plugin plugin)
            {
                return new PluginInfo() { Name = plugin.Name, Filename = plugin.Filename, Version = new Version(plugin.Version.ToString()) };
            }
        }
    }
}
