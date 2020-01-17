please mpte this is a BETA plugin, all features not yet available
* autobanning still beaing tested to make sure all is well. Will be available within a week. 
# ServerArmour
Protect your gaming server against hackers, scripters, cheats and grievers!

# Configuration

## Default config

```
{
  "AutoBanCeiling": 1,// Auto ban players with X amount of previous bans.
  "AutoBanGroup": "serverarmour.bans", // the group name that banned users should be added in
  "AutoBanOn": true, // turn auto banning on or off. 
  "AutoBanReasonKeywords": [
    "aimbot",
    "esp"
  ], // auto ban users that have these keywords in previous ban reasons.
  "Debug": false, // never turn on, unless asked to do so by the developer, otherwise your logs will contain tons of messages.
  "ServerAdminEmail": "", // please fill in your main admins email. This is to add a better trust level to your server.
  "ServerAdminName": "", // please fill in your main admins real name. This is to add a better trust level to your server.
  "ServerApiKey": "TEST", //leave as is
  "ServerName": "", // You can change this manually if you like, but will autopopulate each startup
  "ServerPort": "",  // You can change this manually if you like, but will autopopulate each startup
  "ServerVersion": "", // You can change this manually if you like, but will autopopulate each startup
  "WatchlistCeiling": 1, // Auto add players with X amount of previous bans to a watchlist.
  "WatchlistGroup": "serverarmour.watchlist" // the group name that watched users should be added in
}
```


# API Hooks

## Methods


```
int API_GetServerBanCount(string steamid) // Get the count of servers this use has been banned on
bool API_GetIsVacBanned(string steamid) // Indicates whether or not the player has VAC bans on record.
bool API_GetIsCommunityBanned(string steamid) // Indicates whether or not the player is banned from Steam Community
int API_GetVacBanCount(string steamid) // Number of VAC bans on record.
int API_GetGameBanCount(string steamid) // Number of bans in games, this includes CS:GO Overwatch bans.
string API_GetEconomyBanStatus(string steamid) // The player's ban status in the economy. If the player has no bans on record the string will be "none", if the player is on probation it will say "probation", etc.
bool API_GetIsPlayerDirty(string steamid) // Indicates if the player has any bans at all, includes server, game and vac bans
```


## Example:
```

[PluginReference]
Plugin ServerArmour;

void OnUserConnected(IPlayer player) {
{
    Puts(ServerArmour.Call<bool>("API_GetIsPlayerDirty", player.Id));
}
```

* please note that the above is a covalence example, please use the steamid relevant to your game type if you aren't using covalence.