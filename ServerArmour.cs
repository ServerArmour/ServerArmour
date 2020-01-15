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
        #endregion

        #region Hooks
        void Init() {
            Puts("Server Armour is being initialized.");
            config = Config.ReadObject<ISAConfig>();

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
                    GetPlayerBans(player, false, player.IsConnected);
                    CheckIfBanned(player);
                }
                playerTotal++;
            }

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

            CheckIfBanned(player);
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
        void GetPlayerBans(IPlayer player, bool forceRefresh = false, bool connectedTime = true) {

            if (forceRefresh) {
                Puts("Forcing a refresh for user " + player.Name);
            } else {
                if (PlayerCached(player.Id)) {
                    uint cachedTimestamp = _playerData[player.Id].cacheTimestamp;
                    uint currentTimestamp = _time.GetUnixTimestamp();
                    double minutesOld = (currentTimestamp - cachedTimestamp) / 60.0 / 1000.0;
                    bool oldCache = cacheLifetime <= minutesOld;
                    Puts("Player " + player.Name + " exists, " + (oldCache ? "however his cache is old and will now refresh." : "and his cache is still fresh."));

                    _playerData[player.Id].lastConnected = _time.GetUnixTimestamp();
                    if (!oldCache)
                        return; //user already cached, therefore do not check again before cache time laps.
                }
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
                _playerData.Add(isaPlayer.steamid, isaPlayer);
                if (isaPlayer != null && isaPlayer.banCount > 0) {
                    Puts($"ServerArmour: {isaPlayer.username} checked, and user is dirty with a total of ***{isaPlayer.banCount}*** bans");
                } else if (isaPlayer != null) {
                    Puts($"ServerArmour: {isaPlayer.username} checked, and user is clean");
                }
                if (webrequest.GetQueueLength() == 0) {
                    Puts("WebQueue now empty, will now save data");
                    SaveData();
                } else {
                    Puts("WebQueue still has " + webrequest.GetQueueLength() + " tasks to process, will not save now");
                }
            }, this, RequestMethod.GET);
        }

        bool CheckIfBanned(IPlayer player) {
            Puts($"{player.Name} is {(player.IsBanned ? "banned" : "not banned")}");
            return false;
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
                    ServerApiKey = ""
                }, true);
            SaveConfig();
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
                Puts("Settings loaded");
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
