using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("ServerArmour", "Pho3niX90", "0.0.1")]
    [Description("Protect your server! Auto ban hackers, and notify other server owners of hackers")]
    class ServerArmour : CovalencePlugin
    {
        void Init()
        {
            Puts("Init works!");
        }

        void OnUserConnected(IPlayer player)
        {
            Puts($"{player.Name} ({player.Id}) connected from {player.Address}");

            if (player.IsAdmin)
            {
                Puts($"{player.Name} ({player.Id}) is admin");
            }

            Puts($"{player.Name} is {(player.IsBanned ? "banned" : "not banned")}");

            server.Broadcast($"Welcome {player.Name} to {server.Name}!");
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
    }
}