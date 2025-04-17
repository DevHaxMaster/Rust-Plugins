using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Rust;

namespace Oxide.Plugins
{
    [Info("RustRpDamageControl", "Alpha", "1.5.1")]
    [Description("PvP restriction, toxic flagging with stacking durations, logging, and group-based manual support.")]

    public class RustRpDamageControl : RustPlugin
    {
        private const string DefaultGroup = "default";
        private const string ToxicGroup = "toxic";
        private const string AllowDamagePermission = "RustRpDamageControl.allowdamage";
        private const string PermissionToxicTime = "RustRpDamageControl.toxictime";

        private const string DataFileName = "RustRpDamageControlDataFile";
        private const string LogFileName = "RustRpDamageControl_LogFile";

        private const int DefaultPunishmentSeconds = 3600;

        private Dictionary<ulong, PlayerPunishmentData> toxicPlayers = new();
        private Dictionary<ulong, List<string>> toxicLog = new();

        public class PlayerPunishmentData
        {
            public double ExpirationUnix { get; set; }
            public bool HadAllowDamagePermission { get; set; }
            public int DurationSeconds { get; set; }
        }

        private void Init()
        {
            permission.RegisterPermission(AllowDamagePermission, this);
            permission.RegisterPermission(PermissionToxicTime, this);

            if (!permission.GroupExists(DefaultGroup))
                permission.CreateGroup(DefaultGroup, "Default Group", 0);
            if (!permission.GroupExists(ToxicGroup))
                permission.CreateGroup(ToxicGroup, "Toxic Group", 0);

            permission.GrantGroupPermission(DefaultGroup, PermissionToxicTime, this);

            LoadData();
            LoadLog();
            timer.Every(60f, CheckExpiredPunishments);
        }

        private void Unload()
        {
            SaveData();
            SaveLog();
        }
                private void OnPlayerInit(BasePlayer player)
        {
            if (!permission.UserHasGroup(player.UserIDString, DefaultGroup))
                permission.AddUserGroup(player.UserIDString, DefaultGroup);

            if (!permission.UserHasPermission(player.UserIDString, AllowDamagePermission))
                player.ChatMessage("You cannot damage players until you have been verified.");

            if (IsToxic(player.userID))
            {
                permission.AddUserGroup(player.UserIDString, ToxicGroup);
                player.ChatMessage("You are marked as toxic: no damage, chat, or voice access.");
            }
        }

        private void OnUserGroupAdded(string userId, string group)
        {
            if (group != ToxicGroup) return;

            if (!ulong.TryParse(userId, out var steamId)) return;

            // Don't override if already set via SetToxic (duration-based)
            if (toxicPlayers.ContainsKey(steamId)) return;

            var player = BasePlayer.FindByID(steamId) ?? BasePlayer.FindSleeping(steamId);
            if (player == null) return;

            bool hadPermission = permission.UserHasPermission(userId, AllowDamagePermission);
            if (hadPermission)
                permission.RevokeUserPermission(userId, AllowDamagePermission);

            // Add as permanent toxic status only if no previous duration exists
            toxicPlayers[steamId] = new PlayerPunishmentData
            {
                ExpirationUnix = double.MaxValue,
                HadAllowDamagePermission = hadPermission,
                DurationSeconds = 0
            };

            if (!toxicLog.ContainsKey(steamId))
                toxicLog[steamId] = new List<string>();

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
            toxicLog[steamId].Add($"Manually added to toxic group at {timestamp} (permanent)");

            SaveLog();
            SaveData();

            player.ChatMessage("<color=red>You have been marked as permanently toxic and may not:\n- PVP\n- PVE\n- Use Voice Chat\n- Use Text Chat</color>");
        }       

