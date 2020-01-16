using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;

namespace Oxide.Plugins {
    [Info("ServerArmour", "Pho3niX90", "0.0.1")]
    [Description("Protect your server! Auto ban hackers, and notify other server owners of hackers")]
    class ServerArmour : CovalencePlugin {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 300; // minutes
        string[] groups;
        private ISAConfig config;
        static string thisServerIp;
        #endregion

        #region Hooks
        void Init() {
            Puts("Server Armour is being initialized.");
            config = Config.ReadObject<ISAConfig>();
            thisServerIp = server.Address.ToString();
            if (config.ServerVersion.Equals(server.Version.ToString())) {
                config.ServerName = server.Name;
                config.ServerPort = server.Port;
                config.ServerVersion = server.Version;
                SaveData();
            }

            LoadDefaultMessages();

            CheckGroups();

            LoadData();
        }

        void Loaded() {
            Puts("Checking all known users.");

            int playerTotal = 0;
            foreach (IPlayer player in players.All) {
                if (player != null) {
                    GetPlayerBans(player, true, player.IsConnected);
                }
                playerTotal++;
            }

            CheckLocalBans();

            Puts("Checking " + playerTotal + " players");
            Puts("Server Armour finished initializing.");
        }

        void Unload() {
            Puts("Server Armour unloading, will now save all data.");
            SaveConfig();
            SaveData();
            Puts("Server Armour finished unloaded.");
        }

        void OnUserConnected(IPlayer player) {
            GetPlayerBans(player);

            Puts($"{player.Name} ({player.Id}) connected from {player.Address}");

            if (player.IsAdmin) {
                Puts($"{player.Name} ({player.Id}) is admin");
            }

            // CheckIfBanned(player);
        }

        void OnUserDisconnected(IPlayer player) {
            Puts($"{player.Name} ({player.Id}) disconnected");
        }

        bool CanUserLogin(string name, string id, string ip) {
            if (name.ToLower().Contains("admin")) {
                Puts($"{name} ({id}) at {ip} tried to connect with 'admin' in name");
                return false;
            }

            return true;
        }

        void OnUserApproved(string name, string id, string ip) {
            Puts($"{name} ({id}) at {ip} has been approved to connect");
        }

        void OnUserNameUpdated(string id, string oldName, string newName) {
            Puts($"Player name changed from {oldName} to {newName} for ID {id}");
        }

        void OnUserKicked(IPlayer player, string reason) {
            Puts($"Player {player.Name} ({player.Id}) was kicked");
        }

        void OnUserBanned(string name, string id, string ip, string reason) {
            Puts($"Player {name} ({id}) at {ip} was banned: {reason}");
        }

        void OnUserUnbanned(string name, string id, string ip) {
            Puts($"Player {name} ({id}) at {ip} was unbanned");
        }

        #endregion

        #region WebRequests
        void GetPlayerBans(IPlayer player, bool onlyGetUncached = false, bool connectedTime = true, bool forceRefresh = false) {


            if (PlayerCached(player.Id) && !onlyGetUncached) {
                uint cachedTimestamp = _playerData[player.Id].cacheTimestamp;
                uint currentTimestamp = _time.GetUnixTimestamp();
                double minutesOld = (currentTimestamp - cachedTimestamp) / 60.0 / 1000.0;
                bool oldCache = cacheLifetime <= minutesOld;
                LogDebug("Player " + player.Name + " exists, " + (oldCache ? "however his cache is old and will now refresh." : "and his cache is still fresh."));

                _playerData[player.Id].lastConnected = _time.GetUnixTimestamp();
                if (!oldCache)
                    return; //user already cached, therefore do not check again before cache time laps.
            }


            string url = $"http://io.serverarmour.com/checkUser?steamid={player.Id}&username={player.Name}&ip={player.Address}" + ServerGetString();
            Puts(url);
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts($"Couldn't get an answer from ServerArmour.com!");
                    return;
                }
                ISAPlayer isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                isaPlayer.lastConnected = _time.GetUnixTimestamp();
                if (!PlayerCached(isaPlayer.steamid)) {
                    _playerData.Add(isaPlayer.steamid, isaPlayer);
                } else {
                    _playerData[isaPlayer.steamid].banData = isaPlayer.banData;
                }

                if (isaPlayer != null && isaPlayer.banCount > 0) {
                    Puts($"ServerArmour: {isaPlayer.username} checked, and user is dirty with a total of ***{isaPlayer.banCount}*** bans");
                } else if (isaPlayer != null) {
                    LogDebug($"ServerArmour: {isaPlayer.username} checked, and user is clean");
                }

