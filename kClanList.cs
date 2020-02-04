using Newtonsoft.Json.Linq;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
//Requires: Clans
namespace Oxide.Plugins {
    [Info("kClanList", "MisterBrownZA", "0.0.1")]
    [Description("Gets list of members in a clan using clan from killy0u")]
    class kClanList : CovalencePlugin {

        [PluginReference]
        Plugin Clans;
        private void Init() {
        }

        [Command("test")]
        private void TestCommand(IPlayer player, string command, string[] args) {
            JObject myClan = Clans?.Call<JObject>("GetClan", "King");
            foreach (string member in myClan["members"]) {
                Puts(member);
                Puts(players.FindPlayer(member).Name);
            }
        }
    }
}