        private void OnUserGroupRemoved(string userId, string group)
        {
            if (group != ToxicGroup) return;

            if (!ulong.TryParse(userId, out var steamId)) return;
            if (!toxicPlayers.ContainsKey(steamId)) return;

            var data = toxicPlayers[steamId];
            if (data.HadAllowDamagePermission)
                permission.GrantUserPermission(userId, AllowDamagePermission, this);

            toxicPlayers.Remove(steamId);
            SaveData();

            var player = BasePlayer.FindByID(steamId);
            player?.ChatMessage("<color=green>Your toxic status has been cleared. You may now use PvP, PvE, chat, and voice again.</color>");
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var attacker = info?.InitiatorPlayer;
            if (attacker == null || attacker.userID == 0 || attacker.IsNpc || !attacker.IsConnected)
                return;

            if (IsToxic(attacker.userID))
            {
                info.damageTypes.ScaleAll(0);
                attacker.ChatMessage("<color=red>You have been marked toxic and may not:\n- PVP\n- PVE\n- Use Voice Chat\n- Use Text Chat\n\nUse /toxictime to check your remaining punishment time.</color>");
                return;
            }

            if (!(entity is BasePlayer)) return;

            if (!permission.UserHasPermission(attacker.UserIDString, AllowDamagePermission))
            {
                info.damageTypes.ScaleAll(0);
                attacker.ChatMessage("You cannot damage other players.");
            }
        }

        private object OnPlayerChat(BasePlayer player, string message) => IsToxic(player.userID) ? BlockInteraction(player, "chat") : null;
        private object OnPlayerVoice(BasePlayer player) => IsToxic(player.userID) ? BlockInteraction(player, "voice") : null;

        private object BlockInteraction(BasePlayer player, string type)
        {
            player.ChatMessage($"You are {type}-muted due to toxic behavior.");
            return false;
        }
        [ChatCommand("toxictime")]
        private void CmdToxicTime(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionToxicTime))
            {
                player.ChatMessage("You do not have permission to use this command.");
                return;
            }

            if (!IsToxic(player.userID))
            {
                player.ChatMessage("You are not currently toxic.");
                return;
            }

