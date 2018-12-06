﻿// <copyright file="SourceBans.cs" company="Steve Guidetti">
// Copyright (c) Steve Guidetti. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace SevenMod.Plugin.SourceBans
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Text;
    using System.Timers;
    using SevenMod.Admin;
    using SevenMod.Chat;
    using SevenMod.Console;
    using SevenMod.ConVar;
    using SevenMod.Core;
    using SevenMod.Database;

    /// <summary>
    /// Plugin that periodically shows messages in chat.
    /// </summary>
    public sealed class SourceBans : PluginAbstract
    {
        /// <summary>
        /// The memory cache.
        /// </summary>
        private readonly SBCache cache = new SBCache();

        /// <summary>
        /// The list of players to be rechecked for bans.
        /// </summary>
        private readonly List<string> recheckPlayers = new List<string>();

        /// <summary>
        /// The timer to retry loading the admin users.
        /// </summary>
        private Timer adminRetryTimer;

        /// <summary>
        /// The timer to retry checking for bans.
        /// </summary>
        private Timer banRecheckTimer;

        /// <summary>
        /// The value of the SBWebsite <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue website;

        /// <summary>
        /// The value of the SBAddban <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue addban;

        /// <summary>
        /// The value of the SBUnban <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue unban;

        /// <summary>
        /// The value of the SBDatabasePrefix <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue databasePrefix;

        /// <summary>
        /// The value of the SBRetryTime <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue retryTime;

        /// <summary>
        /// The value of the SBProcessQueueTime <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue processQueueTime;

        /// <summary>
        /// The value of the SBBackupConfigs <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue backupConfigs;

        /// <summary>
        /// The value of the SBEnableAdmins <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue enableAdmins;

        /// <summary>
        /// The value of the SBRequireSiteLogin <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue requireSiteLogin;

        /// <summary>
        /// The value of the SBServerId <see cref="ConVar"/>.
        /// </summary>
        private ConVarValue serverId;

        /// <summary>
        /// Represents the database connection.
        /// </summary>
        private Database database;

        /// <inheritdoc/>
        public override PluginInfo Info => new PluginInfo
        {
            Name = "SourceBans",
            Author = "SevenMod",
            Description = "Integrates SevenMod with the SourceBans backend.",
            Version = "0.1.0.0",
            Website = "https://github.com/SevenMod/Plugin-SourceBans"
        };

        /// <inheritdoc/>
        public override void OnLoadPlugin()
        {
            this.website = this.CreateConVar("SBWebsite", string.Empty, "Website address to tell the player where to go for unban, etc").Value;
            this.addban = this.CreateConVar("SBAddban", "True", "Allow or disallow admins access to addban command").Value;
            this.unban = this.CreateConVar("SBUnban", "True", "Allow or disallow admins access to unban command").Value;
            this.databasePrefix = this.CreateConVar("SBDatabasePrefix", "sb", "The Table prefix you set while installing the webpanel").Value;
            this.retryTime = this.CreateConVar("SBRetryTime", "45.0", "How many seconds to wait before retrying when a players ban fails to be checked", true, 15, true, 60).Value;
            this.processQueueTime = this.CreateConVar("SBProcessQueueTime", "5", "How often should we process the failed ban queue in minutes").Value;
            this.backupConfigs = this.CreateConVar("SBBackupConfigs", "True", "Enable backing up config files after getting admins from database").Value;
            this.enableAdmins = this.CreateConVar("SBEnableAdmins", "True", "Enable admin part of the plugin").Value;
            this.requireSiteLogin = this.CreateConVar("SBRequireSiteLogin", "False", "Require the admin to login once into website").Value;
            this.serverId = this.CreateConVar("SBServerId", "0", "This is the ID of this server (Check in the admin panel -> servers to find the ID of this server)").Value;

            this.AutoExecConfig(true, "SourceBans");
        }

        /// <inheritdoc/>
        public override void OnConfigsExecuted()
        {
            this.database = Database.Connect("sourcebans");

            PluginManager.Unload("BaseBans");

            this.RegAdminCmd("rehash", AdminFlags.RCON, "Reload SQL admins").Executed += this.OnRehashCommandExecuted;
            this.RegAdminCmd("ban", AdminFlags.Ban, "sm ban <#userid|name> <minutes|0> [reason]").Executed += this.OnBanCommandExecuted;
            this.RegAdminCmd("banip", AdminFlags.Ban, "sm banip <ip|#userid|name> <time> [reason]").Executed += this.OnBanipCommandExecuted;
            this.RegAdminCmd("addban", AdminFlags.RCON, "sm addban <time> <steamid> [reason]").Executed += this.OnAddbanCommandExecuted;
            this.RegAdminCmd("unban", AdminFlags.Unban, "sm unban <steamid|ip> [reason]").Executed += this.OnUnbanCommandExecuted;

            this.enableAdmins.ConVar.ValueChanged += this.OnEnableAdminsChanged;
            this.requireSiteLogin.ConVar.ValueChanged += this.OnRequireSiteLoginChanged;
            this.serverId.ConVar.ValueChanged += this.OnServerIdChanged;
        }

        /// <inheritdoc/>
        public override void OnReloadAdmins()
        {
            if (!this.enableAdmins.AsBool || this.adminRetryTimer != null)
            {
                return;
            }

            if (this.cache.GetAdmins(out var list, out var expired) && !expired)
            {
                foreach (var admin in list)
                {
                    AdminManager.AddAdmin(admin.Identity, admin.Immunity, admin.Flags);
                }

                return;
            }

            this.LoadAdmins();
        }

        /// <inheritdoc/>
        public override bool OnPlayerLogin(ClientInfo client, StringBuilder rejectReason)
        {
            if (this.cache.GetPlayerStatus(client.playerId, out var banned, out var expired) && !expired)
            {
                if (banned)
                {
                    rejectReason.AppendLine($"You have been banned from this server, check {this.website.AsString} for more info");
                    return false;
                }

                return true;
            }

            if (!this.recheckPlayers.Contains(client.playerId))
            {
                this.CheckBans(client);
            }

            return base.OnPlayerLogin(client, rejectReason);
        }

        /// <summary>
        /// Converts a SteamID64 to a SteamID.
        /// </summary>
        /// <param name="playerId">The SteamID64.</param>
        /// <returns>The SteamID.</returns>
        private static string GetAuth(string playerId)
        {
            long.TryParse(playerId, out var id);
            id -= 76561197960265728L;
            var p1 = id % 2;
            var p2 = (id - p1) / 2;

            return $"STEAM_0:{p1}:{p2}";
        }

        /// <summary>
        /// Loads the admin users from the database.
        /// </summary>
        private void LoadAdmins()
        {
            this.database.TQuery($"SELECT name, flags, immunity FROM {this.databasePrefix.AsString}_srvgroups ORDER BY id").QueryCompleted += this.OnGroupsQueryCompleted;
        }

        /// <summary>
        /// Checks a player for bans in the database.
        /// </summary>
        /// <param name="client">The <see cref="ClientInfo"/> object representing the client to check.</param>
        private void CheckBans(ClientInfo client)
        {
            var prefix = this.databasePrefix.AsString;
            var auth = GetAuth(client.playerId).Substring(8);
            var ip = this.database.Escape(client.ip);
            this.database.TQuery($"SELECT bid, ip FROM {prefix}_bans WHERE ((type = 0 AND authid REGEXP '^STEAM_[0-9]:{auth}$') OR (type = 1 AND ip = '{ip}')) AND (length = '0' OR ends > UNIX_TIMESTAMP()) AND RemoveType IS NULL", client).QueryCompleted += this.OnCheckPlayerBansQueryCompleted;
        }

        /// <summary>
        /// Called when the value of the SBEnableAdmins <see cref="ConVar"/> is changed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="ConVarChangedEventArgs"/> object containing the event data.</param>
        private void OnEnableAdminsChanged(object sender, ConVarChangedEventArgs e)
        {
            AdminManager.ReloadAdmins();
        }

        /// <summary>
        /// Called when the value of the SBRequireSiteLogin <see cref="ConVar"/> is changed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="ConVarChangedEventArgs"/> object containing the event data.</param>
        private void OnRequireSiteLoginChanged(object sender, ConVarChangedEventArgs e)
        {
            AdminManager.ReloadAdmins();
        }

        /// <summary>
        /// Called when the value of the SBServerId <see cref="ConVar"/> is changed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="ConVarChangedEventArgs"/> object containing the event data.</param>
        private void OnServerIdChanged(object sender, ConVarChangedEventArgs e)
        {
            AdminManager.ReloadAdmins();
        }

        /// <summary>
        /// Called when the rehash admin command is executed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="AdminCommandEventArgs"/> object containing the event data.</param>
        private void OnRehashCommandExecuted(object sender, AdminCommandEventArgs e)
        {
            if (this.enableAdmins.AsBool)
            {
                AdminManager.ReloadAdmins();
            }
        }

        /// <summary>
        /// Called when the ban admin command is executed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="AdminCommandEventArgs"/> object containing the event data.</param>
        private void OnBanCommandExecuted(object sender, AdminCommandEventArgs e)
        {
            if (e.Arguments.Count < 2)
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "Usage: sm ban <#userid|name> <time|0> [reason]");
                return;
            }

            if (!uint.TryParse(e.Arguments[1], out var duration))
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "Invaid ban duration");
                return;
            }

            if (duration == 0 && !AdminManager.CheckAccess(e.SenderInfo.RemoteClientInfo, AdminFlags.Unban))
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "You do not have perm ban permission.");
                return;
            }

            if (SMConsoleHelper.ParseSingleTargetString(e.SenderInfo, e.Arguments[0], out var target))
            {
                var prefix = this.databasePrefix.AsString;
                var auth = GetAuth(target.playerId);
                var name = this.database.Escape(target.playerName);
                duration *= 60;
                var reason = (e.Arguments.Count > 2) ? this.database.Escape(string.Join(" ", e.Arguments.GetRange(2, e.Arguments.Count - 2).ToArray())) : string.Empty;
                var adminAuth = (e.SenderInfo.RemoteClientInfo != null) ? GetAuth(e.SenderInfo.RemoteClientInfo.playerId) : "STEAM_ID_SERVER";
                var adminIp = (e.SenderInfo.RemoteClientInfo != null) ? e.SenderInfo.RemoteClientInfo.ip : string.Empty;
                this.database.TFastQuery($"INSERT INTO {prefix}_bans (ip, authid, name, created, ends, length, reason, aid, adminIp, sid, country) VALUES ('{target.ip}', '{auth}', '{name}', UNIX_TIMESTAMP(), UNIX_TIMESTAMP() + {duration}, {duration}, '{reason}', IFNULL((SELECT aid FROM {prefix}_admins WHERE authid = '{adminAuth}' OR authid REGEXP '^STEAM_[0-9]:{adminAuth.Substring(8)}$'), '0'), '{adminIp}', {this.serverId.AsInt}, ' ')");

                SdtdConsole.Instance.ExecuteSync($"kick {target.playerId} \"You have been banned from this server, check {this.website.AsString} for more info\"", null);

                ChatHelper.ReplyToCommand(e.SenderInfo, $"{name} has been banned.");
            }
        }

        /// <summary>
        /// Called when the banip admin command is executed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="AdminCommandEventArgs"/> object containing the event data.</param>
        private void OnBanipCommandExecuted(object sender, AdminCommandEventArgs e)
        {
            if (e.Arguments.Count < 2)
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "Usage: sm banip <ip|#userid|name> <time> [reason]");
                return;
            }

            if (!uint.TryParse(e.Arguments[1], out var duration))
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "Invaid ban duration");
                return;
            }

            if (duration == 0 && !AdminManager.CheckAccess(e.SenderInfo.RemoteClientInfo, AdminFlags.Unban))
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "You do not have perm ban permission.");
                return;
            }

            if (SMConsoleHelper.ParseSingleTargetString(e.SenderInfo, e.Arguments[0], out var target))
            {
                var prefix = this.databasePrefix.AsString;
                var name = this.database.Escape(target.playerName);
                duration *= 60;
                var reason = (e.Arguments.Count > 2) ? this.database.Escape(string.Join(" ", e.Arguments.GetRange(2, e.Arguments.Count - 2).ToArray())) : string.Empty;
                var adminAuth = (e.SenderInfo.RemoteClientInfo != null) ? GetAuth(e.SenderInfo.RemoteClientInfo.playerId) : "STEAM_ID_SERVER";
                var adminIp = (e.SenderInfo.RemoteClientInfo != null) ? e.SenderInfo.RemoteClientInfo.ip : string.Empty;
                this.database.TFastQuery($"INSERT INTO {prefix}_bans (type, ip, name, created, ends, length, reason, aid, adminIp, sid, country) VALUES (1, '{target.ip}', '{name}', UNIX_TIMESTAMP(), UNIX_TIMESTAMP() + {duration}, {duration}, '{reason}', IFNULL((SELECT aid FROM {prefix}_admins WHERE authid = '{adminAuth}' OR authid REGEXP '^STEAM_[0-9]:{adminAuth.Substring(8)}$'), '0'), '{adminIp}', {this.serverId.AsInt}, ' ')");

                SdtdConsole.Instance.ExecuteSync($"kick {target.playerId} \"You have been banned by this server, check {this.website.AsString} for more info\"", null);

                ChatHelper.ReplyToCommand(e.SenderInfo, $"{name} has been banned.");
            }
        }

        /// <summary>
        /// Called when the addban admin command is executed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="AdminCommandEventArgs"/> object containing the event data.</param>
        private void OnAddbanCommandExecuted(object sender, AdminCommandEventArgs e)
        {
            if (!this.addban.AsBool)
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "The addban command is disabled.");
                return;
            }

            if (e.Arguments.Count < 2)
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "Usage: sm addban <time> <steamid> [reason]");
                return;
            }

            if (!uint.TryParse(e.Arguments[0], out var duration))
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "Invaid ban duration");
                return;
            }

            if (duration == 0 && !AdminManager.CheckAccess(e.SenderInfo.RemoteClientInfo, AdminFlags.Unban))
            {
                return;
            }

            if (SteamUtils.NormalizeSteamId(e.Arguments[1], out var playerId))
            {
                var prefix = this.databasePrefix.AsString;
                var auth = GetAuth(playerId);
                duration *= 60;
                var reason = (e.Arguments.Count > 2) ? this.database.Escape(string.Join(" ", e.Arguments.GetRange(2, e.Arguments.Count - 2).ToArray())) : string.Empty;
                var adminAuth = (e.SenderInfo.RemoteClientInfo != null) ? GetAuth(e.SenderInfo.RemoteClientInfo.playerId) : "STEAM_ID_SERVER";
                var adminIp = (e.SenderInfo.RemoteClientInfo != null) ? e.SenderInfo.RemoteClientInfo.ip : string.Empty;
                this.database.TFastQuery($"INSERT INTO {prefix}_bans (authid, name, created, ends, length, reason, aid, adminIp, sid, country) VALUES ('{auth}', '', UNIX_TIMESTAMP(), UNIX_TIMESTAMP() + {duration}, {duration}, '{reason}', IFNULL((SELECT aid FROM {prefix}_admins WHERE authid = '{adminAuth}' OR authid REGEXP '^STEAM_[0-9]:{adminAuth.Substring(8)}$'), '0'), '{adminIp}', {this.serverId.AsInt}, ' ')");

                SdtdConsole.Instance.ExecuteSync($"kick {playerId} \"You are banned from this server, check {this.website.AsString} for more info\"", null);

                ChatHelper.ReplyToCommand(e.SenderInfo, $"{e.Arguments[1]} has been banned.");
            }
            else
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, $"{e.Arguments[1]} is not a valid SteamID.");
            }
        }

        /// <summary>
        /// Called when the unban admin command is executed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="AdminCommandEventArgs"/> object containing the event data.</param>
        private void OnUnbanCommandExecuted(object sender, AdminCommandEventArgs e)
        {
            if (!this.unban.AsBool)
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, $"The unban command is disabled. You must use the Web interface at {this.website.AsString}.");
                return;
            }

            if (e.Arguments.Count < 1)
            {
                ChatHelper.ReplyToCommand(e.SenderInfo, "Usage: sm unban <steamid|ip> [reason]");
                return;
            }

            var prefix = this.databasePrefix.AsString;
            if (SteamUtils.NormalizeSteamId(e.Arguments[0], out var playerId))
            {
                var auth = GetAuth(playerId);
                this.database.TQuery($"SELECT bid FROM {prefix}_bans WHERE (type = 0 AND authid = '{auth}') AND (length = '0' OR ends > UNIX_TIMESTAMP()) AND RemoveType IS NULL", e).QueryCompleted += this.OnUnbanQueryCompleted;
            }
            else
            {
                var ip = this.database.Escape(e.Arguments[0]);
                this.database.TQuery($"SELECT bid FROM {prefix}_bans WHERE (type = 1 AND ip = '{ip}') AND (length = '0' OR ends > UNIX_TIMESTAMP()) AND RemoveType IS NULL", e).QueryCompleted += this.OnUnbanQueryCompleted;
            }
        }

        /// <summary>
        /// Called when the admin groups query has completed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="QueryCompletedEventArgs"/> object containing the event data.</param>
        private void OnGroupsQueryCompleted(object sender, QueryCompletedEventArgs e)
        {
            if (!e.Success)
            {
                this.adminRetryTimer = new Timer();
                this.adminRetryTimer.Interval = this.retryTime.AsFloat;
                this.adminRetryTimer.Elapsed += this.OnAdminRetryTimerElapsed;
                this.adminRetryTimer.Enabled = true;
                return;
            }

            var groups = new Dictionary<string, GroupInfo>();
            foreach (DataRow row in e.Results.Rows)
            {
                var name = row.ItemArray.GetValue(0).ToString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var flags = row.ItemArray.GetValue(1).ToString();
                int.TryParse(row.ItemArray.GetValue(2).ToString(), out var immunity);

                groups.Add(name, new GroupInfo(name, immunity, flags));
            }

            var prefix = this.databasePrefix.AsString;
            var queryLastLogin = this.requireSiteLogin.AsBool ? "lastvisit IS NOT NULL AND lastvisit != '' AND " : string.Empty;
            this.database.TQuery($"SELECT authid, (SELECT name FROM {prefix}_srvgroups WHERE name = srv_group AND flags != '') AS srv_group, srv_flags, immunity FROM {prefix}_admins_servers_groups AS asg LEFT JOIN {prefix}_admins AS a ON a.aid = asg.admin_id WHERE {queryLastLogin}server_id = {this.serverId.AsInt} OR srv_group_id = ANY(SELECT group_id FROM {prefix}_servers_groups WHERE server_id = {this.serverId.AsInt}) GROUP BY aid, authid, srv_password, srv_group, srv_flags, user", groups).QueryCompleted += this.OnAdminsQueryCompleted;
        }

        /// <summary>
        /// Called when the admin users query has completed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="QueryCompletedEventArgs"/> object containing the event data.</param>
        private void OnAdminsQueryCompleted(object sender, QueryCompletedEventArgs e)
        {
            if (!e.Success)
            {
                this.adminRetryTimer = new Timer();
                this.adminRetryTimer.Interval = this.retryTime.AsFloat;
                this.adminRetryTimer.Elapsed += this.OnAdminRetryTimerElapsed;
                this.adminRetryTimer.Enabled = true;
                return;
            }

            var groups = (Dictionary<string, GroupInfo>)e.Data;
            var admins = new List<SBCache.Admin>();
            foreach (DataRow row in e.Results.Rows)
            {
                var identity = row.ItemArray.GetValue(0).ToString();
                var groupName = row.ItemArray.GetValue(1).ToString();
                var flags = row.ItemArray.GetValue(2).ToString();
                int.TryParse(row.ItemArray.GetValue(3).ToString(), out var immunity);

                if (groups.TryGetValue(groupName, out var group))
                {
                    flags += group.Flags;
                    immunity = Math.Max(immunity, group.Immunity);
                }

                admins.Add(new SBCache.Admin(identity, flags, immunity));
                AdminManager.AddAdmin(identity, immunity, flags);
            }

            this.cache.SetAdminList(admins, 60 * 5);
        }

        /// <summary>
        /// Called when the admin user list retry timer elapses.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="ElapsedEventArgs"/> object containing the event data.</param>
        private void OnAdminRetryTimerElapsed(object sender, ElapsedEventArgs e)
        {
            this.adminRetryTimer.Dispose();
            this.adminRetryTimer = null;
            this.LoadAdmins();
        }

        /// <summary>
        /// Called when the player bans check query has completed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="QueryCompletedEventArgs"/> object containing the event data.</param>
        private void OnCheckPlayerBansQueryCompleted(object sender, QueryCompletedEventArgs e)
        {
            var client = (ClientInfo)e.Data;
            if (!e.Success)
            {
                this.recheckPlayers.Add(client.playerId);
                if (this.banRecheckTimer == null)
                {
                    this.banRecheckTimer = new Timer();
                    this.banRecheckTimer.Interval = this.retryTime.AsFloat;
                    this.banRecheckTimer.Elapsed += this.OnBanRecheckTimerElapsed;
                    this.banRecheckTimer.Enabled = true;
                }

                return;
            }

            if (e.Results.Rows.Count > 0)
            {
                var prefix = this.databasePrefix.AsString;
                var bid = e.Results.Rows[0].ItemArray.GetValue(0).ToString();
                var ip = e.Results.Rows[0].ItemArray.GetValue(1).ToString();

                if (string.IsNullOrEmpty(e.Results.Rows[0].ItemArray.GetValue(1).ToString()))
                {
                    this.database.TFastQuery($"UPDATE {prefix}_bans SET `ip` = '{ip}' WHERE `bid` = '{bid}'");
                }

                var auth = GetAuth(client.playerId).Substring(8);
                var name = this.database.Escape(client.playerName);
                this.database.TFastQuery($"INSERT INTO {prefix}_banlog (sid, time, name, bid) VALUES ({this.serverId.AsInt}, UNIX_TIMESTAMP(), '{name}', (SELECT bid FROM {prefix}_bans WHERE ((type = 0 AND authid REGEXP '^STEAM_[0-9]:{auth}$') OR(type = 1 AND ip = '{client.ip}')) AND RemoveType IS NULL LIMIT 0, 1))");

                SdtdConsole.Instance.ExecuteSync($"kick {client.playerId} \"You have been banned from this server, check {this.website.AsString} for more info\"", null);
            }

            this.cache.SetPlayerStatus(client.playerId, e.Results.Rows.Count > 0, 60 * 5);
        }

        /// <summary>
        /// Called when the player ban retry timer elapses.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="ElapsedEventArgs"/> object containing the event data.</param>
        private void OnBanRecheckTimerElapsed(object sender, ElapsedEventArgs e)
        {
            this.banRecheckTimer.Dispose();
            this.banRecheckTimer = null;

            var list = this.recheckPlayers.ToArray();
            this.recheckPlayers.Clear();
            foreach (var playerId in list)
            {
                var client = ConnectionManager.Instance.Clients.ForPlayerId(playerId);
                if (client != null)
                {
                    this.CheckBans(client);
                }
            }
        }

        /// <summary>
        /// Called when the unban check query has completed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="QueryCompletedEventArgs"/> object containing the event data.</param>
        private void OnUnbanQueryCompleted(object sender, QueryCompletedEventArgs e)
        {
            if (e.Results.Rows.Count > 0)
            {
                var args = (AdminCommandEventArgs)e.Data;
                var prefix = this.databasePrefix.AsString;
                var bid = e.Results.Rows[0].ItemArray.GetValue(0).ToString();
                var reason = (args.Arguments.Count > 1) ? this.database.Escape(string.Join(" ", args.Arguments.GetRange(1, args.Arguments.Count - 1).ToArray())) : string.Empty;
                var adminAuth = (args.SenderInfo.RemoteClientInfo != null) ? GetAuth(args.SenderInfo.RemoteClientInfo.playerId) : "STEAM_ID_SERVER";
                var adminIp = (args.SenderInfo.RemoteClientInfo != null) ? args.SenderInfo.RemoteClientInfo.ip : string.Empty;
                this.database.TFastQuery($"UPDATE {prefix}_bans SET RemovedBy = (SELECT aid FROM {prefix}_admins WHERE authid = '{adminAuth}' OR authid REGEXP '^STEAM_[0-9]:{adminAuth.Substring(8)}$'), RemoveType = 'U', RemovedOn = UNIX_TIMESTAMP(), ureason = '{reason}' WHERE bid = {bid}");

                ChatHelper.ReplyToCommand(args.SenderInfo, $"{args.Arguments[0]} has been unbanned.");
            }
        }
    }
}
