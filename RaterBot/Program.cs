﻿using System.Diagnostics;
using System.Text;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RaterBot.Database;
using RaterBot.Database.Migrations;
using Serilog;
using Serilog.Core;
using SQLitePCL;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;

namespace RaterBot;

internal sealed class Program
{
    private const int UpdateLimit = 100;
    private const int Timeout = 1800;
    private const string DbDir = "db";

    private const string MessageIdPlusCountPosterIdSql =
        "SELECT Interaction.MessageId, COUNT(*), Interaction.PosterId " +
        "FROM Post INNER JOIN Interaction ON Post.MessageId = Interaction.MessageId " +
        "WHERE Post.ChatId = @ChatId AND Interaction.ChatId = @ChatId AND Post.Timestamp > @TimeAgo AND Interaction.Reaction = true " +
        "GROUP BY Interaction.MessageId;";

    private const string MessageIdMinusCountSql =
        "SELECT Interaction.MessageId, COUNT(*) " +
        "FROM Post " +
        "INNER JOIN Interaction ON Post.MessageId = Interaction.MessageId " +
        "WHERE Post.ChatId = @ChatId AND Interaction.ChatId = @ChatId AND Post.Timestamp > @TimeAgo AND Interaction.Reaction = false " +
        "GROUP BY Interaction.MessageId;";

    private static readonly Logger _logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
    
    private static readonly ITelegramBotClient _botClient = new TelegramBotClient(
        Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_BOT_API") ??
        throw new Exception("TELEGRAM_MEDIA_RATER_BOT_API environment variable not set"));

    private static readonly string _dbPath = Path.Combine(DbDir, "sqlite.db");

    private static readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ConnectionString;
    private static readonly SqliteConnection _dbConnection = new(_connectionString);

    private static readonly string _migrationConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ConnectionString;

    private static readonly InlineKeyboardMarkup _newPostIkm = new(new[]
    {
        new InlineKeyboardButton("👍") { CallbackData = "+" },
        new InlineKeyboardButton("👎") { CallbackData = "-" }
    });