            var timeLeft = toxicPlayers[player.userID].ExpirationUnix - GetUnixTime();
            player.ChatMessage($"Remaining: {(timeLeft >= double.MaxValue ? "Permanent" : FormatTime((int)timeLeft))}.");
        }

        [ConsoleCommand("tox.settoxic")]
        private void ConsoleSetToxic(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Usage: tox.settoxic <steamID> [duration]");
                return;
            }
            if (!ulong.TryParse(arg.Args[0], out var steamId))
            {
                arg.ReplyWith("Invalid SteamID.");
                return;
            }

            var target = BasePlayer.FindByID(steamId) ?? BasePlayer.FindSleeping(steamId);
            if (target == null) { arg.ReplyWith("Player not found."); return; }

            int duration = ParseDuration(arg.Args.Length > 1 ? arg.Args[1] : null);
            SetToxic(target, duration);
            arg.ReplyWith($"{target.displayName} marked toxic (+{FormatTime(duration)}).");
        }

        [ConsoleCommand("tox.cleartoxic")]
        private void ConsoleClearToxic(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Usage: tox.cleartoxic <steamID>");
                return;
            }

            if (!ulong.TryParse(arg.Args[0], out var steamId))
            {
                arg.ReplyWith("Invalid SteamID.");
                return;
            }

            ClearToxic(steamId);
            arg.ReplyWith($"Toxic status cleared for {steamId}.");
        }

        [ConsoleCommand("tox.list")]
        private void ConsoleListToxic(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            if (toxicPlayers.Count == 0)
            {
                arg.ReplyWith("No players are currently toxic.");
                return;
            }

            foreach (var entry in toxicPlayers)
            {
                var remaining = entry.Value.ExpirationUnix - GetUnixTime();
                arg.ReplyWith($"{entry.Key} - {(remaining >= double.MaxValue ? "Permanent" : FormatTime((int)remaining))}");
            }
        }

        [ConsoleCommand("tox.log")]
        private void ConsoleLogToxic(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;

            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Usage: tox.log <steamID>");
                return;
            }

            if (!ulong.TryParse(arg.Args[0], out var steamId))
            {
                arg.ReplyWith("Invalid SteamID.");
                return;
            }

            if (!toxicLog.ContainsKey(steamId))
            {
                arg.ReplyWith($"No toxic history found for {steamId}.");
                return;
            }

            var entries = toxicLog[steamId];
            var output = $"Toxic log for {steamId} ({entries.Count} entries):\n";
            foreach (var entry in entries)
                output += $" - {entry}\n";

            arg.ReplyWith(output);
        } private void SetToxic(BasePlayer player, int duration)
        {
            permission.AddUserGroup(player.UserIDString, ToxicGroup);
            bool hadPermission = permission.UserHasPermission(player.UserIDString, AllowDamagePermission);
            if (hadPermission)
                permission.RevokeUserPermission(player.UserIDString, AllowDamagePermission);

            double currentTime = GetUnixTime();
            double newExpiration;

            if (IsToxic(player.userID))
            {
                newExpiration = toxicPlayers[player.userID].ExpirationUnix + duration;
                duration = (int)(newExpiration - currentTime);
            }
            else
            {
                newExpiration = currentTime + duration;
            }

            toxicPlayers[player.userID] = new PlayerPunishmentData
            {
                ExpirationUnix = newExpiration,
                HadAllowDamagePermission = hadPermission,
                DurationSeconds = duration
            };

            if (!toxicLog.ContainsKey(player.userID))
                toxicLog[player.userID] = new List<string>();

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
            toxicLog[player.userID].Add($"Marked toxic (+{FormatTime(duration)}) at {timestamp}");

            SaveLog();
            SaveData();

            player.ChatMessage("<color=red>You have been marked as toxic and may not:\n- PVP\n- PVE\n- Use Voice Chat\n- Use Text Chat\n\nUse /toxictime to check your remaining punishment time.</color>");
        }

        private void ClearToxic(ulong userID)
        {
            permission.RemoveUserGroup(userID.ToString(), ToxicGroup);
            if (toxicPlayers.TryGetValue(userID, out var data) && data.HadAllowDamagePermission)
                permission.GrantUserPermission(userID.ToString(), AllowDamagePermission, this);

            toxicPlayers.Remove(userID);
            SaveData();
        }

        private bool IsToxic(ulong id) => toxicPlayers.ContainsKey(id) && toxicPlayers[id].ExpirationUnix > GetUnixTime();

        private void CheckExpiredPunishments()
        {
            var now = GetUnixTime();
            var expired = toxicPlayers.Where(p => p.Value.ExpirationUnix <= now).Select(p => p.Key).ToList();
            foreach (var id in expired)
            {
                ClearToxic(id);
                Puts($"Toxic status expired for {id}");
            }
        }

        private int ParseDuration(string input)
        {
            if (string.IsNullOrEmpty(input)) return DefaultPunishmentSeconds;
            input = input.ToLower();
            if (input.EndsWith("h") && int.TryParse(input.TrimEnd('h'), out var h)) return h * 3600;
            if (input.EndsWith("m") && int.TryParse(input.TrimEnd('m'), out var m)) return m * 60;
            if (int.TryParse(input, out var fallback)) return fallback * 60;
            return DefaultPunishmentSeconds;
        }

        private double GetUnixTime() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private string FormatTime(int seconds)
        {
            if (seconds <= 0) return "expired";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
        }

        private void LoadData() =>
            toxicPlayers = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerPunishmentData>>(DataFileName) ?? new();

        private void SaveData() =>
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, toxicPlayers);

        private void LoadLog() =>
            toxicLog = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, List<string>>>(LogFileName) ?? new();

        private void SaveLog() =>
            Interface.Oxide.DataFileSystem.WriteObject(LogFileName, toxicLog);
    }
}

/*
Admin Console Commands (RCON/Server Console):

tox.settoxic <SteamID> [duration]
 Marks a player toxic (blocks PvP, PvE, chat, and voice). Defaults to an hour
 **You can set to anytime you want just do 1h or 1m to indicate minutes or hours**

tox.cleartoxic <SteamID>
 Clears toxic status and restores previous PvP permission (verified)
Does NOT remove perms, overrides it

 Lists all currently toxic players <SteamIDs> with time remaining.

tox.log <SteamID> -- shows admins all the times they were marked as toxic

Oxide Commands:

oxide.usergroup add <steamID> toxic: Manually mark a player permanently toxic (fully enforced).
oxide.usergroup remove <steamID> toxic: Manually clear permanent toxic status (restores permissions)

Key Set Up:
Apply = RustRpDamageControl.allowdamage to Verified Oxide Group
oxide.grant group default RustRpDamageControl.allowdamage

Apply = RustRpDamageControl.toxictime to default  Oxide group
oxide.grant group default RustRpDamageControl.toxictime

Logging Files
RustRpDamageControlDataFile.json: Stores active toxic status for each player.
RustRpDamageControl_LogFile.json: Logs every toxic action with timestamp (UTC).

Notes:
- When you add time to someone being toxic, if not perm, it will add the total time for each time the comamand is used
- The perms will stick during a disconnect / reconnect

*/