using Newtonsoft.Json;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("CombatLog Info", "Pho3niX90", "1.0.12")]
    [Description("Collects combat log entries, and submits them to SA for analysis.")]
    class CombatLogInfo : RustPlugin
    {
        const bool debug = false;
        [PluginReference] Plugin ServerArmour;

        List<int> usedHashes = new List<int>();

        // Simple stats
        DateTime logsSince = DateTime.Now;
        int totalLogs = 0;
        int totalLogsUploaded = 0;
        int pendingGeneration = 0;
        int failedUploads = 0;
        //

        void OnServerInitialized(bool first)
        {
            Server.Command("server.combatlogsize 45");
        }

        void OnServerSave()
        {
            this.cleanupHashes();
        }

        #region log generation

        private void GenCombatLog(BasePlayer forPlayer)
        {
            if (forPlayer.IsNpc || !forPlayer.userID.IsSteamId())
                return;

            var pInfo = new PInfo { Name = forPlayer.displayName, SteamId = forPlayer.UserIDString };
            var cLog = CombatLog.Get(forPlayer.userID);

            pendingGeneration++;

            timer.Once(ConVar.Server.combatlogdelay + 1, () =>
            {
                AddEntries(forPlayer, cLog);
            });
        }
        #endregion

        #region commands
        [ConsoleCommand("clog.stats")]
        void printStats(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith($"Stats since {logsSince}\n\nTotal Logs: {totalLogs}\nTotal Logs Uploaded: {totalLogsUploaded}\nFailed Logs Upload: {failedUploads}\nPending Log Generation: {pendingGeneration}");
        }

        [ConsoleCommand("combatlog")]
        void printCombatLog(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!player)
                return;

            TextTable textTable = new TextTable();

            textTable.AddColumns("time","attacker", "id", "target", "id", "weapon", "ammo", "area", "distance", "old_hp", "new_hp", "info", "desync");

            foreach (CombatLog.Event evt in CombatLog.Get(player.userID))
            {
                var entry = CLogEntry.from(player, evt);
                if (entry != null)
                {
                    textTable.AddRow((Time.realtimeSinceStartup -  entry.EventTime).ToString("0.0s"),
                        GetUsername(player, entry.AttackerSteamId) , entry.AttackerSteamId,
                        GetUsername(player, entry.TargetSteamId) , entry.TargetSteamId,
                        entry.Weapon, entry.Ammo, 
                        entry.Area, entry.Distance.ToString("0.0m"), 
                        entry.HealthOld.ToString("0.0"), entry.HealthNew.ToString("0.0"), entry.EventInfo, entry.Desync.ToString());
                }
            }
            player.ConsoleMessage("---------- COMBATLOG  ----------\n");
            player.ConsoleMessage(textTable.ToString());
        }

        string GetUsername(BasePlayer player, string id)
        {
            if (id == player.UserIDString || id == player.net.ID.Value.ToString())
                return "you";

            BasePlayer fp = null;

            try
            {
                fp = BasePlayer.allPlayerList.FirstOrDefault(x => x.UserIDString == id || x.net.ID.Value.ToString() == id);
            } catch (Exception e) { }

            return fp?.displayName ?? "N/A";

        }
        #endregion

        #region hooks
        void processingTask()
        {
            var task = Task.Run(() => { });
        }

        object OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            var initiator = info?.InitiatorPlayer;
            if (initiator != null && initiator.userID.IsSteamId())
            {
                GenCombatLog(initiator ?? player);
            }
            else
            {
                GenCombatLog(player);
            }
            return null;
        }

        private void UploadLogs(Dictionary<int, CLogEntry> logs)
        {
            pendingGeneration--;
            totalLogs += logs.Count;

            if (logs.Count > 0)
            {

                if (ServerArmour == null || !ServerArmour.IsLoaded)
                {
                    // LogDebug("ServerArmour plugin is not loaded. Cannot upload logs.");
                    failedUploads += logs.Count;
                    return;
                }

                try
                {
                    // LogDebug($"Requested upload of {logs.Count} entries");
                    ServerArmour.Call("UploadCombatEntries", JsonConvert.SerializeObject(logs.Values));
                    totalLogsUploaded += logs.Count;
                }
                catch (Exception ex)
                {
                    // LogDebug($"Exception during log upload: {ex}");
                    failedUploads += logs.Count;
                }
            }
            else
                LogDebug($"Nothing to upload");
        }

        #endregion

        #region helpers
        private void AddEntries(BasePlayer forPlayer, Queue<CombatLog.Event> cLog)
        {
            Dictionary<int, CLogEntry> logEntries = new Dictionary<int, CLogEntry>();
            foreach (CombatLog.Event evt in cLog)
            {
                var hash = evt.GetHashCode();
                if (!logEntries.ContainsKey(hash) && !usedHashes.Contains(hash))
                {
                    var entry = CreateEntry(forPlayer, evt);
                    if (entry != null)
                    {
                        usedHashes.Add(hash);
                        logEntries.Add(hash, entry);
                    }
                }
            }
            UploadLogs(logEntries);
            logEntries.Clear();
        }

        void cleanupHashes()
        {
            // Check if usedHashes exceeds 100000 entries, then cleanup
            if (usedHashes.Count > 100000)
            {
                // Take the most recent 1000 entries and add a new one
                usedHashes = usedHashes.Skip(usedHashes.Count - 1000).ToList();
            }
        }

        private CLogEntry CreateEntry(BasePlayer forPlayer, CombatLog.Event evt)
        {
            if ((forPlayer is NPCPlayer || forPlayer.IsNpc || !forPlayer.userID.IsSteamId()) && !evt.target_id.IsSteamId())
            {
                // LogDebug($"Event = {evt.GetHashCode()} bot attacker and bot death. Skipped");
                return null;
            }
            totalLogs++;
            // LogDebug($"Event = {evt.GetHashCode()} saved.");
            return CLogEntry.from(forPlayer, evt);
        }

        private void LogDebug(string txt)
        {
            if (debug) Puts($"DEBUG: {txt}");
        }

        static private PInfo UintFind(ulong netId)
        {
            BasePlayer player = null;
            try
            {
                player = BasePlayer.activePlayerList.First(x => x.net.ID.Value == netId);
            }
            catch (Exception e)
            {
            }
            return player != null ? new PInfo { Name = player.displayName, SteamId = player.UserIDString } : new PInfo { Name = netId.ToString(), SteamId = netId.ToString() };
        }

        /**
         * This avoids scientific notations
         */
        static public void RoundOrLimitFloat(ref float value) => value = (value > 1000000) ? value % 1000000 : value;

        public class PInfo
        {
            public string Name;
            public string SteamId;
        }
        #endregion

        public class CLogEntry
        {
            public int EventHash;
            public float EventTime;
            public string AttackerSteamId;
            public string TargetSteamId;
            public string Weapon;
            public string Ammo;
            public string Area;
            public float Distance;
            public float HealthOld;
            public float HealthNew;
            public string EventInfo;
            public int ProjectileHits;
            public float ProjectileIntegrity;
            public float ProjectileTravelTime;
            public float ProjectileTrajectoryMismatch;
            public int Desync;
            public static CLogEntry from(BasePlayer forPlayer, CombatLog.Event evt)
            {
                var pInfo = new PInfo { Name = forPlayer.displayName, SteamId = forPlayer.UserIDString };
                var attacker = evt.attacker == "you" ? pInfo : UintFind(evt.attacker_id);
                var target = evt.target == "you" ? pInfo : UintFind(evt.target_id);
                RoundOrLimitFloat(ref evt.health_new);
                RoundOrLimitFloat(ref evt.health_old);
                return new CLogEntry
                {
                    EventHash = evt.GetHashCode(),
                    AttackerSteamId = attacker.SteamId,
                    TargetSteamId = target.SteamId,
                    Ammo = evt.ammo,
                    Area = HitAreaUtil.Format(evt.area).ToLower(),
                    Distance = evt.distance,
                    EventInfo = evt.info,
                    EventTime = evt.time,
                    HealthNew = evt.health_new,
                    HealthOld = evt.health_old,
                    Desync = evt.desync,
                    ProjectileHits = evt.proj_hits,
                    ProjectileIntegrity = evt.proj_integrity,
                    ProjectileTrajectoryMismatch = evt.proj_mismatch,
                    ProjectileTravelTime = evt.proj_travel,
                    Weapon = evt.weapon
                };
            }
        }
    }
}