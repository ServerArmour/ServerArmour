using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Server Armour Elo", "Pho3niX90", "1.0.3")]
    [Description("Elo System")]
    class ServerArmourElo : CovalencePlugin
    {
        [PluginReference]
        Plugin ServerArmour;

        private Dictionary<string, double> eloData = new Dictionary<string, double>();
        private Dictionary<string, string> eloRequest = new Dictionary<string, string>();

        private const string PermissionSeeOwnElo = "serverarmourelo.seeownelo";
        private const string PermissionSeeOtherElo = "serverarmourelo.seeotherelo";

        #region Commands
        [Command("elo")]
        private void CmdElo(IPlayer player, string command, string[] args)
        {
            if (player.IsServer && args.Length == 0)
            {
                player.Reply(GetMessage("Missing SteamId", player.Id));
                return;
            }

            string steamId = args.Length > 0 && args[0].Length == 17 ? args[0] : player.Id;

            string name = GetName(steamId);
            double elo = GetElo(steamId);

            if (steamId.Length != 17)
            {
                player.Reply(GetMessage("Invalid SteamId"));
            }

            if ((args.Length == 0 && elo > 0 && HasPermission(steamId, PermissionSeeOwnElo)) || (args.Length == 0 && player.IsServer))
            {
                player.Reply(GetMessage("Your Elo", player.Id, new Dictionary<string, string> { ["elo"] = elo.ToString() }));
            }
            else if (args.Length == 1 && elo > 0 && HasPermission(steamId, PermissionSeeOtherElo) || player.IsServer)
            {
                player.Reply(GetMessage("Player Elo", player.Id, new Dictionary<string, string> { ["player"] = name, ["elo"] = elo.ToString() }));
            }
            else if (args.Length > 0 && HasPermission(steamId, PermissionSeeOtherElo) || args.Length == 0 && HasPermission(steamId, PermissionSeeOwnElo) || player.IsServer)
            {
                eloRequest.TryAdd(args[0], player.Id);
                player.Reply(GetMessage("Fetching Elo"));
            }
        }
        #endregion

        #region Data Operations
        private void SaveElo(string steamId, double elo)
        {
            if (eloData.ContainsKey(steamId))
            {
                eloData[steamId] = Math.Round(elo);
            }
            else
            {
                eloData.Add(steamId, Math.Round(elo));
            }
        }

        private double GetElo(string steamId)
        {
            if (eloData.ContainsKey(steamId))
            {
                return eloData[steamId];
            }
            else
            {
                ServerArmour?.Call("FetchElo", steamId);
                return -1;
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("ServerArmourElo/eloData", eloData, true);
        }

        private void LoadData()
        {
            Dictionary<string, double> loadedData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, double>>("ServerArmourElo/eloData");
            if (loadedData != null)
            {
                eloData = loadedData;
            }
        }
        #endregion

        #region Helpers
        private bool HasPermission(string playerId, string perm)
        {
            return permission.UserHasPermission(playerId, perm);
        }

        private string GetName(string playerId)
        {
            IPlayer player = covalence.Players.FindPlayerById(playerId);
            return player?.Name ?? playerId;
        }

        private void RegisterPermission(string perm)
        {
            if (!permission.PermissionExists(perm))
            {
                permission.RegisterPermission(perm, this);
            }
        }
        #endregion

        #region API Hooks

        private void CalcElo(string attackedSteamId, string victimSteamId, string hitInfo)
        {
            ServerArmour?.Call("CalcElo", attackedSteamId, victimSteamId, hitInfo);
        }

        /**
         * Called when a player kills another, which changes elo.
         */
        private void OnEloChange(JObject changeValue)
        {
            EloChange eloChange = changeValue.ToObject<EloChange>();
            SaveElo(eloChange.playerA.steamId, eloChange.playerA.eloEnd);
            SaveElo(eloChange.playerB.steamId, eloChange.playerB.eloEnd);
        }

        /**
         * Called when a plugin requests an update.
         */
        private void OnEloUpdate(JObject updateValue)
        {
            EloUpdate eloUpdate = updateValue.ToObject<EloUpdate>();
            string playerId = eloUpdate.steamId;
            string name = GetName(playerId);

            SaveElo(eloUpdate.steamId, eloUpdate.elo);
            string requestedBy = eloRequest.ContainsKey(playerId) ? eloRequest[playerId] : playerId;

            try
            {
                IPlayer player = covalence.Players.FindPlayerById(requestedBy);
                var msg = requestedBy == playerId ?
                    GetMessage("Your Elo", player.Id, new Dictionary<string, string> { ["elo"] = eloUpdate.elo.ToString() }) :
                    GetMessage("Player Elo", player.Id, new Dictionary<string, string> { ["player"] = name, ["elo"] = eloUpdate.elo.ToString() });
                if (player != null)
                {
                    player.Reply(msg);
                }
                else
                {
                    Puts(msg);
                }
                eloRequest.Remove(playerId);
            }
            catch (Exception _ignore)
            {
                // Handle exception
            }
        }
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            RegisterPermission(PermissionSeeOwnElo);
            RegisterPermission(PermissionSeeOtherElo);
            LoadData();
        }

        private void OnPlayerDeath(BasePlayer victim, HitInfo hitInfo)
        {
            if (victim == null || hitInfo == null) return;

            if (hitInfo.damageTypes.GetMajorityDamageType() == DamageType.Fall || hitInfo.Initiator is HotAirBalloon)
            {
                return;
            }

            BasePlayer attacker = victim.lastAttacker?.ToPlayer() ?? hitInfo.InitiatorPlayer;

            if (attacker == null || attacker == victim) return;
            if (victim.IsNpc || !victim.userID.IsSteamId() || !attacker.userID.IsSteamId() || hitInfo == null) return;

            int distance = (int)Vector3.Distance(attacker.transform.position, victim.transform.position);
            string killInfo = JsonConvert.SerializeObject(new { bone = hitInfo.boneName, hitInfo.ProjectileDistance, distance });
            CalcElo(attacker.UserIDString, victim.UserIDString, killInfo);
        }

        private void Unload()
        {
            SaveData();
            eloData.Clear();
            eloData = null;
        }
        #endregion

        #region Localization
        private string GetMessage(string key, string userId = null, Dictionary<string, string> replacements = null)
        {
            string message = lang.GetMessage(key, this, userId);
            if (replacements != null)
            {
                foreach (KeyValuePair<string, string> replacement in replacements)
                {
                    message = message.Replace($"{{{replacement.Key}}}", replacement.Value);
                }
            }
            return message;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Player Elo"] = "{player}'s elo: [#008080ff]{elo}[/#]",
                ["Your Elo"] = "Your elo: [#008080ff]{elo}[/#]",
                ["Fetching Elo"] = "Fetching elo, please wait...",
                ["Invalid SteamId"] = "Invalid SteamId",
                ["Missing SteamId"] = "You need to add a steamid to check, ex: elo steamid"
            }, this, "en");
        }
        #endregion

        #region Classes
        public class EloUpdate
        {
            public int serverId { get; set; }
            public string steamId { get; set; }
            public float elo { get; set; }
            public DateTime updated { get; set; }
        }

        public class EloChange
        {
            public EloPlayer playerA { get; set; }
            public EloPlayer playerB { get; set; }
        }

        public class EloPlayer
        {
            public string steamId { get; set; }
            public int eloStart { get; set; }
            public float eloExpected { get; set; }
            public float eloEnd { get; set; }
            public bool winner { get; set; }
        }
        #endregion
    }
}