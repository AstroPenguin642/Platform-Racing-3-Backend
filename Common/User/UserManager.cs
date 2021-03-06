﻿using log4net;
using Microsoft.Extensions.Caching.Memory;
using Platform_Racing_3_Common.Database;
using Platform_Racing_3_Common.Extensions;
using Platform_Racing_3_Common.Redis;
using Platform_Racing_3_Common.Customization;
using Platform_Racing_3_Common.Utils;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Platform_Racing_3_Common.User
{
    public class UserManager
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        //TODO: Player caching is kinda... ehhh... mess, we should have some kinda version number so we can decide what request should be thrown away
        //This is kinda complicated as the this class is supposed to be able to handle concurrency without issues also some kinda redis events should be implemented
        //This is not that important as we dont use cached results when we log in tho it has race conditions due to it not being perfect, this should not bring any big issues tho
        //Yeah, I'm lazy bastard at doing this properly at the moment

        private static readonly TimeSpan UserCacheTime = TimeSpan.FromDays(1);
        private static readonly MemoryCache Users = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromHours(1),
        });

        private static readonly TimeSpan UserIdsCacheTime = TimeSpan.FromDays(7);
        private static readonly MemoryCache UserIds = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromDays(1),
        });

        public static Task<PlayerUserData> TryGetUserDataByIdAsync(uint userId, bool allowCached = true)
        {
            if (userId == 0)
            {
                throw new ArgumentException(nameof(userId));
            }

            if (allowCached)
            {
                if (!UserManager.Users.TryGetValue(userId, out Lazy <PlayerUserData> lazyPlayerUserData)) //We are assuming that the user is already loaded into the memory thus not needing to do extra allocation for GetOrCreate method
                {
                    lazyPlayerUserData = UserManager.Users.GetOrCreate(userId, (cacheEntry) =>
                    {
                        cacheEntry.SlidingExpiration = UserManager.UserCacheTime;

                        return new Lazy<PlayerUserData>(() =>
                        {
                            return RedisConnection.GetDatabase().HashGetAsync($"users:{userId}", new RedisValue[]
                            {
                                "id",
                                "username",

                                "permission_rank",
                                "name_color",
                                "group_name",

                                "last_login",
                                "last_online",

                                "total_exp",
                                "bonus_exp",

                                "hats",
                                "heads",
                                "bodys",
                                "feets",

                                "hat",
                                "hatcolor",

                                "head",
                                "headcolor",

                                "body",
                                "bodycolor",

                                "feet",
                                "feetcolor",

                                "speed",
                                "accel",
                                "jump",

                                "campaigns",

                                "friends",
                                "ignored",
                            }).ContinueWith(UserManager.ParseRedisUserData).ContinueWith((task) => task.Result ?? UserManager.TryGetUserDataByIdAsync(userId, false).Result).Result;
                        }, LazyThreadSafetyMode.ExecutionAndPublication);
                    });
                }

                if (lazyPlayerUserData.IsValueCreated)
                {
                    return Task.FromResult(lazyPlayerUserData.Value);
                }

                return Task.Factory.StartNew(() => lazyPlayerUserData.Value);
            }

            return DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT u.id, u.username, u.permission_rank, u.name_color, u.group_name, u.total_exp, u.bonus_exp, u.hats, u.heads, u.bodys, u.feets, u.current_hat, u.current_hat_color, u.current_head, u.current_head_color, u.current_body, u.current_body_color, u.current_feet, u.current_feet_color, u.speed, u.accel, u.jump, u.last_online, array_remove(array_agg(DISTINCT f.friend_user_id), NULL) AS friends, array_remove(array_agg(DISTINCT i.ignored_user_id), NULL) AS ignored, array_agg(ARRAY[c.level_id, c.finish_time]) AS campaign_runs FROM base.users u LEFT JOIN base.friends f ON u.id = f.user_id LEFT JOIN base.ignored i ON u.id = i.user_id LEFT JOIN base.campaigns_runs c ON c.user_id = u.id WHERE u.id = {userId} GROUP BY u.id LIMIT 1").ContinueWith(UserManager.ParseSqlUserData));
        }

        public static Task<PlayerUserData> TryGetUserDataByNameAsync(string username, bool allowCached = true)
        {
            if (username == null)
            {
                throw new ArgumentException(nameof(username));
            }

            username = username.ToUpperInvariant(); //Hmh.... Case insetivity needed here
            
            if (allowCached)
            {
                if (!UserManager.UserIds.TryGetValue(username, out Lazy<uint> lazyUserId))
                {
                    lazyUserId = UserManager.UserIds.GetOrCreate(username, (cacheEntry) =>
                    {
                        cacheEntry.SlidingExpiration = UserManager.UserIdsCacheTime;

                        return new Lazy<uint>(() =>
                        {
                            RedisValue userId = RedisConnection.GetDatabase().HashGet($"userids:{Math.Floor(username.CountCharsSum() / 100.0)}", username);
                            if (userId.HasValue)
                            {
                                return (uint)userId;
                            }
                            else
                            {
                                return DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT u.id, u.username, u.permission_rank, u.name_color, u.group_name, u.total_exp, u.bonus_exp, u.hats, u.heads, u.bodys, u.feets, u.current_hat, u.current_hat_color, u.current_head, u.current_head_color, u.current_body, u.current_body_color, u.current_feet, u.current_feet_color, u.speed, u.accel, u.jump, u.last_online, u.last_online, array_remove(array_agg(DISTINCT f.friend_user_id), NULL) AS friends, array_remove(array_agg(DISTINCT i.ignored_user_id), NULL) AS ignored, array_agg(ARRAY[c.level_id, c.finish_time]) AS campaign_runs FROM base.users u LEFT JOIN base.friends f ON u.id = f.user_id LEFT JOIN base.ignored i ON u.id = i.user_id LEFT JOIN base.campaigns_runs c ON c.user_id = u.id WHERE u.username ILIKE {username} GROUP BY u.id LIMIT 1").ContinueWith(UserManager.ParseSqlUserData)).Result?.Id ?? 0;
                            }
                        }, LazyThreadSafetyMode.ExecutionAndPublication);
                    });
                }

                if (lazyUserId.IsValueCreated)
                {
                    uint userId = lazyUserId.Value;
                    if (userId > 0)
                    {
                        return UserManager.TryGetUserDataByIdAsync(userId);
                    }
                    else
                    {
                        return Task.FromResult<PlayerUserData>(null);
                    }
                }

                return Task.Factory.StartNew(() =>
                {
                    uint userId = lazyUserId.Value;
                    if (userId > 0)
                    {
                        return UserManager.TryGetUserDataByIdAsync(userId);
                    }
                    else
                    {
                        return Task.FromResult<PlayerUserData>(null);
                    }
                }).Unwrap(); //Unwrap should be fine?
            }

            return DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT u.id, u.username, u.permission_rank, u.name_color, u.group_name, u.total_exp, u.bonus_exp, u.hats, u.heads, u.bodys, u.feets, u.current_hat, u.current_hat_color, u.current_head, u.current_head_color, u.current_body, u.current_body_color, u.current_feet, u.current_feet_color, u.speed, u.accel, u.jump, u.last_online, array_remove(array_agg(DISTINCT f.friend_user_id), NULL) AS friends, array_remove(array_agg(DISTINCT i.ignored_user_id), NULL) AS ignored, array_agg(ARRAY[c.level_id, c.finish_time]) AS campaign_runs FROM base.users u LEFT JOIN base.friends f ON u.id = f.user_id LEFT JOIN base.ignored i ON u.id = i.user_id LEFT JOIN base.campaigns_runs c ON c.user_id = u.id WHERE u.username ILIKE {username} GROUP BY u.id LIMIT 1").ContinueWith(UserManager.ParseSqlUserData));
        }

        public static Task<PlayerUserData> TryGetUserDataByEmailAsync(string email)
        {
            if (email == null)
            {
                throw new ArgumentException(nameof(email));
            }

            return DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT u.id, u.username, u.permission_rank, u.name_color, u.group_name, u.total_exp, u.bonus_exp, u.hats, u.heads, u.bodys, u.feets, u.current_hat, u.current_hat_color, u.current_head, u.current_head_color, u.current_body, u.current_body_color, u.current_feet, u.current_feet_color, u.speed, u.accel, u.jump, u.last_online, array_remove(array_agg(DISTINCT f.friend_user_id), NULL) AS friends, array_remove(array_agg(DISTINCT i.ignored_user_id), NULL) AS ignored, array_agg(ARRAY[c.level_id, c.finish_time]) AS campaign_runs FROM base.users u LEFT JOIN base.friends f ON u.id = f.user_id LEFT JOIN base.ignored i ON u.id = i.user_id LEFT JOIN base.campaigns_runs c ON c.user_id = u.id WHERE u.email ILIKE {email} GROUP BY u.id  LIMIT 1").ContinueWith(UserManager.ParseSqlUserData));
        }

        public static Task<PlayerUserData> TryCreateNewUserAsync(string username, string password, string email, IPAddress ip) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"INSERT INTO base.users(username, password, email, register_ip) VALUES({username}, {PasswordUtils.HashPassword(password)}, {email}, {ip}) RETURNING id, username, permission_rank, name_color, group_name, total_exp, bonus_exp, hats, heads, bodys, feets, current_hat, current_hat_color, current_head, current_head_color, current_body, current_body_color, current_feet, current_feet_color, speed, accel, jump, last_online, '{{}}'::integer[] AS friends, '{{}}'::integer[] AS ignored, '{{}}'::integer[] AS campaign_runs").ContinueWith(UserManager.ParseSqlUserData));

        public static Task<uint> TryAuthenicateAsync(string identifier, string password)
        {
            Task<DbDataReader> userDataTask = null;
            DatabaseConnection dbConnection = new DatabaseConnection();

            try
            {
                try
                {
                    new MailAddress(identifier); //TODO: Alternativly we could use regex

                    userDataTask = dbConnection.ReadDataAsync($"SELECT id, password FROM base.users WHERE email ILIKE {identifier} LIMIT 1");
                }
                catch
                {
                    userDataTask = dbConnection.ReadDataAsync($"SELECT id, password FROM base.users WHERE username ILIKE {identifier} LIMIT 1");
                }

                Task<uint> resultTask = userDataTask.ContinueWith((task) =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        DbDataReader reader = task.Result;
                        if (reader?.Read() ?? false)
                        {
                            uint userId = (uint)(int)reader["id"];
                            string dbPassword = (string)reader["password"];
                            if (PasswordUtils.VerifyPassword(password, dbPassword))
                            {
                                return userId;
                            }
#pragma warning disable CS0618 // We know what we are doing, tryna be good guys
                            else if (PasswordUtils.VerifyPasswordLegacy(password, dbPassword))
#pragma warning restore CS0618
                            {
                                //ALERT!!! LEGACY PASSWORD FOUND!!!!! UPDATE PASSWORD!!!!!!!
                                DatabaseConnection.NewAsyncConnection((dbConnection_) => dbConnection_.ExecuteNonQueryAsync($"UPDATE base.users SET password = {PasswordUtils.HashPassword(password)} WHERE id = {userId}"));

                                return userId;
                            }
                        }
                    }
                    else if (task.IsFaulted)
                    {
                        UserManager.Logger.Error("Failed to authenicate user", task.Exception);
                    }

                    return 0u;
                });

                Task.WhenAll(resultTask).ContinueWith((task) => dbConnection.Dispose());

                return resultTask;
            }
            catch
            {
                dbConnection.Dispose();
            }

            return Task.FromResult(0u);
        }

        public static Task UpdateStatsAsync(uint userId, uint speed, uint accel, uint jump)
        {
            //Update to redis
            return RedisConnection.GetDatabase().HashSetAsync($"users:{userId}", new HashEntry[]
            {
                new HashEntry("speed", speed),
                new HashEntry("accel", accel),
                new HashEntry("jump", jump),
            }, CommandFlags.FireAndForget);
        }

        public static Task SetPartsAsync(uint userId, Hat hat, Color hatColor, Part head, Color headColor, Part body, Color bodyColor, Part feet, Color feetColor)
        {
            //Update to redis
            return RedisConnection.GetDatabase().HashSetAsync($"users:{userId}", new HashEntry[]
            {
                new HashEntry("hat", (uint)hat),
                new HashEntry("hatcolor", hatColor.ToArgb()),

                new HashEntry("head", (uint)head),
                new HashEntry("headcolor", headColor.ToArgb()),

                new HashEntry("body", (uint)body),
                new HashEntry("bodycolor", bodyColor.ToArgb()),

                new HashEntry("feet", (uint)feet),
                new HashEntry("feetcolor", feetColor.ToArgb()),
            }, CommandFlags.FireAndForget);
        }

        //Update to sql and then call SetTotalExp on PlayerUserData and then update to redis
        public static Task AddExpAsync(uint userId, ulong addExp, ulong totalExp, uint rank, ulong exp) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"UPDATE base.users SET total_exp = total_exp + {addExp}, rank = {rank}, exp = {exp} WHERE id = {userId} RETURNING id, total_exp").ContinueWith(UserManager.ParseSqlAddExp));

        //Update to sql and then call SetBonusExp on PlayerUserData and then update to redis
        public static Task DrainBonusExp(uint userId, ulong drainBonusExp, ulong bonusExp) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"UPDATE base.users SET bonus_exp = GREATEST(bonus_exp - {drainBonusExp}, 0) WHERE id = {userId} RETURNING id, bonus_exp").ContinueWith(UserManager.ParseSqlDrainBonusExp));

        public static Task AddFriendAsync(uint userId, uint friendId) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ExecuteNonQueryAsync($"INSERT INTO base.friends(user_id, friend_user_id) VALUES({userId}, {friendId}) ON CONFLICT DO NOTHING"));
        public static Task RemoveFriendAsync(uint userId, uint ignoredId) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ExecuteNonQueryAsync($"DELETE FROM base.friends WHERE user_id = {userId} AND friend_user_id = {ignoredId}"));

        public static Task<uint> CountMyFriendsAsync(uint userId) =>  DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT COUNT(user_id) AS friends_count FROM base.friends WHERE user_id = {userId}").ContinueWith(UserManager.ParseSqlMyFriendsCount));
        public static Task<IReadOnlyCollection<PlayerUserData>> GetMyFriendsAsync(uint userId, uint start = 0, uint count = uint.MaxValue) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT u.id, u.username, u.permission_rank, u.name_color, u.group_name, u.total_exp, u.bonus_exp, u.hats, u.heads, u.bodys, u.feets, u.current_hat, u.current_hat_color, u.current_head, u.current_head_color, u.current_body, u.current_body_color, u.current_feet, u.current_feet_color, u.speed, u.accel, u.jump, u.last_online, array_remove(array_agg(DISTINCT f.friend_user_id), NULL) AS friends, array_remove(array_agg(DISTINCT i.ignored_user_id), NULL) AS ignored, array_agg(ARRAY[c.level_id, c.finish_time]) AS campaign_runs FROM base.friends ff JOIN base.users u ON u.id = ff.friend_user_id LEFT JOIN base.friends f ON u.id = f.user_id LEFT JOIN base.ignored i ON u.id = i.user_id LEFT JOIN base.campaigns_runs c ON c.user_id = u.id WHERE ff.user_id = {userId} GROUP BY u.id OFFSET {start} LIMIT {count}").ContinueWith(UserManager.ParseSqlMultipleUserData));

        public static Task AddIgnoredAsync(uint userId, uint friendId) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ExecuteNonQueryAsync($"INSERT INTO base.ignored(user_id, ignored_user_id) VALUES({userId}, {friendId}) ON CONFLICT DO NOTHING"));
        public static Task RemoveIgnoredAsync(uint userId, uint ignoredId) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ExecuteNonQueryAsync($"DELETE FROM base.ignored WHERE user_id = {userId} AND ignored_user_id = {ignoredId}"));

        public static Task<uint> CountMyIgnoredAsync(uint userId) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT COUNT(user_id) AS ignored_count FROM base.ignored WHERE user_id = {userId}").ContinueWith(UserManager.ParseSqlMyIgnoredCount));
        public static Task<IReadOnlyCollection<PlayerUserData>> GetMyIgnoredAsync(uint userId, uint start = 0, uint count = uint.MaxValue) =>  DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT u.id, u.username, u.permission_rank, u.name_color, u.group_name, u.total_exp, u.bonus_exp, u.hats, u.heads, u.bodys, u.feets, u.current_hat, u.current_hat_color, u.current_head, u.current_head_color, u.current_body, u.current_body_color, u.current_feet, u.current_feet_color, u.speed, u.accel, u.jump, u.last_online, array_remove(array_agg(DISTINCT f.friend_user_id), NULL) AS friends, array_remove(array_agg(DISTINCT i.ignored_user_id), NULL) AS ignored, array_agg(ARRAY[c.level_id, c.finish_time]) AS campaign_runs FROM base.ignored ii JOIN base.users u ON u.id = ii.ignored_user_id LEFT JOIN base.friends f ON u.id = f.user_id LEFT JOIN base.ignored i ON u.id = ii.user_id LEFT JOIN base.campaigns_runs c ON c.user_id = u.id WHERE i.user_id = {userId} GROUP BY u.id OFFSET {start} LIMIT {count}").ContinueWith(UserManager.ParseSqlMultipleUserData));

        public static Task<IReadOnlyCollection<PlayerUserData>> SearchUsers(string name) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"SELECT u.id, u.username, u.permission_rank, u.name_color, u.group_name, u.total_exp, u.bonus_exp, u.hats, u.heads, u.bodys, u.feets, u.current_hat, u.current_hat_color, u.current_head, u.current_head_color, u.current_body, u.current_body_color, u.current_feet, u.current_feet_color, u.speed, u.accel, u.jump, u.last_online, array_remove(array_agg(DISTINCT f.friend_user_id), NULL) AS friends, array_remove(array_agg(DISTINCT i.ignored_user_id), NULL) AS ignored, array_agg(ARRAY[c.level_id, c.finish_time]) AS campaign_runs FROM base.users u LEFT JOIN base.friends f ON u.id = f.user_id LEFT JOIN base.ignored i ON u.id = i.user_id LEFT JOIN base.campaigns_runs c ON c.user_id = u.id WHERE u.username ILIKE '%' || {name} || '%' GROUP BY u.id").ContinueWith(UserManager.ParseSqlMultipleUserData));

        public static Task GiveHat(uint userId, Hat hat) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"UPDATE base.users SET hats = ARRAY_APPEND(hats, {(uint)hat}) WHERE id = {userId} AND NOT hats @> '{{{(uint)hat}}}' RETURNING id, hats").ContinueWith(UserManager.ParseSqlAddHat));
        public static Task GiveHead(uint userId, Part part) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"UPDATE base.users SET heads = ARRAY_APPEND(heads, {(uint)part}) WHERE id = {userId} AND NOT heads @> '{{{(uint)part}}}' RETURNING id, heads").ContinueWith(UserManager.ParseSqlAddHead));
        public static Task GiveBody(uint userId, Part part) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"UPDATE base.users SET bodys = ARRAY_APPEND(bodys, {(uint)part}) WHERE id = {userId} AND NOT bodys @> '{{{(uint)part}}}' RETURNING id, bodys").ContinueWith(UserManager.ParseSqlAddBody));
        public static Task GiveFeet(uint userId, Part part) => DatabaseConnection.NewAsyncConnection((dbConnection) => dbConnection.ReadDataAsync($"UPDATE base.users SET feets = ARRAY_APPEND(feets, {(uint)part}) WHERE id = {userId} AND NOT feets @> '{{{(uint)part}}}' RETURNING id, feets").ContinueWith(UserManager.ParseSqlAddFeet));
        public static Task GiveSet(uint userId, Part part) => Task.WhenAll(UserManager.GiveHead(userId, part), UserManager.GiveBody(userId, part), UserManager.GiveFeet(userId, part)); //TODO: More optimized version

        private static PlayerUserData ParseRedisUserData(Task<RedisValue[]> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                RedisValue[] results = task.Result;
                if (results?.All(v => v.HasValue) ?? false)
                {
                    PlayerUserData playerUserData = new PlayerUserData(results);
                    return UserManager.CachePlayerUserData(playerUserData, true);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to load {nameof(PlayerUserData)} from redis", task.Exception);
            }

            return null;
        }

        private static PlayerUserData ParseSqlUserData(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    PlayerUserData playerUserData = new PlayerUserData(reader);
                    return UserManager.CachePlayerUserData(playerUserData);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to load {nameof(PlayerUserData)} from sql", task.Exception);
            }

            return null;
        }

        private static PlayerUserData CachePlayerUserData(PlayerUserData playerUserData, bool loadedFromRedis = false)
        {
            //Push it to redis to keep it up to date
            if (!loadedFromRedis) //We loaded it from redis... there is no point to push data back to there
            {
                RedisConnection.GetDatabase().HashSetAsync($"users:{playerUserData.Id}", playerUserData.ToRedis(), CommandFlags.FireAndForget);
            }

            //Make sure to keep the usernames sync
            RedisConnection.GetDatabase().HashSetAsync($"userids:{Math.Floor(playerUserData.Username.ToUpperInvariant().CountCharsSum() / 100.0)}", new HashEntry[] //TODO: Umh? Also ToUpper due to case sensetivity
            {
                new HashEntry(playerUserData.Username.ToUpperInvariant(), playerUserData.Id)
            }, CommandFlags.FireAndForget);

            UserManager.UserIds.GetOrCreate(playerUserData.Username.ToUpperInvariant(), (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = UserManager.UserIdsCacheTime;

                return new Lazy<uint>(playerUserData.Id);
            });

            Lazy<PlayerUserData> lazy = UserManager.Users.GetOrCreate(playerUserData.Id, (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = UserManager.UserCacheTime;

                return new Lazy<PlayerUserData>(playerUserData);
            });

            if (lazy.IsValueCreated)
            {
                lazy.Value.Merge(playerUserData);
                return lazy.Value;
            }
            else
            {
                return playerUserData;
            }
        }

        private static IReadOnlyCollection<PlayerUserData> ParseSqlMultipleUserData(Task<DbDataReader> task)
        {
            List<PlayerUserData> users = new List<PlayerUserData>();
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                while (reader?.Read() ?? false)
                {
                    PlayerUserData playerUserData = new PlayerUserData(reader);
                    users.Add(UserManager.CachePlayerUserData(playerUserData));
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to load multipl {nameof(PlayerUserData)} from sql", task.Exception);
            }

            return users;
        }
        
        private static uint ParseSqlMyFriendsCount(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    return (uint)(long)reader["friends_count"];
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to load my friends count from sql", task.Exception);
            }

            return 0;
        }

        private static uint ParseSqlMyIgnoredCount(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    return (uint)(long)reader["ignored_count"];
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to load my ignored count from sql", task.Exception);
            }

            return 0;
        }

        private static void ParseSqlAddExp(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    uint userId = (uint)(int)reader["id"];
                    ulong totalExp = (ulong)(long)reader["total_exp"];

                    UserManager.TryGetUserDataByIdAsync(userId).Result?.SetTotalExp(totalExp);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to update total exp to sql", task.Exception);
            }
        }

        private static void ParseSqlDrainBonusExp(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    uint userId = (uint)(int)reader["id"];
                    ulong bonusExp = (ulong)(long)reader["bonus_exp"];

                    UserManager.TryGetUserDataByIdAsync(userId).Result?.SetTotalBonusExp(bonusExp);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to update total exp to sql", task.Exception);
            }
        }

        private static void ParseSqlAddHat(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    uint userId = (uint)(int)reader["id"];
                    IEnumerable<Hat> hats = ((int[])reader["hats"]).Select((h) => (Hat)h);

                    UserManager.TryGetUserDataByIdAsync(userId).Result?.SetHats(hats);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to add user hat to sql", task.Exception);
            }
        }

        private static void ParseSqlAddHead(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    uint userId = (uint)(int)reader["id"];
                    IEnumerable<Part> heads = ((int[])reader["heads"]).Select((h) => (Part)h);

                    UserManager.TryGetUserDataByIdAsync(userId).Result?.SetHeads(heads);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to add user hat to sql", task.Exception);
            }
        }

        private static void ParseSqlAddBody(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    uint userId = (uint)(int)reader["id"];
                    IEnumerable<Part> bodys = ((int[])reader["bodys"]).Select((h) => (Part)h);

                    UserManager.TryGetUserDataByIdAsync(userId).Result?.SetBodys(bodys);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to add user hat to sql", task.Exception);
            }
        }

        private static void ParseSqlAddFeet(Task<DbDataReader> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                DbDataReader reader = task.Result;
                if (reader?.Read() ?? false)
                {
                    uint userId = (uint)(int)reader["id"];
                    IEnumerable<Part> feets = ((int[])reader["feets"]).Select((h) => (Part)h);

                    UserManager.TryGetUserDataByIdAsync(userId).Result?.SetFeets(feets);
                }
            }
            else if (task.IsFaulted)
            {
                UserManager.Logger.Error($"Failed to add user hat to sql", task.Exception);
            }
        }
    }
}
