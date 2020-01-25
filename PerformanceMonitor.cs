using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Performance Monitor", "Orange", "1.2.4")]
    [Description("Tool for collecting information about server performance")]
    public class PerformanceMonitor : RustPlugin
    {
        #region Vars

        private const string commandString = "monitor.createreport";
        private PerformanceDump currentReport;

        #endregion
        
        #region Oxide Hooks

        private void Init()
        {
           
            cmd.AddConsoleCommand(commandString, this, nameof(cmdCompleteNow));
        }

        private void OnServerInitialized()
        {
            if (config.checkTime > 0)
            {
                timer.Every(config.checkTime, CreateReport);
            }
        }

        #endregion

        #region Commands

        private void cmdCompleteNow(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin == false)
            {
                return;
            }

            CreateReport(); 
        }

        #endregion

        #region Core

        private void CreateReport()
        {
            ServerMgr.Instance.StartCoroutine(CreateActualReport());
        }

        private IEnumerator CreateActualReport()
        {
            if (currentReport != null)
            {
                yield break;
            }
            else
            {
                currentReport = new PerformanceDump();
            }
            
            var sw = new Stopwatch();
            sw.Start();

            CompletePluginsReport();
            ServerMgr.Instance.StartCoroutine(CompleteEntitiesReport());
            
            while (currentReport.entities.completed == false && config.runEntitiesReport == true)
            {
                Puts($"Report status: {currentReport.statusBar}% [{currentReport.entitiesChecked}/{currentReport.entitiesTotal}]");
                yield return new WaitForSecondsRealtime(3f);
            }
            
            SaveReport(currentReport);
            currentReport = null;
            sw.Stop();
            Puts($"Performance report was completed in {Time()}");
            LogToFile("events", $"Performance report was completed in {Time()}, it taken {sw.ElapsedMilliseconds}ms", this);
            
        }
        
        private void CompletePluginsReport()
        {
            var list = new List<string>();
            
            if (config.runPluginsReport == false)
            {
                return;
            }
            
            foreach (var plugin in plugins.GetAll().OrderByDescending(x => x.TotalHookTime))
            {
                var name = plugin.Name;
                if (name == Name || plugin.IsCorePlugin || config.excludedPlugins.Contains(name))
                {
                    continue;
                }
                
                var version = plugin.Version;
                var time = Convert.ToInt32(plugin.TotalHookTime);
                var info = $"{name} ({version}), Total Hook Time = {time}";
                list.Add(info);
            }

            currentReport.plugins = list.ToArray();
        }

        private IEnumerator CompleteEntitiesReport()
        {
            if (config.runEntitiesReport == false)
            {
                yield break;
            }
            
            yield return new WaitForEndOfFrame();
            var entities = UnityEngine.Object.FindObjectsOfType<BaseEntity>();
            var entitiesByShortname = currentReport.entities.list;
            currentReport.entitiesTotal = entities.Length;
            yield return new WaitForEndOfFrame();
            
            yield return new WaitForEndOfFrame();
            
            for (var i = 0; i < entities.Length; i++)
            {
                currentReport.entitiesChecked++;
                currentReport.statusBar = Convert.ToInt32(i * 100 / entities.Length);

                var entity = entities[i];
                if (entity.IsValid() == false)
                {
                    continue;
                }
                
                var shortname = entity.ShortPrefabName;
                if (config.excludedEntities.Contains(shortname) == true)
                {
                    continue;
                }
                
                yield return new WaitForEndOfFrame();
                var info = (EntityInfo) null;
                if (entitiesByShortname.TryGetValue(shortname, out info) == false)
                {
                    info = new EntityInfo();
                    entitiesByShortname.Add(shortname, info);
                }
                
                yield return new WaitForEndOfFrame();
                
                if (entity.OwnerID == 0)
                {
                    info.countUnowned++;
                    currentReport.entities.countUnowned++;
                }
                else
                {
                    info.countOwned++;
                    currentReport.entities.countOwned++;
                }

                info.countGlobal++;
                currentReport.entities.countGlobal++;
            }

            currentReport.entities.list = currentReport.entities.list.OrderByDescending(x => x.Value.countGlobal).ToDictionary(x => x.Key, y => y.Value);
            yield return new WaitForEndOfFrame();
            currentReport.entities.completed = true;
        }

        #endregion

        #region Utils
        
        private void SaveReport(PerformanceDump dump)
        {
            var name1 = DateTime.Now.ToString("dd/MM/yyyy").Replace("/", "-");
            var name2 = DateTime.Now.ToString(Time()).Replace(':', '-');
            var filename = $"PerformanceMonitor/Reports/{name1}/{name2}";
            Interface.Oxide.DataFileSystem.WriteObject(filename, dump);
        }

        private string Time()
        {
            return DateTime.Now.ToString("HH:mm:ss");
        }

        #endregion
        
        #region Configuration | 2.0.0

        private static ConfigData config = new ConfigData();

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Create reports every (seconds)")]
            public int checkTime = 0;

            [JsonProperty(PropertyName = "Create plugins report")]
            public bool runPluginsReport = true;

            [JsonProperty(PropertyName = "Create entities report")]
            public bool runEntitiesReport = true;

            [JsonProperty(PropertyName = "Excluded entities")]
            public string[] excludedEntities =
            {
                "shortname here",
                "another here"
            };

            [JsonProperty(PropertyName = "Excluded plugins")]
            public string[] excludedPlugins =
            {
                "name here",
                "another name"
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                config = Config.ReadObject<ConfigData>();

                if (config == null)
                {
                    LoadDefaultConfig();
                }
            }
            catch
            {
                PrintError("Configuration file is corrupt! Check your config file at https://jsonlint.com/");

                timer.Every(10f,
                    () =>
                    {
                        PrintError("Configuration file is corrupt! Check your config file at https://jsonlint.com/");
                    });
                LoadDefaultConfig();
                return;
            }

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }

        #endregion

        #region Classes

        private class PerformanceDump
        {
            [JsonProperty(PropertyName = "Online players")]
            public int onlinePlayers = BasePlayer.activePlayerList.Count;

            [JsonProperty(PropertyName = "Offline players")]
            public int offlinePlayers = BasePlayer.sleepingPlayerList.Count;

            [JsonProperty(PropertyName = "Entities report")]
            public EntitiesReport entities = new EntitiesReport();
            
            [JsonProperty(PropertyName = "Plugins report")]
            public string[] plugins;

            [JsonProperty(PropertyName = "Performance report")]
            public Performance.Tick performance = Performance.current;

            [JsonIgnore] 
            public int statusBar;

            [JsonIgnore] 
            public int entitiesChecked;

            [JsonIgnore]
            public int entitiesTotal;
        }

        private class EntitiesReport
        {
            [JsonProperty(PropertyName = "Total")]
            public int countGlobal;
            
            [JsonProperty(PropertyName = "Owned")]
            public int countOwned;
            
            [JsonProperty(PropertyName = "Unowned")]
            public int countUnowned;
            
            [JsonProperty(PropertyName = "List")]
            public Dictionary<string, EntityInfo> list = new Dictionary<string, EntityInfo>();

            [JsonIgnore] 
            public bool completed;
        }

        private class EntityInfo
        {
            [JsonProperty(PropertyName = "Total")]
            public int countGlobal;
            
            [JsonProperty(PropertyName = "Owned")]
            public int countOwned;
            
            [JsonProperty(PropertyName = "Unowned")]
            public int countUnowned;
        }

        #endregion
    }
}