    private static readonly HashSet<char> _shouldBeEscaped = new()
        { '\\', '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

    private static void InitAndMigrateDb()
    {
        if (!Directory.Exists(DbDir))
            Directory.CreateDirectory(DbDir);
        Batteries.Init();

        var serviceProvider = CreateServices();
        using (var scope = serviceProvider.CreateScope())
        {
            MigrateDatabase(scope.ServiceProvider);
        }

        _dbConnection.Execute("PRAGMA synchronous = NORMAL;");
        _dbConnection.Execute("PRAGMA vacuum;");
        _dbConnection.Execute("PRAGMA temp_store = memory;");
    }

    private static async Task Main()
    {
        InitAndMigrateDb();

        var me = await _botClient.GetMeAsync();

        var offset = 0;
        while (true)
            try
            {
                var updates = await _botClient.GetUpdatesAsync(offset, UpdateLimit, Timeout);
                if (!updates.Any())
                    continue;

                // Assumtion is that all images/videos from one MediaGroup will come in a single update
                foreach (var grouping in updates.Where(u => u.Message?.MediaGroupId != null).GroupBy(u => u.Message.MediaGroupId))
                    _ = HandleMediaGroup(grouping.First().Message!);

                foreach (var update in updates.Where(u => u.Message?.MediaGroupId == null))
                    _ = HandleUpdate(me, update);

                offset = updates.Max(u => u.Id) + 1;

                if (offset % 100 == 0) // Optimize sometimes
                    await _dbConnection.ExecuteAsync("PRAGMA optimize;");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "General update exception");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
    }

    private static async Task HandleUpdate(User me, Update update)
    {
        Debug.Assert(me.Username != null);
        try
        {
            if (update.Type == UpdateType.CallbackQuery)
            {
                await HandleCallbackData(update);
                return;
            }

            if (update.Type == UpdateType.Message)
            {
                var msg = update.Message;
                Debug.Assert(msg?.Text != null);

                if (IsBotCommand(me.Username, msg.Text, "/delete"))
                {
                    await HandleDelete(update, me);
                    return;
                }

                if (IsBotCommand(me.Username, msg.Text, "/top_posts_day"))
                {
                    await HandleTopPosts(update, Period.Day);
                    return;
                }

                if (IsBotCommand(me.Username, msg.Text, "/top_posts_week"))
                {
                    await HandleTopPosts(update, Period.Week);
                    return;
                }

                if (IsBotCommand(me.Username, msg.Text, "/top_authors_week"))
                {
                    await HandleTopAuthors(update, Period.Week);
                    return;
                }

                if (IsBotCommand(me.Username, msg.Text, "/top_authors_month"))
                {
                    await HandleTopAuthors(update, Period.Month);
                    return;
                }

                if (IsBotCommand(me.Username, msg.Text, "/text"))
                {
                    if (msg.ReplyToMessage?.From?.Id == me.Id)
                    {
                        var m = await _botClient.SendTextMessageAsync(msg.Chat.Id,
                            "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку не от бота");
                        _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                        _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(msg.ReplyToMessage?.Text))
                    {
                        var m = await _botClient.SendTextMessageAsync(msg.Chat.Id,
                            "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
                        _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                        _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                        return;
                    }

                    await HandleTextReplyAsync(update);
                    return;
                }

                var ttl = FindTikTokLink(msg);
                if (ttl != null)
                {
                    await HandleTikTokAsync(update, ttl);
                    return;
                }

                if (msg.Type is MessageType.Photo or MessageType.Video || (msg.Type == MessageType.Document &&
                                                                           msg.Document?.MimeType != null &&
                                                                           (msg.Document.MimeType.StartsWith("image") ||
                                                                            msg.Document.MimeType.StartsWith("video"))))
                {
                    if (msg.ReplyToMessage != null)
                    {
                        _logger.Information("Reply media messages should be ignored");
                        return;
                    }

                    var caption = msg.Caption?.ToLower();
                    if (!string.IsNullOrWhiteSpace(caption) && (caption.Contains("/skip") || caption.Contains("/ignore") ||
                                                                caption.Contains("#skip") || caption.Contains("#ignore")))
                    {
                        _logger.Information("Media message that should be ignored");
                        return;
                    }

                    Debug.Assert(msg.MediaGroupId == null);
                    await HandleMediaMessage(msg);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"General update exception inside {nameof(HandleUpdate)}");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static Uri? FindTikTokLink(Message msg)
    {
        if (msg.Text is null)
            return null;
        var entities = msg.Entities?.Where(e => e.Type == MessageEntityType.Url).ToArray() ?? Array.Empty<MessageEntity>();
        if (entities.Length == 0)
            return null;

        foreach (var entity in entities)
        {
            var urlText = msg.Text[entity.Offset..(entity.Offset + entity.Length)];
            var url = new Uri(urlText);
            if (url.Host.EndsWith("tiktok.com"))
                return url;
        }

        return null;
    }

    private static async Task HandleDelete(Update update, User bot)
    {
        var msg = update.Message;
        Debug.Assert(msg != null);
        if (msg.ReplyToMessage == null)
        {
            var m = await _botClient.SendTextMessageAsync(msg.Chat.Id,
                "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
            _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
            _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
            return;
        }

        var sqlParams = new { ChatId = msg.Chat.Id, msg.ReplyToMessage.MessageId };

        Debug.Assert(msg.ReplyToMessage.From != null);
        if (msg.ReplyToMessage.From.Id != bot.Id)
        {
            var m = await _botClient.SendTextMessageAsync(msg.Chat.Id, "Эту команду нужно вызывать реплаем на сообщение бота");
            _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
            _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
            return;
        }

        var sql =
            $"SELECT * FROM {nameof(Post)} WHERE {nameof(Post)}.{nameof(Post.ChatId)} = @ChatId AND {nameof(Post)}.{nameof(Post.MessageId)} = @MessageId";
        var post = await _dbConnection.QueryFirstOrDefaultAsync<Post>(sql, sqlParams);
        if (post == null)
        {
            var m = await _botClient.SendTextMessageAsync(msg.Chat.Id, "Это сообщение нельзя удалить");
            _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
            _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
            return;
        }

        Debug.Assert(msg.From != null);
        if (post.PosterId != msg.From.Id)
        {
            var m = await _botClient.SendTextMessageAsync(msg.Chat.Id, "Нельзя удалить чужой пост");
            _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
            _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
            return;
        }

        if (post.Timestamp + TimeSpan.FromHours(1) < DateTime.UtcNow)
        {
            var m = await _botClient.SendTextMessageAsync(msg.Chat.Id, "Этот пост слишком старый, чтобы его удалять");
            _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
            _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
            return;
        }

        await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.ReplyToMessage.MessageId);
        await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
        sql =
            $"DELETE FROM {nameof(Interaction)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} = @MessageId;";
        await _dbConnection.ExecuteAsync(sql, sqlParams);
        sql = $"DELETE FROM {nameof(Post)} WHERE {nameof(Post.ChatId)} = @ChatId AND {nameof(Post.MessageId)} = @MessageId;";
        await _dbConnection.ExecuteAsync(sql, sqlParams);
    }

    private static async Task HandleTopAuthors(Update update, Period period)
    {
        Debug.Assert(update.Message != null);
        var chat = update.Message.Chat;
        var sql = MessageIdPlusCountPosterIdSql;
        var sqlParams = new { TimeAgo = DateTime.UtcNow - PeriodToTimeSpan(period), ChatId = chat.Id };
        var plusQ = await _dbConnection.QueryAsync<(long MessageId, long PlusCount, long PosterId)>(sql, sqlParams);
        var pluses = plusQ.ToList();
        if (!pluses.Any())
        {
            await _botClient.SendTextMessageAsync(chat.Id, $"Не найдено заплюсованных постов за {ForLast(period)}");
            _logger.Information($"{nameof(HandleTopPosts)} - no upvoted posts, skipping");
            return;
        }

        sql = MessageIdMinusCountSql;
        var minus = (await _dbConnection.QueryAsync<(long MessageId, long MinusCount)>(sql, sqlParams)).ToDictionary(x => x.MessageId,
            y => y.MinusCount);

        var topAuthors = pluses.GroupBy(x => x.PosterId).Select(x => new
        {
            x.Key,
            Hindex = x.OrderByDescending(i => i.PlusCount - minus.GetValueOrDefault(i.MessageId)).TakeWhile((z, i) => z.PlusCount >= i + 1)
                .Count(),
            Likes = x.Sum(p => p.PlusCount)
        }).OrderByDescending(x => x.Hindex).ThenByDescending(x => x.Likes).Take(20).ToList();

        var userIds = topAuthors.Select(x => x.Key).Distinct();
        var userIdToUser = await GetTelegramUsers(chat, userIds);

        var message = new StringBuilder(1024);
        message.Append("Топ авторов за ");
        message.Append(ForLast(period));
        message.Append(':');
        message.Append(Environment.NewLine);
        var i = 0;
        foreach (var item in topAuthors)
        {
            AppendPlace(message, i);

            var knownUser = userIdToUser.TryGetValue(item.Key, out var user);
            message.Append(knownUser ? GetFirstLastName(user!) : "покинувший чат пользователь");
            message.Append($" очков: {item.Hindex}, апвоутов: {item.Likes}");

            i++;
        }

        var m = await _botClient.SendTextMessageAsync(chat.Id, message.ToString());
        _ = RemoveAfterSomeTime(chat, update.Message.MessageId);
        _ = RemoveAfterSomeTime(chat, m.MessageId);
    }

    private static async Task HandleTopPosts(Update update, Period period)
    {
        Debug.Assert(update.Message != null);
        var chat = update.Message.Chat;

        if (chat.Type != ChatType.Supergroup && string.IsNullOrWhiteSpace(chat.Username))
        {
            await _botClient.SendTextMessageAsync(chat.Id,
                "Этот чат не является супергруппой и не имеет имени: нет возможности оставлять ссылки на посты");
            _logger.Information($"{nameof(HandleTopPosts)} - unable to link top posts, skipping");
            return;
        }

        var sql = MessageIdPlusCountPosterIdSql;
        var sqlParams = new { TimeAgo = DateTime.UtcNow - PeriodToTimeSpan(period), ChatId = chat.Id };
        var plusQuery = await _dbConnection.QueryAsync<(long MessageId, long PlusCount, long PosterId)>(sql, sqlParams);
        var plusList = plusQuery.ToList();
        var plus = plusList.ToDictionary(x => x.MessageId, x => x.PlusCount);
        var messageIdToUserId = plusList.ToDictionary(x => x.MessageId, x => x.PosterId);
        if (!plus.Any())
        {
            await _botClient.SendTextMessageAsync(chat.Id, $"Не найдено заплюсованных постов за {ForLast(period)}");
            _logger.Information($"{nameof(HandleTopPosts)} - no upvoted posts, skipping");
            return;
        }

        sql = MessageIdMinusCountSql;
        var minus = (await _dbConnection.QueryAsync<(long MessageId, long MinusCount)>(sql, sqlParams)).ToDictionary(x => x.MessageId,
            y => y.MinusCount);

        var keys = plus.Keys.ToList();
        foreach (var key in keys)
            plus[key] -= minus.GetValueOrDefault(key);
        var topPosts = plus.OrderByDescending(x => x.Value).Take(20).ToList();

        var userIds = topPosts.Select(x => messageIdToUserId[x.Key]).Distinct();
        var userIdToUser = await GetTelegramUsers(chat, userIds);

        var message = new StringBuilder(1024);
        message.Append("Топ постов за ");
        message.Append(ForLast(period));
        message.Append(':');
        message.Append(Environment.NewLine);
        var i = 0;
        var sg = chat.Type == ChatType.Supergroup;
        foreach (var item in topPosts)
        {
            AppendPlace(message, i);
            var knownUser = userIdToUser.TryGetValue(messageIdToUserId[item.Key], out var user);

            message.Append("[От ");
            if (knownUser)
                message.Append($"{UserEscaped(user!)}](");
            else
                message.Append("покинувшего чат пользователя](");

            var link = sg ? LinkToSuperGroupMessage(chat, item.Key) : LinkToGroupWithNameMessage(chat, item.Key);
            message.Append(link);
            message.Append(") ");
            if (item.Value > 0)
                message.Append("\\+");
            message.Append(item.Value);
            i++;
        }

        var m = await _botClient.SendTextMessageAsync(chat.Id, message.ToString(), ParseMode.MarkdownV2);
        _ = RemoveAfterSomeTime(chat, m.MessageId);
        _ = RemoveAfterSomeTime(chat, update.Message.MessageId);
    }

    private static async Task<Dictionary<long, User>> GetTelegramUsers(Chat chat, IEnumerable<long> userIds)
    {
        var userIdToUser = new Dictionary<long, User>();
        foreach (var id in userIds)
            try
            {
                var member = await _botClient.GetChatMemberAsync(chat.Id, id);
                userIdToUser.Add(id, member.User);
            }
            catch (ApiRequestException)
            {
                // User not found for any reason, we don't care.
            }

        return userIdToUser;
    }

    private static async Task RemoveAfterSomeTime(Chat chat, int messageId)
    {
        await Task.Delay(TimeSpan.FromMinutes(10));
        await _botClient.DeleteMessageAsync(chat.Id, messageId);
    }

    private static bool IsBotCommand(string username, string? msgText, string command)
    {
        return msgText != null && (msgText == command || msgText == $"{command}@{username}");
    }

    private static void AppendPlace(StringBuilder stringBuilder, int i)
    {
        switch (i)
        {
            case 0:
                stringBuilder.Append("🥇 ");
                break;
            case 1:
                stringBuilder.Append($"{Environment.NewLine}🥈 ");
                break;
            case 2:
                stringBuilder.Append($"{Environment.NewLine}🥉 ");
                break;
            default:
                stringBuilder.Append($"{Environment.NewLine}{i + 1} ");
                break;
        }
    }

    private static string LinkToSuperGroupMessage(Chat chat, long messageId)
    {
        return $"https://t.me/c/{chat.Id.ToString()[4..]}/{messageId}";
    }

    private static string LinkToGroupWithNameMessage(Chat chat, long messageId)
    {
        return $"https://t.me/{chat.Username}/{messageId}";
    }

    private static async Task HandleCallbackData(Update update)
    {
        Debug.Assert(update.CallbackQuery != null);
        var msg = update.CallbackQuery.Message;
        Debug.Assert(msg != null);
        var connection = _dbConnection;
        var chatAndMessageIdParams = new { ChatId = msg.Chat.Id, msg.MessageId };
        var updateData = update.CallbackQuery.Data;
        if (updateData != "-" && updateData != "+")
        {
            _logger.Warning("Invalid callback query data: {Data}", updateData);
            return;
        }

        _logger.Debug("Valid callback request");
        var sql = "SELECT * FROM Post WHERE ChatId = @ChatId AND MessageId = @MessageId;";
        var post = await connection.QuerySingleOrDefaultAsync<Post>(sql, chatAndMessageIdParams);
        if (post == null)
        {
            _logger.Error("Cannot find post in the database, ChatId = {ChatId}, MessageId = {MessageId}", msg.Chat.Id, msg.MessageId);
            try
            {
                await _botClient.EditMessageReplyMarkupAsync(msg.Chat.Id, msg.MessageId, InlineKeyboardMarkup.Empty());
            }
            catch (ApiRequestException e)
            {
                _logger.Warning(e, "Unable to set empty reply markup, trying to delete post");
                await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            }

            sql = "DELETE FROM Interaction WHERE ChatId = @ChatId AND MessageId = @MessageId;";
            await connection.QueryAsync<Interaction>(sql, chatAndMessageIdParams);
            return;
        }

        if (post.PosterId == update.CallbackQuery.From.Id)
        {
            await _botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "Нельзя голосовать за свои посты!");
            return;
        }

        sql = "SELECT * FROM Interaction WHERE ChatId = @ChatId AND MessageId = @MessageId;";
        var interactions = (await connection.QueryAsync<Interaction>(sql, chatAndMessageIdParams)).ToList();
        var interaction = interactions.SingleOrDefault(i => i.UserId == update.CallbackQuery.From.Id);

        var newReaction = updateData == "+";
        if (interaction != null)
        {
            if (newReaction == interaction.Reaction)
            {
                var reaction = newReaction ? "👍" : "👎";
                await _botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, $"Ты уже поставил {reaction} этому посту");
                _logger.Information("No need to update reaction");
                return;
            }

            sql = "UPDATE Interaction SET Reaction = @Reaction WHERE Id = @Id;";
            await connection.ExecuteAsync(sql, new { Reaction = newReaction, interaction.Id });
            interaction.Reaction = newReaction;
        }
        else
        {
            sql =
                "INSERT INTO Interaction (ChatId, UserId, MessageId, Reaction, PosterId) VALUES (@ChatId, @UserId, @MessageId, @Reaction, @PosterId);";
            await connection.ExecuteAsync(sql,
                new
                {
                    Reaction = newReaction, ChatId = msg.Chat.Id, UserId = update.CallbackQuery.From.Id, msg.MessageId, post.PosterId
                });
            interactions.Add(new Interaction { Reaction = newReaction });
        }

        var likes = interactions.Count(i => i.Reaction);
        var dislikes = interactions.Count - likes;

        if (DateTime.UtcNow.AddMinutes(-5) > post.Timestamp && dislikes > 2 * likes + 3)
        {
            _logger.Information("Deleting post. Dislikes = {Dislikes}, Likes = {Likes}", dislikes, likes);
            await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            sql = "DELETE FROM Post WHERE Id = @Id;";
            await _dbConnection.ExecuteAsync(sql, new { post.Id });
            sql = "DELETE FROM Interaction WHERE ChatId = @ChatId AND MessageId = @MessageId;";
            var deletedRows = await _dbConnection.ExecuteAsync(sql, chatAndMessageIdParams);
            _logger.Debug("Deleted {Count} rows from Interaction", deletedRows);
            await _botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "Твой голос стал решающей каплей, этот пост удалён");
            return;
        }

        var plusText = likes > 0 ? $"{likes} 👍" : "👍";
        var minusText = dislikes > 0 ? $"{dislikes} 👎" : "👎";

        var ikm = new InlineKeyboardMarkup(new[]
        {
            new(plusText) { CallbackData = "+" },
            new InlineKeyboardButton(minusText) { CallbackData = "-" }
        });

        try
        {
            await _botClient.EditMessageReplyMarkupAsync(msg.Chat.Id, msg.MessageId, ikm);
            await _botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "EditMessageReplyMarkupAsync");
        }
    }

    private static async Task HandleTikTokAsync(Update update, Uri tiktokLink)
    {
        _logger.Information("New tiktok message");

        var msg = update.Message;
        Debug.Assert(msg != null);
        var from = msg.From;
        Debug.Assert(from != null);
        var msgText = msg.Text;
        Debug.Assert(msgText != null);
        Debug.Assert(tiktokLink != null);

        var processingMsg = await _botClient.SendTextMessageAsync(msg.Chat.Id, "Processing...", replyToMessageId: msg.MessageId);

        var tempFileName = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp4");
        var ok = YtDlpHelper.Download(tiktokLink, tempFileName);
        if (!ok)
        {
            _logger.Information("Could not download the video, check logs");
            await _botClient.DeleteMessageAsync(msg.Chat.Id, processingMsg.MessageId);
            return;
        }

        try
        {
            await using (var stream = File.Open(tempFileName, FileMode.Open, FileAccess.Read))
            {
                var newMessage = await _botClient.SendVideoAsync(
                    msg.Chat.Id,
                    new InputOnlineFile(stream),
                    replyMarkup: _newPostIkm,
                    caption: MentionUsername(from), parseMode: ParseMode.MarkdownV2);
                await InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
            }

            _ = _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);

            for (var retries = 3; retries >= 0; retries--)
            {
                File.Delete(tempFileName);
                if (File.Exists(tempFileName))
                    await Task.Delay(TimeSpan.FromSeconds(1));
                else
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.Warning(e, nameof(HandleTikTokAsync));
        }
        finally
        {
            _ = _botClient.DeleteMessageAsync(msg.Chat.Id, processingMsg.MessageId);
        }
    }

    private static async Task HandleTextReplyAsync(Update update)
    {
        _logger.Information("New valid text message");
        var msg = update.Message;
        Debug.Assert(msg != null);
        var replyTo = msg.ReplyToMessage;
        Debug.Assert(replyTo != null);
        var from = replyTo.From;
        Debug.Assert(from != null);

        var newMessage = await _botClient.SendTextMessageAsync(msg.Chat.Id,
            $"{AtMentionUsername(from)}:{Environment.NewLine}{replyTo.Text}", replyMarkup: _newPostIkm);
        try
        {
            await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
        }
        catch (ApiRequestException are)
        {
            _logger.Warning(are, "Unable to delete message in HandleTextReplyAsync, duplicated update?");
        }

        if (msg.From?.Id == replyTo.From?.Id)
            await _botClient.DeleteMessageAsync(msg.Chat.Id, replyTo.MessageId);

        await InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
    }

    private static async Task InsertIntoPosts(long chatId, long posterId, long messageId)
    {
        const string sql = "INSERT INTO Post (ChatId, PosterId, MessageId, Timestamp) Values (@ChatId, @PosterId, @MessageId, @Timestamp);";
        await _dbConnection.ExecuteAsync(sql,
            new { ChatId = chatId, PosterId = posterId, MessageId = messageId, Timestamp = DateTime.UtcNow });
    }

    private static async Task HandleMediaMessage(Message msg)
    {
        _logger.Information("New valid media message");
        var from = msg.From;
        Debug.Assert(from != null);
        try
        {
            var newMessage = await _botClient.CopyMessageAsync(msg.Chat.Id, msg.Chat.Id, msg.MessageId, replyMarkup: _newPostIkm,
                caption: MentionUsername(from), parseMode: ParseMode.MarkdownV2);
            await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            await InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.Id);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Cannot handle media message");
        }
    }

