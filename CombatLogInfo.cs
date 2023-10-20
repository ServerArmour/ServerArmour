using Facepunch.Extend;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("CombatLog Info", "Pho3niX90", "1.0.2")]
    [Description("Collects combat log entries, and submits them to SA for analysis.")]
    class CombatLogInfo : RustPlugin
    {
        const bool debug = false;
        Dictionary<int, CLogEntry> logEntries = new Dictionary<int, CLogEntry>();
        Dictionary<ulong, Timer> logTimers = new Dictionary<ulong, Timer>();
        [PluginReference] Plugin ServerArmour;

        Timer mainTimer;

        // Simple stats
        DateTime logsSince = DateTime.Now;
        int totalLogs = 0;
        int totalLogsUploaded = 0;
        //

        void OnServerInitialized(bool first)
        {
            Server.Command("server.combatlogsize 80");
            mainTimer = timer.Every(30, () =>
            {
                UploadLogs(logEntries);
                if (logEntries.Count > (BasePlayer.activePlayerList.Count * 150))
                {
                    timer.Once(2, () => PurgeDb());
                }
            });
        }

        void Unload()
        {
            UploadLogs(logEntries);
            if (mainTimer != null)
                mainTimer.Destroy();
            foreach (var item in logTimers)
            {
                if (!item.Value.Destroyed)
                {
                    item.Value.Destroy();
                }
            }
        }

        #region log generation

        private void GenCombatLog(BasePlayer forPlayer)
        {
            var pInfo = new PInfo { Name = forPlayer.displayName, SteamId = forPlayer.UserIDString };
            var cLog = CombatLog.Get(forPlayer.userID);

            if (!logTimers.ContainsKey(forPlayer.userID) || logTimers[forPlayer.userID].Destroyed)
            {
                logTimers[forPlayer.userID] = timer.Once(ConVar.Server.combatlogdelay, () =>
                {
                    foreach (CombatLog.Event evt in cLog)
                    {
                        AddEntry(forPlayer, evt);
                    }
                    logTimers.Remove(forPlayer.userID);
                });
            } else
            {
                LogDebug($"Log already queued, delaying {ConVar.Server.combatlogdelay}secs");
                logTimers[forPlayer.userID].Reset(ConVar.Server.combatlogdelay);
            }
        }
        #endregion

        #region commands
        [ConsoleCommand("clog.save")]
        void forceSaveLogs(ConsoleSystem.Arg arg)
        {
            UploadLogs(logEntries);
        }
        [ConsoleCommand("clog.stats")]
        void printStats(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith($"Stats since {logsSince}\n\nTotal Logs: {totalLogs}\nTotal Logs Uploaded: {totalLogsUploaded}\nPending Log Generation: {logTimers.Where(x=>!x.Value.Destroyed).Count()}\nPending Logs Upload: {CountEntries()}");
        }
        #endregion

        #region hooks
        object OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            GenCombatLog(player);
            return null;
        }

        object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            GenCombatLog(attacker);
            return null;
        }

        int pendingUploads = 0;
        private void UploadLogs(Dictionary<int, CLogEntry> logs)
        {
            if (CountEntries() > 0)
            {
                pendingUploads += CountEntries();

                if (ServerArmour == null | !ServerArmour.IsLoaded)
                    return;

                LogDebug($"Requested upload of {CountEntries()} entries");
                ServerArmour.Call("UploadCombatEntries", JsonConvert.SerializeObject(LogEntries()));
            }
            else
                LogDebug($"Nothing to upload");
        }
        /**
         * Hook called by serverarmour after upload
         */
        private void OnEntriesUploaded(int c, string r)
        {
            if (c <= 299)
            {
                ClearDb();
                totalLogsUploaded += pendingUploads;
                LogDebug($"Uploaded {pendingUploads} entries");
                pendingUploads = 0;
            }
            else
            {
                PrintWarning($"There was an upload error = {c}: {r}");
            }
        }
        #endregion

        #region helpers
        private void AddEntry(BasePlayer forPlayer, CombatLog.Event evt)
        {
            if ((forPlayer is NPCPlayer || forPlayer.IsNpc || !forPlayer.userID.IsSteamId()) && !evt.target_id.IsSteamId())
            {
                LogDebug($"Event = {evt.GetHashCode()} bot attacker and bot death. Skipped");
                return;
            }
            int entriesBefore = CountEntries();
            if (!logEntries.ContainsKey(evt.GetHashCode()))
            {
                totalLogs++;
                logEntries.Add(evt.GetHashCode(), CLogEntry.from(forPlayer, evt));
                LogDebug($"Event = {evt.GetHashCode()} saved.");
            }
            else
            {
                LogDebug($"Event = {evt.GetHashCode()} already saved. Skipped");
            }
            int entriesAfter = CountEntries();
            LogDebug($"Entries: Before = {entriesBefore}, After = {entriesAfter}");
        }

        private void LogDebug(string txt)
        {
            if (debug) Puts($"DEBUG: {txt}");
        }

        /**
         * We clear the data, leaving the keys so that we do not create unnecessary network overhead by sending duplicates.
         */
        private void ClearDb()
        {
            foreach (var key in logEntries.Keys.ToList())
                logEntries[key] = null;
        }

        /**
         * We will purge the DB on a timer.
         */
        private void PurgeDb() => logEntries.Clear();

        private int CountEntries() => logEntries.Where(x => x.Value != null).Count();
        private List<CLogEntry> LogEntries() => logEntries.Values.Where(x => x != null).ToList();

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

        void findWeaponShortName(string weaponLong)
        {

        }

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