                SaveData();

            }, this, RequestMethod.GET);
        }

        void AddBan(IPlayer player, string banreason) {
            string dateTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm").ToString();
            string url = $"http://io.serverarmour.com/addBan?steamid={player.Id}&username={player.Name}&ip={player.Address}&reason={banreason}&dateTime={dateTime}" + ServerGetString();
            Puts(url);
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts($"Couldn't get an answer from ServerArmour.com!");
                    return;
                }
                Puts("Ban submitted for player " + player.Name);

                if (PlayerCached(player.Id.ToString())) {
                    _playerData[player.Id.ToString()].banData.Add(new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = server.Address.ToString() });
                } else {
                    _playerData.Add(player.Id.ToString(),
                        new ISAPlayer {
                            steamid = player.Id.ToString(),
                            username = player.Name,
                            banCount = 1,
                            cacheTimestamp = _time.GetUnixTimestamp(),
                            lastConnected = _time.GetUnixTimestamp(),
                            banData = new List<ISABan> { new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = server.Address.ToString() } }
                        });
                }
                SaveData();
            }, this, RequestMethod.GET);
        }

        #endregion

        #region Data Handling
        bool PlayerCached(string steamid) => _playerData.ContainsKey(steamid);
        T GetConfig<T>(string name, T defaultValue) => Config[name] == null ? defaultValue : (T)Convert.ChangeType(Config[name], typeof(T));
        protected override void LoadDefaultConfig() {
            LogWarning("Creating a new configuration file");
            Config.WriteObject(
                new ISAConfig {
                    AutoBanGroup = "serverarmour.bans",
                    AutoBanOn = true,
                    AutoBanCeiling = 1,
                    AutoBanReasonKeywords = new string[] { "aimbot", "esp" },

                    WatchlistGroup = "serverarmour.watchlist",
                    WatchlistCeiling = 1,

                    ServerName = server.Name,
                    ServerPort = server.Port,
                    ServerVersion = server.Version,
                    ServerAdminName = "",
                    ServerAdminEmail = "",
                    ServerApiKey = "FREE"
                }, true);
            SaveConfig();
        }

        private void LogDebug(string txt) {
            if (config.Debug) Puts(txt);
        }

        protected override void LoadDefaultMessages() {
            lang.RegisterMessages(new Dictionary<string, string> {
                ["EpicThing"] = "An epic thing has happened"
            }, this, "en");
        }

        string GetMsg(IPlayer player, string key) {
            return lang.GetMessage(key, this, player.Id);
        }

        void SaveData() {
            Interface.Oxide.DataFileSystem.WriteObject<Dictionary<string, ISAPlayer>>("ServerArmour", _playerData, true);
        }

        void LoadData() {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile("ServerArmour")) {
                _playerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, ISAPlayer>>("ServerArmour");
                Puts("DATA loaded");
            } else {
                SaveData();
            }
        }

        void CheckGroups() {
            Puts("Registering groups");

            string[] groups = permission.GetGroups();

            Puts("Checking if config groups exists.");

            string autobanGroup = GetConfig("AutoBanGroup", "serverarmour.bans");
            string watchlistGroup = GetConfig("AutoBanGroup", "serverarmour.watchlists");

            if (!permission.GroupExists(autobanGroup)) {
                permission.CreateGroup(autobanGroup, "Server Armour Autobans", 0);
            }

            if (!permission.GroupExists(watchlistGroup)) {
                permission.CreateGroup(watchlistGroup, "Server Armour Watchlist", 0);
            }
        }

        string ServerGetString() {
            return $"&sn={config.ServerName}&sp={config.ServerPort}&an={config.ServerAdminName}&ae={config.ServerAdminEmail}&gameId={covalence.ClientAppId}&gameName={covalence.Game}";
        }

        void CheckLocalBans() {
            // player.BanTimeRemaining;
            foreach (ServerUsers.User usr in ServerUsers.GetAll(ServerUsers.UserGroup.Banned)) {
                Puts("Banned: " + usr.group + " " + usr.notes + " " + usr.steamid);

                bool containsMyBan = false;

                Puts($" check steamid '{usr.steamid.ToString()}'");
                if (PlayerCached(usr.steamid.ToString())) {

                    Puts("There is cached data for player.");
                    Puts("Player has " + _playerData[usr.steamid.ToString()].banData.Count);
                    foreach (ISABan ban in _playerData[usr.steamid.ToString()].banData) {
                        Puts("serverIp " + ban.serverIp + " myserverip " + thisServerIp);
                        if (ban.serverIp.Equals(thisServerIp)) {
                            containsMyBan = true;
                            Puts(" " + ban.serverIp);
                            break;
                        }
                    }
                }

                if (!containsMyBan) {
                    IPlayer player = covalence.Players.FindPlayer(usr.steamid.ToString());
                    Puts("Submitting ban for player " + player.Name);
                    AddBan(player, usr.notes);
                }
            }
        }

        #endregion

        #region Classes 
        public class ISAPlayer {
            public string steamid;
            public string username;
            public int banCount;
            public List<ISABan> banData;
            public uint cacheTimestamp;
            public uint lastConnected;
        }

        public class ISABan {
            public string serverName;
            public string serverIp;
            public string reason;
            public string date;
        }

        private class ISAConfig {
            public bool Debug;

            public string AutoBanGroup;
            public bool AutoBanOn;
            public int AutoBanCeiling;
            public string[] AutoBanReasonKeywords;

            public string WatchlistGroup;
            public int WatchlistCeiling;

            public string ServerName;
            public int ServerPort;
            public string ServerVersion;
            public string ServerAdminName;
            public string ServerAdminEmail;
            public string ServerApiKey;
        }
        #endregion
    }
}