    private static async Task HandleMediaGroup(Message msg)
    {
        Debug.Assert(msg.MediaGroupId != null);
        _logger.Information("New valid media group");

        var from = msg.From;
        Debug.Assert(from != null);
        try
        {
            var newMessage = await _botClient.SendTextMessageAsync(msg.Chat.Id, "Оценить всю серию", replyToMessageId: msg.MessageId,
                replyMarkup: _newPostIkm);
            await InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Cannot handle media group");
        }
    }

    private static string MentionUsername(User user)
    {
        var whoEscaped = UserEscaped(user);
        return $"[От {whoEscaped}](tg://user?id={user.Id})";
    }

    private static string UserEscaped(User user)
    {
        var who = GetFirstLastName(user);
        var whoEscaped = new StringBuilder(who.Length);
        foreach (var c in who)
        {
            if (_shouldBeEscaped.Contains(c))
                whoEscaped.Append('\\');
            whoEscaped.Append(c);
        }

        return whoEscaped.ToString();
    }

    private static string AtMentionUsername(User user)
    {
        if (string.IsNullOrWhiteSpace(user.Username))
        {
            var who = GetFirstLastName(user);
            return $"От {who} без ника в телеге";
        }

        return $"От @{user.Username}";
    }

    private static string GetFirstLastName(User user)
    {
        var last = user.LastName ?? string.Empty;
        var who = $"{user.FirstName} {last}".Trim();
        if (string.IsNullOrWhiteSpace(who))
            who = "аноним";
        return who;
    }

    private static IServiceProvider CreateServices()
    {
        return new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddSQLite()
                .WithGlobalConnectionString(_migrationConnectionString)
                .ScanIn(typeof(Init).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .BuildServiceProvider(false);
    }

    private static void MigrateDatabase(IServiceProvider serviceProvider)
    {
        var runner = serviceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }

    private static TimeSpan PeriodToTimeSpan(Period period)
    {
        return TimeSpan.FromDays(period switch
        {
            Period.Day => 1,
            Period.Week => 7,
            Period.Month => 30,
            _ => throw new ArgumentException("Enum out of range", nameof(period))
        });
    }

    private static string ForLast(Period period)
    {
        return period switch
        {
            Period.Day => "последний день",
            Period.Week => "последнюю неделю",
            Period.Month => "последний месяц",
            _ => throw new ArgumentException("Enum out of range", nameof(period))
        };
    }

    private enum Period
    {
        Day,
        Week,
        Month
    }
}