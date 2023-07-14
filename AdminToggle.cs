using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("Admin Toggle", "Talha", "1.0.6")]
    [Description("Toggle your admin status")]
    public class AdminToggle : RustPlugin
    {
        private const string perm = "admintoggle.use";
        
        private void Init() { permission.RegisterPermission(perm, this); }
        
        private void Message(BasePlayer player, string key)
        {
            var message = string.Format(lang.GetMessage(key, this, player.UserIDString));
            player.ChatMessage(message);
        }
        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["ToPlayer"] = "You switched to player mode!",
                ["ToAdmin"] = "You switched to admin mode!"
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["ToPlayer"] = "Oyuncu moduna geçiş yaptın!",
                ["ToAdmin"] = "Admin moduna geçiş yaptın!"
            }, this, "tr");
        }

        [ChatCommand("admin")]
        private void Toggle(BasePlayer player)
        {
            if (!player.IPlayer.HasPermission(perm)) return;
            if (player.IsAdmin)
            {
                player.PauseFlyHackDetection(float.MaxValue);
                if (player.IsFlying) { player.SendConsoleCommand("noclip");}
                player.Connection.authLevel = 0;
                ServerUsers.Set(player.userID, ServerUsers.UserGroup.None, player.displayName, "");
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                permission.RemoveUserGroup(player.userID.ToString(), "admin");
                ServerUsers.Save();
                Message(player, "ToPlayer");
            }
            else
            {
                player.Connection.authLevel = 1;
                ServerUsers.Set(player.userID, ServerUsers.UserGroup.Owner, player.displayName, "");
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                permission.AddUserGroup(player.userID.ToString(), "admin");
                ServerUsers.Save();
                Message(player, "ToAdmin");
            }
        }
    }
}