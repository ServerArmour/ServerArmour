using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("ServerArmour", "Pho3niX90", "0.0.1")]
    [Description("Protect your server! Auto ban hackers, and notify other server owners of hackers")]
    class ServerArmour : CovalencePlugin
    {
        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double MaxMinutesToRefresh = 60;
        #endregion

        #region Hooks
        void Init()
        {
            Puts("Server Armour is being initialized.");
            LoadSettings();
        }

        void Loaded()
        {
            Puts("Checking all known users.");

            int playerTotal = 0;
            foreach (IPlayer player in players.All)
            {
                if (player != null)
                    GetPlayerBans(player);
                playerTotal++;
            }

            Puts("Checking " + playerTotal + " players");
            Puts("Server Armour finished initializing.");
        }

        void OnUserConnected(IPlayer player)
        {
            GetPlayerBans(player, true);

            Puts($"{player.Name} ({player.Id}) connected from {player.Address}");

            if (player.IsAdmin)
            {
                Puts($"{player.Name} ({player.Id}) is admin");
            }

            Puts($"{player.Name} is {(player.IsBanned ? "banned" : "not banned")}");
        }

        void OnUserDisconnected(IPlayer player)
        {
            Puts($"{player.Name} ({player.Id}) disconnected");
        }

        bool CanUserLogin(string name, string id, string ip)
        {
            if (name.ToLower().Contains("admin"))
            {
                Puts($"{name} ({id}) at {ip} tried to connect with 'admin' in name");
                return false;
            }

            return true;
        }

        void OnUserApproved(string name, string id, string ip)
        {
            Puts($"{name} ({id}) at {ip} has been approved to connect");
        }

        void OnUserNameUpdated(string id, string oldName, string newName)
        {
            Puts($"Player name changed from {oldName} to {newName} for ID {id}");
        }

        void OnUserKicked(IPlayer player, string reason)
        {
            Puts($"Player {player.Name} ({player.Id}) was kicked");
        }

        void OnUserBanned(string name, string id, string ip, string reason)
        {
            Puts($"Player {name} ({id}) at {ip} was banned: {reason}");
        }

        void OnUserUnbanned(string name, string id, string ip)
        {
            Puts($"Player {name} ({id}) at {ip} was unbanned");
        }
        #endregion

        #region WebRequests
        void GetPlayerBans(IPlayer player, bool forceRefresh = false)
        {

            if (forceRefresh)
            {
                Puts("Forcing a refresh for newly connected user " + player.Name);
            }
            else
            {
                if (PlayerCached(player.Id))
                {
                    uint cachedTimestamp = _playerData[player.Id].cacheTimestamp;
                    uint currentTimestamp = _time.GetUnixTimestamp();
                    double minutesOld = (currentTimestamp - cachedTimestamp) / 60.0 / 1000.0;
                    bool oldCache = MaxMinutesToRefresh <= minutesOld;
                    Puts("Player " + player.Name + " exists, " + (oldCache ? "however his cache is old and will now refresh." : "and his cache is still fresh."));

                    if (!oldCache)
                        return; //user already cached, therefore do not check again before cache time laps.
                }
            }

            string url = $"http://io.serverarmour.com/checkUser?steamid={player.Id}&username={player.Name}&ip={player.Address}";
            Puts(url);
            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code != 200 || response == null)
                {
                    Puts($"Couldn't get an answer from ServerArmour.com!");
                    return;
                }

                ISAPlayer isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                _playerData.Add(isaPlayer.steamid, isaPlayer);
                if (isaPlayer != null && isaPlayer.banCount > 0)
                {
                    Puts($"ServerArmour: {isaPlayer.username} checked, and user is dirty with a total of ***{isaPlayer.banCount}*** bans");
                }
                else if (isaPlayer != null)
                {
                    Puts($"ServerArmour: {isaPlayer.username} checked, and user is clean");
                }
                SaveSettings();
            }, this, RequestMethod.GET);
        }
        #endregion
        #region Classes 
        public class ISAPlayer
        {
            public string steamid;
            public string username;
            public int banCount;
            public List<ISABan> banData;
            public uint cacheTimestamp;
        }
        public class ISABan
        {
            public string serverName;
            public string reason;
            public string date;
        }
        #endregion

        #region Data Handling
        bool PlayerCached(string steamid) => _playerData.ContainsKey(steamid);

        void SaveSettings()
        {
            Interface.Oxide.DataFileSystem.WriteObject<Dictionary<string, ISAPlayer>>("ServerArmour", _playerData, true);
        }

        void LoadSettings()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile("ServerArmour"))
            {
                _playerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, ISAPlayer>>("ServerArmour");
                Puts("Settings loaded");
            }
            else
            {
                SaveSettings();
            }
        }
        #endregion
    }
}