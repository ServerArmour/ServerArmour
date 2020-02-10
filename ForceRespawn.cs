namespace Oxide.Plugins {
    [Info("ForceRespawn", "Pho3niX90", "0.1.1")]
    [Description("Forces a player respawn when they cannot from death screen")]
    class ForceRespawn : RustPlugin {

        void OnPlayerInit(BasePlayer player) {
            timer.Once(15f, () => RespawnPlayer(player));
        }

        [Command("frespawn")]
        void KillPLayerEnt3(BasePlayer player, string command, string[] args) {
            if (args.Length == 0) {
                RespawnPlayer(player);
            } else {
                if (!player.IsAdmin) return;
                RespawnPlayer(BasePlayer.Find(args[0]));
            }
        }

        [ChatCommand("frespawn")]
        void KillPLayerEnt(BasePlayer player, string command, string[] args) {
            if (player.IsAdmin && args.Length == 1) {
                RespawnPlayer(BasePlayer.Find(args[0]));
            }
        }

        /*       
                BasePlayer FindOnlinePlayer(string identifier) {
                   int activeCount = BasePlayer.activePlayerList.Count;
                   for (int i = 0; i < activeCount; i++) {
                       BasePlayer player = BasePlayer.activePlayerList[i];
                       if (player.displayName.Equals(identifier) || player.UserIDString.Equals(identifier)) return player;
                   }
                   return null;
               }
               */
        void RespawnPlayer(BasePlayer player) {
            if (player != null && player.IsDead() && player.IsConnected) {
                player.LifeStoryEnd();
                player.Respawn();
            }
        }
    }
}